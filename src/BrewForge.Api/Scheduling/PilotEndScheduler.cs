using BrewForge.Application.Launch;
using Microsoft.Extensions.Options;

namespace BrewForge.Api.Scheduling;

public sealed class SchedulerOptions
{
    public const string Section = "Scheduler";

    /// <summary>How often pilots whose last day has passed are ended. Zero or less turns the scheduler off.</summary>
    public int PilotEndIntervalMinutes { get; set; } = 15;

    /// <summary>How often queued notifications are delivered. Zero or less turns the dispatcher off.</summary>
    public int NotificationIntervalSeconds { get; set; } = 30;

    /// <summary>How often learners are reminded of courses due soon and overdue ones are reported. Zero or less turns it off.</summary>
    public int ReminderIntervalMinutes { get; set; } = 60;

}

/// <summary>
/// RUNNING to ENDED belongs to the calendar: a pilot ends the day after its
/// <c>end_date</c> whether or not anybody looks at it. The transition is the
/// one <see cref="PilotService.EndDueAsync"/> also makes on demand, so a
/// request that arrives before the next run still sees the pilot ended. It is
/// audited as a system action: there is no caller.
/// </summary>
public sealed class PilotEndScheduler(IServiceScopeFactory scopes, IOptions<SchedulerOptions> options,
    ILogger<PilotEndScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.PilotEndIntervalMinutes;
        if (minutes <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One failed run must not stop the runs after it.
                logger.LogError(exception, "Ending the pilots that are due failed; it is tried again at the next run");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PilotService>().EndDueAsync(cancellationToken);
    }
}
