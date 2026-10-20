using BrewForge.Application.Common;
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

    /// <summary>
    /// The caller's notifications in the application, latest first, paged.
    /// <c>?unread=true</c> lists those not read yet.
    /// </summary>
    [HttpGet("me/notifications")]
    public Task<PagedResult<NotificationDto>> Inbox([FromQuery] PageQuery paging, [FromQuery] bool? unread,
        CancellationToken cancellationToken) =>
        notifications.MyNotificationsAsync(paging, unread, cancellationToken);

    /// <summary><c>{ count }</c> of the caller's unread notifications, for a badge.</summary>
    [HttpGet("me/notifications/unread-count")]
    public Task<UnreadCountDto> UnreadCount(CancellationToken cancellationToken) =>
        notifications.UnreadCountAsync(cancellationToken);

    [HttpPost("me/notifications/{id:long}/read")]
    public Task<NotificationDto> MarkRead(long id, CancellationToken cancellationToken) =>
        notifications.MarkReadAsync(id, cancellationToken);

    /// <summary>Marks every unread notification of the caller read: <c>{ count }</c> of those it marked.</summary>
    [HttpPost("me/notifications/read-all")]
    public Task<UnreadCountDto> MarkAllRead(CancellationToken cancellationToken) =>
        notifications.MarkAllReadAsync(cancellationToken);

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
