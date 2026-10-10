using BrewForge.Application.Training;
using Microsoft.Extensions.Options;

namespace BrewForge.Api.Scheduling;

/// <summary>
/// A due date belongs to the calendar: a learner is reminded shortly before
/// it, and a course that is past it is reported, whether or not anybody
/// opens the training dashboard that day. Each is said once per enrolment,
/// so running this often costs nothing.
/// </summary>
public sealed class TrainingReminderScheduler(IServiceScopeFactory scopes, IOptions<SchedulerOptions> options,
    ILogger<TrainingReminderScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ReminderIntervalMinutes;
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
                logger.LogError(exception, "Sending training reminders failed; it is tried again at the next run");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<ReminderResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LearningPathService>().RemindAsync(cancellationToken);
    }
}
