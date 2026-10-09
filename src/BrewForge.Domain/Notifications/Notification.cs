using BrewForge.Domain.Common;

namespace BrewForge.Domain.Notifications;

/// <summary>What became of a notification on one channel.</summary>
public enum DeliveryStatus
{
    /// <summary>Not tried yet, or tried and to be tried again.</summary>
    Pending,
    Sent,
    /// <summary>Tried as often as is allowed and given up.</summary>
    Failed,
    /// <summary>Nothing to send to: the channel is not configured, or the user cannot be reached on it.</summary>
    Skipped,
}

/// <summary>
/// One message for one user, and what became of it on each of the two
/// channels it is delivered by: e-mail and Web Push. The message itself is
/// also an entry of the audit log (NOTIFY); this row is what the dispatcher
/// works from.
/// </summary>
public sealed class Notification
{
    /// <summary>How often delivery is tried before a channel that keeps failing is given up.</summary>
    public const int MaxAttempts = 5;

    private Notification() { }

    public Notification(long userId, string subject, string message, DateTimeOffset now)
    {
        UserId = userId;
        Subject = Shorten(subject, 160);
        Message = Shorten(message, 1000);
        CreatedAt = now;
    }

    public long Id { get; private set; }
    public long UserId { get; private set; }
    public string Subject { get; private set; } = null!;
    public string Message { get; private set; } = null!;
    public DeliveryStatus EmailStatus { get; private set; } = DeliveryStatus.Pending;
    public DeliveryStatus PushStatus { get; private set; } = DeliveryStatus.Pending;
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set once neither channel has anything left to try.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    public bool IsProcessed => ProcessedAt is not null;

    /// <summary>
    /// Records one run of the dispatcher over this notification. A channel
    /// that is already settled keeps its status: what was sent is not sent
    /// again because the other channel failed. A channel that failed stays
    /// PENDING and is tried again, until the attempts run out.
    /// </summary>
    public void RecordAttempt(DeliveryOutcome email, DeliveryOutcome push, DateTimeOffset now)
    {
        if (IsProcessed) return;

        Attempts++;
        var lastTry = Attempts >= MaxAttempts;
        EmailStatus = Settle(EmailStatus, email, lastTry);
        PushStatus = Settle(PushStatus, push, lastTry);

        var error = new[] { email.Error, push.Error }.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        if (error.Count > 0) LastError = Shorten(string.Join(" | ", error), 500);
        if (EmailStatus != DeliveryStatus.Pending && PushStatus != DeliveryStatus.Pending) ProcessedAt = now;
    }

    private static DeliveryStatus Settle(DeliveryStatus current, DeliveryOutcome outcome, bool lastTry) =>
        current != DeliveryStatus.Pending ? current
        : outcome.Status != DeliveryStatus.Failed ? outcome.Status
        : lastTry ? DeliveryStatus.Failed
        : DeliveryStatus.Pending;

    private static string Shorten(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>What one try on one channel came to.</summary>
public sealed record DeliveryOutcome(DeliveryStatus Status, string? Error = null)
{
    public static readonly DeliveryOutcome Sent = new(DeliveryStatus.Sent);
    public static readonly DeliveryOutcome Skipped = new(DeliveryStatus.Skipped);

    /// <summary>Left as it is: the channel was settled on an earlier run.</summary>
    public static readonly DeliveryOutcome Untouched = new(DeliveryStatus.Pending);

    public static DeliveryOutcome Failed(string error) => new(DeliveryStatus.Failed, error);
}

/// <summary>
/// A browser of a user that agreed to receive Web Push messages: the
/// endpoint of its push service and the two keys a message to it is
/// encrypted with (RFC 8291).
/// </summary>
public sealed class PushSubscription
{
    private PushSubscription() { }

    public long Id { get; private set; }
    public long UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string P256dh { get; private set; } = null!;
    public string Auth { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static PushSubscription Create(long userId, string? endpoint, string? p256dh, string? auth,
        DateTimeOffset now)
    {
        endpoint = endpoint?.Trim() ?? "";
        p256dh = p256dh?.Trim() ?? "";
        auth = auth?.Trim() ?? "";
        new FieldErrors()
            .Check(endpoint.Length <= 1000 && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps, "endpoint", "must be the https address of a push service")
            .Check(IsKey(p256dh, bytes: 65), "keys.p256dh", "must be a P-256 public key in base64url")
            .Check(IsKey(auth, bytes: 16), "keys.auth", "must be a 16-byte secret in base64url")
            .ThrowIfAny();

        return new PushSubscription { UserId = userId, Endpoint = endpoint, P256dh = p256dh, Auth = auth, CreatedAt = now };
    }

    /// <summary>A browser belongs to whoever is signed in on it: subscribing again hands it over.</summary>
    public void Reassign(long userId, string p256dh, string auth)
    {
        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
    }

    private static bool IsKey(string value, int bytes)
    {
        // IsValid first: decoding throws on a character that is not base64url.
        if (value.Length is 0 or > 128 || !System.Buffers.Text.Base64Url.IsValid(value)) return false;
        var buffer = new byte[128];
        return System.Buffers.Text.Base64Url.TryDecodeFromChars(value, buffer, out var written) && written == bytes;
    }
}
