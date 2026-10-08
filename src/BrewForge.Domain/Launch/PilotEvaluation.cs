using BrewForge.Domain.Sales;

namespace BrewForge.Domain.Launch;

public enum Verdict
{
    Pass,
    Fail,

    /// <summary>Not scored: the figures needed to judge are not there.</summary>
    Incomplete,
}

/// <summary>One criterion measured: what was asked for, what was found, and the verdict.</summary>
public sealed record CriterionResult(CriterionType Type, decimal Target, decimal? Actual, Verdict Verdict);

/// <summary>
/// One pilot branch. <c>ExpectedDays</c> are the days it was live within the
/// period; a branch that has a sales record for fewer of them has incomplete
/// coverage and is not scored.
/// </summary>
public sealed record BranchResult(long BranchId, int ExpectedDays, int TradingDays, int CupsTotal, decimal CupsPerDay,
    bool CoverageComplete, Verdict Verdict, IReadOnlyList<CriterionResult> Criteria);

/// <summary>A week of the pilot, counted from its first day.</summary>
public sealed record WeekResult(int Week, DateOnly From, DateOnly To, int Cups);

/// <summary>The evaluation of the API contract: a verdict per criterion, per branch and overall.</summary>
public sealed record PilotEvaluation(long PilotId, PilotState State, DateOnly From, DateOnly To, bool CoverageComplete,
    Verdict Overall, IReadOnlyList<CriterionResult> Criteria, IReadOnlyList<BranchResult> ByBranch,
    IReadOnlyList<WeekResult> ByWeek);

/// <summary>
/// The Pilot Criteria Evaluator (UC-24). It judges the pilot on the sales of
/// its version at its branches within its period, and on nothing else. A
/// branch with missing trading days is reported as incomplete and left out of
/// the scoring rather than scored on part of the picture.
/// </summary>
public static class PilotEvaluator
{
    /// <param name="sales">The sales of the pilot's version at its branches.</param>
    /// <param name="controlSales">The sales of the control drink at the same branches, if a criterion names one.</param>
    public static PilotEvaluation Evaluate(PilotProgram pilot, DateOnly today, IReadOnlyCollection<DailyCups> sales,
        IReadOnlyCollection<DailyCups> controlSales)
    {
        // A pilot that is still running is measured up to today.
        var lastDay = pilot.EndDate < today ? pilot.EndDate : today;
        var inPeriod = sales.Where(day => day.TradingDate >= pilot.StartDate && day.TradingDate <= lastDay).ToList();
        var criteria = pilot.Criteria().Criteria;

        var branches = new List<BranchResult>();
        var scored = new List<DailyCups>();
        foreach (var branch in pilot.Branches.OrderBy(b => b.BranchId))
        {
            // A branch can only have sold from the day it went live.
            var liveFrom = branch.WentLiveAt is { } at ? TradingCalendar.DateOf(at) : (DateOnly?)null;
            var firstDay = liveFrom is null ? (DateOnly?)null : liveFrom > pilot.StartDate ? liveFrom : pilot.StartDate;
            var expected = firstDay is { } first && first <= lastDay ? lastDay.DayNumber - first.DayNumber + 1 : 0;

            var own = inPeriod.Where(day => day.BranchId == branch.BranchId).ToList();
            var cups = own.Sum(day => day.Cups);
            var complete = expected > 0 && own.Count == expected;
            if (complete) scored.AddRange(own);

            var results = complete ? Score(criteria, pilot, own, controlSales) : [];
            branches.Add(new BranchResult(branch.BranchId, expected, own.Count, cups, SalesAggregator.Average(cups, own.Count),
                complete, complete ? Summarize(results) : Verdict.Incomplete, results));
        }

        var overall = Score(criteria, pilot, scored, controlSales);
        return new PilotEvaluation(pilot.Id, pilot.State, pilot.StartDate, pilot.EndDate,
            CoverageComplete: branches.Count > 0 && branches.All(branch => branch.CoverageComplete),
            // With no branch to score there is no verdict to give.
            Overall: scored.Count == 0 ? Verdict.Incomplete : Summarize(overall),
            overall, branches, Weeks(pilot, lastDay, inPeriod));
    }

    private static List<CriterionResult> Score(IReadOnlyList<PilotCriterion> criteria, PilotProgram pilot,
        IReadOnlyCollection<DailyCups> sales, IReadOnlyCollection<DailyCups> controlSales) =>
    [
        .. criteria.Select(criterion =>
        {
            var actual = criterion.Type switch
            {
                CriterionType.Absolute => CupsPerDayPerBranch(sales),
                CriterionType.Relative => PercentOfControl(sales, controlSales),
                _ => DropInSecondHalf(sales, pilot.StartDate, pilot.EndDate),
            };
            var verdict = actual switch
            {
                null => Verdict.Incomplete,
                // A drop is passed by staying at or under the limit; the other two by reaching the target.
                _ when criterion.Type == CriterionType.Retention => actual <= criterion.Target ? Verdict.Pass : Verdict.Fail,
                _ => actual >= criterion.Target ? Verdict.Pass : Verdict.Fail,
            };
            return new CriterionResult(criterion.Type, criterion.Target, actual, verdict);
        }),
    ];

    private static decimal? CupsPerDayPerBranch(IReadOnlyCollection<DailyCups> sales) =>
        sales.Count == 0 ? null : SalesAggregator.Average(sales.Sum(day => day.Cups), sales.Count);

    /// <summary>The control drink is counted on the branches and days the pilot drink traded.</summary>
    private static decimal? PercentOfControl(IReadOnlyCollection<DailyCups> sales,
        IReadOnlyCollection<DailyCups> controlSales)
    {
        var traded = sales.Select(day => (day.BranchId, day.TradingDate)).ToHashSet();
        var control = controlSales.Where(day => traded.Contains((day.BranchId, day.TradingDate))).Sum(day => day.Cups);
        return SalesAggregator.PercentOf(sales.Sum(day => day.Cups), control);
    }

    /// <summary>
    /// The drop of cups per day per branch from the first half of the period
    /// to the second, in percent. Negative when the drink sold better in the
    /// second half. For a period of an odd number of days the first half has
    /// the extra day.
    /// </summary>
    private static decimal? DropInSecondHalf(IReadOnlyCollection<DailyCups> sales, DateOnly startDate, DateOnly endDate)
    {
        var days = endDate.DayNumber - startDate.DayNumber + 1;
        var secondHalfStarts = startDate.AddDays((days + 1) / 2);
        var first = sales.Where(day => day.TradingDate < secondHalfStarts).ToList();
        var second = sales.Where(day => day.TradingDate >= secondHalfStarts).ToList();
        if (first.Count == 0 || second.Count == 0) return null;

        var before = (decimal)first.Sum(day => day.Cups) / first.Count;
        var after = (decimal)second.Sum(day => day.Cups) / second.Count;
        return before <= 0 ? null : Math.Round((before - after) * 100m / before, 1, MidpointRounding.AwayFromZero);
    }

    private static Verdict Summarize(IReadOnlyCollection<CriterionResult> results) =>
        results.Any(result => result.Verdict == Verdict.Fail) ? Verdict.Fail
        : results.Any(result => result.Verdict == Verdict.Incomplete) ? Verdict.Incomplete
        : Verdict.Pass;

    private static List<WeekResult> Weeks(PilotProgram pilot, DateOnly lastDay, IReadOnlyCollection<DailyCups> sales)
    {
        var weeks = new List<WeekResult>();
        for (var (week, from) = (1, pilot.StartDate); from <= lastDay; week++, from = from.AddDays(7))
        {
            var to = from.AddDays(6) < pilot.EndDate ? from.AddDays(6) : pilot.EndDate;
            weeks.Add(new WeekResult(week, from, to,
                sales.Where(day => day.TradingDate >= from && day.TradingDate <= to).Sum(day => day.Cups)));
        }
        return weeks;
    }
}
