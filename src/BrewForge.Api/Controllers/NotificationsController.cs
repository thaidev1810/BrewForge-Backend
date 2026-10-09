using BrewForge.Application.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// Web Push for the caller, whatever their role. Not in the API contract:
/// the pack records notifications and delivers none.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class NotificationsController(NotificationService notifications) : ControllerBase
{
    /// <summary>
    /// <c>{ enabled, publicKey }</c>: the VAPID public key a browser passes
    /// as <c>applicationServerKey</c> when it subscribes. Not enabled means
    /// the server has no keys, and there is nothing to subscribe to.
    /// </summary>
    [HttpGet("push/config")]
    public PushConfigDto PushConfig() => notifications.PushConfig();

    [HttpGet("me/push-subscriptions")]
    public Task<IReadOnlyList<PushSubscriptionDto>> Mine(CancellationToken cancellationToken) =>
        notifications.MySubscriptionsAsync(cancellationToken);

    /// <summary>
    /// Registers this browser: the body is the JSON of its <c>PushSubscription</c>,
    /// <c>{ endpoint, keys: { p256dh, auth } }</c>. Subscribing a browser that
    /// is already known hands it over to the caller.
    /// </summary>
    [HttpPost("me/push-subscriptions")]
    public async Task<ActionResult<PushSubscriptionDto>> Subscribe(PushSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var subscription = await notifications.SubscribeAsync(request, cancellationToken);
        return Created("/api/v1/me/push-subscriptions", subscription);
    }

    /// <summary>Forgets a browser of the caller: <c>?endpoint=</c> the endpoint it subscribed with.</summary>
    [HttpDelete("me/push-subscriptions")]
    public async Task<IActionResult> Unsubscribe([FromQuery] string? endpoint, CancellationToken cancellationToken)
    {
        await notifications.UnsubscribeAsync(endpoint, cancellationToken);
        return NoContent();
    }
}
