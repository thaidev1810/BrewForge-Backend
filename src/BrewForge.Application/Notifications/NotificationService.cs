using System.Text.Json;
using System.Text.Json.Nodes;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BrewForge.Application.Notifications;

public static class NotificationExtensions
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Notifies a user. The message is recorded in the audit log (NOTIFY,
    /// with <paramref name="context"/> beside it) and queued for delivery by
    /// e-mail and Web Push, both in the same transaction as the next save:
    /// a notification never exists for a change that was not stored.
    /// </summary>
    public static void Notify(this IBrewForgeDbContext db, long userId, string subject, string message,
        DateTimeOffset now, object? context = null)
    {
        var payload = new JsonObject { ["subject"] = subject, ["message"] = message };
        if (context is not null && JsonSerializer.SerializeToNode(context, Web) is JsonObject extra)
        {
            foreach (var (key, value) in extra.ToList()) payload[key] = value?.DeepClone();
        }

        db.Audit(AuditEntities.User, () => userId, AuditActions.Notify, payload);
        db.Notifications.Add(new Notification(userId, subject, message, now));
    }
}

/// <summary>What a browser sends to subscribe: the JSON of its <c>PushSubscription</c>.</summary>
public sealed record PushSubscriptionRequest(string? Endpoint, PushSubscriptionKeys? Keys);

public sealed record PushSubscriptionKeys(string? P256dh, string? Auth);

public sealed record PushSubscriptionDto(long Id, string Endpoint, DateTimeOffset CreatedAt);

/// <summary>What a browser needs before it can subscribe. <c>PublicKey</c> is null while push is not configured.</summary>
public sealed record PushConfigDto(bool Enabled, string? PublicKey);

/// <summary>How many notifications one run of the dispatcher handled.</summary>
public sealed record DispatchResult(int Processed, int Emails, int Pushes);

/// <summary>
/// Web Push subscriptions of the caller, and the delivery of queued
/// notifications by e-mail and Web Push.
/// </summary>
public sealed class NotificationService(IBrewForgeDbContext db, IEmailSender email, IPushSender push,
    ICurrentUser currentUser, TimeProvider clock, ILogger<NotificationService> logger)
{
    private const int BatchSize = 50;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public PushConfigDto PushConfig() => new(push.IsConfigured, push.PublicKey);

    public async Task<IReadOnlyList<PushSubscriptionDto>> MySubscriptionsAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        return await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Id)
            .Select(s => new PushSubscriptionDto(s.Id, s.Endpoint, s.CreatedAt)).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Registers the browser the caller is signed in on. Subscribing a
    /// browser that is already known hands it over to the caller: a browser
    /// receives the notifications of one user, the one who uses it now.
    /// </summary>
    public async Task<PushSubscriptionDto> SubscribeAsync(PushSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var subscription = PushSubscription.Create(userId, request.Endpoint, request.Keys?.P256dh, request.Keys?.Auth,
            clock.GetUtcNow());

        var known = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == subscription.Endpoint,
            cancellationToken);
        if (known is null) db.PushSubscriptions.Add(subscription);
        else known.Reassign(userId, subscription.P256dh, subscription.Auth);

        await db.SaveChangesAsync(cancellationToken);
        var stored = known ?? subscription;
        return new PushSubscriptionDto(stored.Id, stored.Endpoint, stored.CreatedAt);
    }

    /// <summary>Forgets a browser of the caller. Somebody else's does not exist for them.</summary>
    public async Task UnsubscribeAsync(string? endpoint, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        endpoint = endpoint?.Trim();
        new FieldErrors().Required("endpoint", endpoint).ThrowIfAny();

        var subscription = await db.PushSubscriptions
                               .SingleOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == userId, cancellationToken)
                           ?? throw DomainException.NotFound("Push subscription", endpoint!);
        db.PushSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Delivers the notifications that are waiting, oldest first, a batch at
    /// a time. A channel that is not configured, a user who has left or has
    /// no browser subscribed, is skipped, not failed. A failure is tried
    /// again on a later run. A browser its push service no longer knows is
    /// forgotten.
    /// </summary>
    public async Task<DispatchResult> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await db.Notifications.Where(n => n.ProcessedAt == null).OrderBy(n => n.Id).Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return new DispatchResult(0, 0, 0);

        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var browsers = (await db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)).ToListAsync(cancellationToken))
            .ToLookup(s => s.UserId);
        var gone = new HashSet<PushSubscription>();

        var (emails, pushes) = (0, 0);
        foreach (var notification in pending)
        {
            var user = users.GetValueOrDefault(notification.UserId);
            var reachable = user is { Status: UserStatus.Active };

            var byEmail = notification.EmailStatus != DeliveryStatus.Pending ? DeliveryOutcome.Untouched
                : !reachable || !email.IsConfigured ? DeliveryOutcome.Skipped
                : await SendEmailAsync(user!, notification, cancellationToken);
            var byPush = notification.PushStatus != DeliveryStatus.Pending ? DeliveryOutcome.Untouched
                : !reachable || !push.IsConfigured ? DeliveryOutcome.Skipped
                : await SendPushAsync(browsers[notification.UserId].Where(b => !gone.Contains(b)).ToList(), notification,
                    gone, cancellationToken);

            notification.RecordAttempt(byEmail, byPush, clock.GetUtcNow());
            if (byEmail.Status == DeliveryStatus.Sent) emails++;
            if (byPush.Status == DeliveryStatus.Sent) pushes++;
        }

        db.PushSubscriptions.RemoveRange(gone);
        await db.SaveChangesAsync(cancellationToken);
        return new DispatchResult(pending.Count, emails, pushes);
    }

    private async Task<DeliveryOutcome> SendEmailAsync(AppUser user, Notification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            await email.SendAsync(user.Email, user.FullName, notification.Subject, notification.Message, cancellationToken);
            return DeliveryOutcome.Sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "E-mail of notification {NotificationId} was not sent", notification.Id);
            return DeliveryOutcome.Failed($"email: {exception.Message}");
        }
    }

    /// <summary>Sent when at least one browser of the user took it.</summary>
    private async Task<DeliveryOutcome> SendPushAsync(IReadOnlyList<PushSubscription> targets, Notification notification,
        HashSet<PushSubscription> gone, CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return DeliveryOutcome.Skipped;

        var payload = JsonSerializer.Serialize(new { title = notification.Subject, body = notification.Message }, Web);
        var (delivered, failed) = (0, 0);
        string? error = null;
        foreach (var target in targets)
        {
            try
            {
                var result = await push.SendAsync(new PushTarget(target.Endpoint, target.P256dh, target.Auth), payload,
                    cancellationToken);
                if (result == PushResult.Delivered) delivered++;
                else if (result == PushResult.Gone) gone.Add(target);
                else failed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Push of notification {NotificationId} was not sent", notification.Id);
                failed++;
                error = exception.Message;
            }
        }

        return delivered > 0 ? DeliveryOutcome.Sent
            : failed > 0 ? DeliveryOutcome.Failed($"push: {error ?? "the push service refused the message"}")
            // Every browser of the user turned out to be gone: nothing is left to send to.
            : DeliveryOutcome.Skipped;
    }
}
