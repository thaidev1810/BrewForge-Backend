using BrewForge.Application.Notifications;
using Microsoft.Extensions.Options;

namespace BrewForge.Api.Scheduling;

/// <summary>
/// Delivers the notifications that are queued, by e-mail and Web Push, on a
/// timer. A notification is queued in the transaction of the change it
/// reports, and sent from here afterwards: a mail server that is slow or
/// down never holds up or fails the request that caused the notification.
/// One instance of the API should run this; two would both send.
/// </summary>
public sealed class NotificationDispatcher(IServiceScopeFactory scopes, IOptions<SchedulerOptions> options,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.NotificationIntervalSeconds;
        if (seconds <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                // Keep going while there is a full batch waiting.
                while ((await RunOnceAsync(stoppingToken)).Processed > 0 && !stoppingToken.IsCancellationRequested)
                {
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One failed run must not stop the runs after it.
                logger.LogError(exception, "Delivering notifications failed; it is tried again at the next run");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<DispatchResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationService>().DispatchPendingAsync(cancellationToken);
    }
}
