namespace BrewForge.Application.Abstractions;

/// <summary>
/// Sends an e-mail. Not configured means there is no mail server to send
/// through: notifications are then skipped on this channel, and everything
/// else works.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>A browser a push message is sent to.</summary>
public sealed record PushTarget(string Endpoint, string P256dh, string Auth);

public enum PushResult
{
    Delivered,

    /// <summary>The push service says the subscription no longer exists. It is to be forgotten.</summary>
    Gone,
    Failed,
}

/// <summary>Sends a Web Push message. Not configured means there are no VAPID keys to sign with.</summary>
public interface IPushSender
{
    bool IsConfigured { get; }

    /// <summary>The key a browser subscribes with, in base64url. Null when the channel is not configured.</summary>
    string? PublicKey { get; }

    Task<PushResult> SendAsync(PushTarget target, string payloadJson, CancellationToken cancellationToken);
}
