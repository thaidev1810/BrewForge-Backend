using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BrewForge.Api.Scheduling;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Notifications;
using BrewForge.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrewForge.Api.Tests.Notifications;

/// <summary>
/// Notifications are queued with the change they report and delivered
/// afterwards by e-mail and Web Push. The mail server and the push services
/// are fakes; the dispatcher is run by the tests, not by its timer.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationApiTests(BrewForgeApiFactory factory)
{
    private const string Subscriptions = "/api/v1/me/push-subscriptions";

    private NotificationDispatcher Dispatcher => factory.Services.GetRequiredService<NotificationDispatcher>();

    [Fact]
    public async Task Notification_is_queued_with_the_change_and_delivered_by_email_and_web_push()
    {
        await StartCleanAsync();
        var browser = await SubscribeAsync(TestUsers.Trainee);

        var notification = await NotifyTraineeAsync();

        // Queued by the request, and not sent by it: nothing has left yet.
        Assert.Equal((DeliveryStatus.Pending, DeliveryStatus.Pending, false),
            (notification.EmailStatus, notification.PushStatus, notification.IsProcessed));
        Assert.Empty(factory.Email.Sent);
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "AppUser" && a.EntityId == notification.UserId && a.Action == "NOTIFY")));

        var run = await Dispatcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal((1, 1, 1), (run.Processed, run.Emails, run.Pushes));
        var mail = Assert.Single(factory.Email.Sent);
        var trainee = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Username == TestUsers.Trainee));
        Assert.Equal((trainee.Email, trainee.FullName, "Course assigned"), (mail.ToAddress, mail.ToName, mail.Subject));
        Assert.Contains("You have been enrolled on", mail.Body);

        var push = Assert.Single(factory.Push.Sent);
        Assert.Equal(browser, push.Target.Endpoint);
        var payload = JsonDocument.Parse(push.PayloadJson).RootElement;
        Assert.Equal(("Course assigned", mail.Body), (payload.GetProperty("title").GetString(), payload.GetProperty("body").GetString()));

        var delivered = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Sent, DeliveryStatus.Sent, 1, true),
            (delivered.EmailStatus, delivered.PushStatus, delivered.Attempts, delivered.IsProcessed));

        // Delivered once: the next run finds nothing to do.
        Assert.Equal(0, (await Dispatcher.RunOnceAsync(CancellationToken.None)).Processed);
        Assert.Single(factory.Email.Sent);
        Assert.Single(factory.Push.Sent);
    }

    [Fact]
    public async Task Channel_that_is_not_configured_is_skipped_and_holds_nothing_up()
    {
        await StartCleanAsync();
        await SubscribeAsync(TestUsers.Trainee);
        (factory.Email.IsConfigured, factory.Push.IsConfigured) = (false, false);

        var notification = await NotifyTraineeAsync();
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var skipped = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Skipped, DeliveryStatus.Skipped, true), (skipped.EmailStatus, skipped.PushStatus, skipped.IsProcessed));
        Assert.Empty(factory.Email.Sent);
        Assert.Empty(factory.Push.Sent);
    }

    [Fact]
    public async Task User_without_a_subscribed_browser_gets_the_email_and_the_push_is_skipped()
    {
        await StartCleanAsync();

        var notification = await NotifyTraineeAsync();
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var delivered = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Sent, DeliveryStatus.Skipped, true), (delivered.EmailStatus, delivered.PushStatus, delivered.IsProcessed));
        Assert.Single(factory.Email.Sent);
        Assert.Empty(factory.Push.Sent);
    }

    [Fact]
    public async Task Failed_email_is_tried_again_without_sending_the_push_twice_and_is_given_up_after_five_attempts()
    {
        await StartCleanAsync();
        await SubscribeAsync(TestUsers.Trainee);
        factory.Email.Down = true;

        var notification = await NotifyTraineeAsync();
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        // The push went; the e-mail waits for the next run.
        var waiting = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Pending, DeliveryStatus.Sent, 1, false),
            (waiting.EmailStatus, waiting.PushStatus, waiting.Attempts, waiting.IsProcessed));
        Assert.Contains("email", waiting.LastError);

        for (var attempt = 2; attempt <= Notification.MaxAttempts; attempt++)
        {
            await Dispatcher.RunOnceAsync(CancellationToken.None);
        }

        var givenUp = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Failed, DeliveryStatus.Sent, Notification.MaxAttempts, true),
            (givenUp.EmailStatus, givenUp.PushStatus, givenUp.Attempts, givenUp.IsProcessed));
        Assert.Single(factory.Push.Sent);
        Assert.Equal(0, (await Dispatcher.RunOnceAsync(CancellationToken.None)).Processed);
    }

    [Fact]
    public async Task Email_that_failed_once_is_sent_when_the_mail_server_is_back()
    {
        await StartCleanAsync();
        factory.Email.Down = true;
        var notification = await NotifyTraineeAsync();
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        factory.Email.Down = false;
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        var delivered = await ReloadAsync(notification);
        Assert.Equal((DeliveryStatus.Sent, 2, true), (delivered.EmailStatus, delivered.Attempts, delivered.IsProcessed));
        Assert.Single(factory.Email.Sent);
    }

    [Fact]
    public async Task Browser_its_push_service_no_longer_knows_is_forgotten()
    {
        await StartCleanAsync();
        var gone = await SubscribeAsync(TestUsers.Trainee);
        var alive = await SubscribeAsync(TestUsers.Trainee);
        factory.Push.Answers[gone] = PushResult.Gone;

        var notification = await NotifyTraineeAsync();
        await Dispatcher.RunOnceAsync(CancellationToken.None);

        // The browser that is still there got it, and the other one is not asked again.
        Assert.Equal(alive, Assert.Single(factory.Push.Sent).Target.Endpoint);
        Assert.Equal(DeliveryStatus.Sent, (await ReloadAsync(notification)).PushStatus);
        Assert.Equal([alive], await factory.WithDbAsync(db => db.PushSubscriptions.Select(s => s.Endpoint).ToListAsync()));
    }

    [Fact]
    public async Task Browser_is_subscribed_listed_handed_over_and_forgotten()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var colleague = await factory.ClientForAsync("trainee2");
        var browser = Browser();
        var endpoint = browser.Endpoint;

        var created = await (await trainee.PostAsJsonAsync(Subscriptions, browser.Body)).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(endpoint, created.GetProperty("endpoint").GetString());
        Assert.Contains(endpoint, await EndpointsAsync(trainee));

        // Subscribing the same browser again changes nothing; it is one browser.
        var again = await (await trainee.PostAsJsonAsync(Subscriptions, browser.Body)).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(created.Id(), again.Id());

        // Nobody sees or removes the browser of somebody else.
        Assert.DoesNotContain(endpoint, await EndpointsAsync(colleague));
        await (await colleague.DeleteAsync($"{Subscriptions}?endpoint={Uri.EscapeDataString(endpoint)}"))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // A colleague who signs in on that browser takes it over: it is theirs now.
        await (await colleague.PostAsJsonAsync(Subscriptions, browser.Body)).ShouldBeAsync(HttpStatusCode.Created);
        Assert.DoesNotContain(endpoint, await EndpointsAsync(trainee));
        Assert.Contains(endpoint, await EndpointsAsync(colleague));

        using var removed = await colleague.DeleteAsync($"{Subscriptions}?endpoint={Uri.EscapeDataString(endpoint)}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain(endpoint, await EndpointsAsync(colleague));
    }

    [Fact]
    public async Task Subscription_must_be_the_one_a_browser_makes()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var browser = Browser();
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(65));
        var auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

        var notHttps = await (await trainee.PostAsJsonAsync(Subscriptions,
            new { endpoint = "http://push.example.test/x", keys = new { p256dh = key, auth } })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var shortKey = await (await trainee.PostAsJsonAsync(Subscriptions,
            new { endpoint = browser.Endpoint, keys = new { p256dh = "abc", auth } })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noKeys = await (await trainee.PostAsJsonAsync(Subscriptions, new { endpoint = browser.Endpoint }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noEndpoint = await (await trainee.DeleteAsync(Subscriptions)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("endpoint", notHttps.DetailFields());
        Assert.Contains("keys.p256dh", shortKey.DetailFields());
        Assert.Contains("keys.auth", noKeys.DetailFields());
        Assert.Contains("endpoint", noEndpoint.DetailFields());
    }

    [Fact]
    public async Task Push_config_tells_a_browser_the_key_to_subscribe_with_or_that_there_is_none()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        factory.Push.Reset();

        var enabled = await (await trainee.GetAsync("/api/v1/push/config")).ShouldBeAsync(HttpStatusCode.OK);
        factory.Push.IsConfigured = false;
        var disabled = await (await trainee.GetAsync("/api/v1/push/config")).ShouldBeAsync(HttpStatusCode.OK);
        factory.Push.Reset();

        Assert.Equal((true, FakePushSender.Key), (enabled.GetProperty("enabled").GetBoolean(), enabled.GetProperty("publicKey").GetString()));
        Assert.Equal((false, JsonValueKind.Null), (disabled.GetProperty("enabled").GetBoolean(), disabled.GetProperty("publicKey").ValueKind));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Settles whatever earlier tests left in the queue, forgets the browsers
    /// they subscribed, and puts the fakes back to working order.
    /// </summary>
    private async Task StartCleanAsync()
    {
        await factory.WithDbAsync(db => db.PushSubscriptions.ExecuteDeleteAsync());
        factory.Email.Reset();
        factory.Push.Reset();
        while ((await Dispatcher.RunOnceAsync(CancellationToken.None)).Processed > 0)
        {
        }
        factory.Email.Reset();
        factory.Push.Reset();
    }

    /// <summary>The seeded trainee is enrolled on a class, which notifies them. Returns that notification.</summary>
    private async Task<Notification> NotifyTraineeAsync()
    {
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [traineeId]);
        return await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.UserId == traineeId && n.Subject == "Course assigned").OrderByDescending(n => n.Id).FirstAsync());
    }

    private Task<Notification> ReloadAsync(Notification notification) =>
        factory.WithDbAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.Id == notification.Id));

    private async Task<string> SubscribeAsync(string username)
    {
        using var client = await factory.ClientForAsync(username);
        var browser = Browser();
        await (await client.PostAsJsonAsync(Subscriptions, browser.Body)).ShouldBeAsync(HttpStatusCode.Created);
        return browser.Endpoint;
    }

    private static async Task<List<string?>> EndpointsAsync(HttpClient client) =>
    [
        .. (await (await client.GetAsync(Subscriptions)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()
            .Select(s => s.GetProperty("endpoint").GetString()),
    ];

    /// <summary>What a browser sends when it subscribes: a new endpoint and a key pair of its own.</summary>
    private static (string Endpoint, object Body) Browser()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = $"https://push.example.test/send/{Guid.NewGuid():N}";
        return (endpoint, new
        {
            endpoint,
            keys = new
            {
                p256dh = Base64Url.EncodeToString(WebPushEncryption.ExportPoint(key.ExportParameters(includePrivateParameters: false))),
                auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            },
        });
    }
}
