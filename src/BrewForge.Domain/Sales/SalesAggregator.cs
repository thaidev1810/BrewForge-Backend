using System.Globalization;

namespace BrewForge.Domain.Sales;

/// <summary>The cups of one drink at one branch on one day: the grain every aggregate is built from.</summary>
public readonly record struct DailyCups(long BranchId, DateOnly TradingDate, int Cups);

public enum SalesGrouping
{
    Branch,
    Day,

    /// <summary>ISO 8601 weeks: Monday to Sunday, numbered within the ISO year.</summary>
    Week,
}

/// <summary>
/// The sales of one group. <see cref="TradingDays"/> counts branch-days, so
/// that <see cref="CupsPerDay"/> is cups per day per branch whatever the
/// group holds: one branch over a period, or every branch over a week.
/// </summary>
public sealed record SalesBucket(string Key, long? BranchId, DateOnly From, DateOnly To, int Cups, int TradingDays)
{
    public decimal CupsPerDay => SalesAggregator.Average(Cups, TradingDays);
}

/// <summary>Aggregation of daily sales: per branch, per day and per ISO week, with trend and comparison.</summary>
public static class SalesAggregator
{
    public static IReadOnlyList<SalesBucket> Group(IEnumerable<DailyCups> sales, SalesGrouping grouping) =>
        grouping switch
        {
            SalesGrouping.Branch =>
            [
                .. sales.GroupBy(day => day.BranchId).OrderBy(group => group.Key)
                    .Select(group => Bucket(group.Key.ToString(CultureInfo.InvariantCulture), group.Key,
                        group.Min(day => day.TradingDate), group.Max(day => day.TradingDate), group)),
            ],
            SalesGrouping.Day =>
            [
                .. sales.GroupBy(day => day.TradingDate).OrderBy(group => group.Key)
                    .Select(group => Bucket(group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null,
                        group.Key, group.Key, group)),
            ],
            SalesGrouping.Week =>
            [
                .. sales.GroupBy(day => IsoWeek(day.TradingDate)).OrderBy(group => group.Key)
                    .Select(group =>
                    {
                        var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(group.Key.Year, group.Key.Week, DayOfWeek.Monday));
                        return Bucket(WeekKey(group.Key.Year, group.Key.Week), null, monday, monday.AddDays(6), group);
                    }),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(grouping)),
        };

    public static (int Year, int Week) IsoWeek(DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day));
    }

    public static string WeekKey(DateOnly date)
    {
        var (year, week) = IsoWeek(date);
        return WeekKey(year, week);
    }

    /// <summary>
    /// Trend: the change of a period against the one before it, in percent.
    /// Null when there is nothing to compare with.
    /// </summary>
    public static decimal? ChangePercent(int previousCups, int cups) =>
        previousCups <= 0 ? null : Round((cups - previousCups) * 100m / previousCups);

    /// <summary>Comparison: the drink as a percentage of the control drink. Null when the control sold nothing.</summary>
    public static decimal? PercentOf(int cups, int controlCups) =>
        controlCups <= 0 ? null : Round(cups * 100m / controlCups);

    public static decimal Average(int cups, int tradingDays) => tradingDays <= 0 ? 0 : Round((decimal)cups / tradingDays);

    private static string WeekKey(int year, int week) => $"{year}-W{week:00}";

    private static SalesBucket Bucket(string key, long? branchId, DateOnly from, DateOnly to, IEnumerable<DailyCups> days)
    {
        var list = days.ToList();
        return new SalesBucket(key, branchId, from, to, list.Sum(day => day.Cups), list.Count);
    }

    private static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
