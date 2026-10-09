using BrewForge.Domain.Courses;

namespace BrewForge.Domain.Training;

/// <summary>
/// How one module fared on first attempts at the quiz. <c>Highlighted</c>
/// marks a module whose pass rate is markedly below that of the others: the
/// signal that it is the module, not the trainees, that needs attention.
/// </summary>
public sealed record ModuleEffectiveness(long CourseModuleId, ModuleType ModuleType, int FirstAttempts, int Passed,
    decimal? PassRatePct, bool Highlighted);

/// <summary>
/// UC-31: what the quiz attempts say about a course. This is what tagging
/// every question with its module is for (BR-35).
/// </summary>
public static class CourseEffectiveness
{
    /// <summary>How far below the average of the other modules a pass rate must lie to be highlighted, in points.</summary>
    public const decimal MarkedlyBelowPoints = 20m;

    /// <summary>
    /// The first-attempt pass rate per module. A trainee passes a module on
    /// an attempt by answering every one of its questions correctly.
    /// </summary>
    /// <param name="firstAttempts">The first quiz attempt of each enrolment, as its breakdown per module.</param>
    public static IReadOnlyList<ModuleEffectiveness> PerModule(IEnumerable<IReadOnlyList<ModuleScore>> firstAttempts)
    {
        var rates = firstAttempts.SelectMany(sheet => sheet)
            .GroupBy(score => (score.CourseModuleId, score.ModuleType))
            .Select(group => (group.Key.CourseModuleId, group.Key.ModuleType, Attempts: group.Count(),
                Passed: group.Count(score => !score.Failed)))
            .Select(module => (module.CourseModuleId, module.ModuleType, module.Attempts, module.Passed,
                Rate: Percent(module.Passed, module.Attempts)))
            .OrderBy(module => module.ModuleType)
            .ToList();

        return
        [
            .. rates.Select(module =>
            {
                var others = rates.Where(other => other.CourseModuleId != module.CourseModuleId && other.Rate is not null)
                    .Select(other => other.Rate!.Value).ToList();
                var markedlyBelow = module.Rate is { } rate && others.Count > 0
                                    && others.Average() - rate >= MarkedlyBelowPoints;
                return new ModuleEffectiveness(module.CourseModuleId, module.ModuleType, module.Attempts, module.Passed,
                    module.Rate, markedlyBelow);
            }),
        ];
    }

    /// <summary>A share in percent to one decimal; null when there is nothing to take a share of.</summary>
    public static decimal? Percent(int part, int whole) =>
        whole <= 0 ? null : Math.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The on-time completion rate: of the enrolments whose deadline can be
    /// judged, those certified on or before it. An enrolment can be judged
    /// once it has been passed or its due date has gone by.
    /// </summary>
    public static decimal? OnTimeRate(IEnumerable<(EnrollmentState State, DateOnly? DueDate, DateTimeOffset? CompletedAt)> enrollments,
        DateOnly today)
    {
        var judged = enrollments
            .Where(e => e.State != EnrollmentState.Closed && e.DueDate is not null
                        && (e.State == EnrollmentState.Passed || e.DueDate < today))
            .ToList();
        return Percent(judged.Count(e => e.State == EnrollmentState.Passed && e.CompletedAt is { } completedAt
                                         && DateOnly.FromDateTime(completedAt.UtcDateTime) <= e.DueDate), judged.Count);
    }

    /// <summary>The average number of days from assignment to certification, over the enrolments that were passed.</summary>
    public static decimal? AverageDaysToCertification(IEnumerable<(DateTimeOffset EnrolledAt, DateTimeOffset? CompletedAt)> passed)
    {
        var days = passed.Where(e => e.CompletedAt is not null)
            .Select(e => (decimal)(e.CompletedAt!.Value - e.EnrolledAt).TotalDays).ToList();
        return days.Count == 0 ? null : Math.Round(days.Average(), 1, MidpointRounding.AwayFromZero);
    }
}
