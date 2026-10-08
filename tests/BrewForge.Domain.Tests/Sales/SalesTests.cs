using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Sales;

namespace BrewForge.Domain.Tests.Sales;

/// <summary>UC-22 and UC-23: BR-24, BR-25, the POS file layout and the aggregates.</summary>
public sealed class SalesTests
{
    private const long Branch = 7;
    private const long Drink = 12;
    private const long VersionOne = 310;
    private const long VersionTwo = 320;
    private const long Manager = 40;

    /// <summary>06:30 on 6 October 2026 in Vietnam, which is still 5 October in UTC.</summary>
    private static readonly DateTimeOffset WentLive = new(2026, 10, 5, 23, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly LiveDay = new(2026, 10, 6);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
    private static readonly LaunchChange[] NoHistory = [];

    private static BranchLaunchStatus Planned() => BranchLaunchStatus.Plan(Branch, Drink, VersionOne, minCertifiedStaff: 2);

    private static BranchLaunchStatus Ready()
    {
        var launch = Planned();
        launch.RecomputeCoverage(2);
        return launch;
    }

    private static BranchLaunchStatus Live()
    {
        var launch = Ready();
        launch.GoLive(WentLive);
        return launch;
    }

    private static SalesRecord Sale(BranchLaunchStatus launch, DateOnly day, int cups = 143,
        IReadOnlyCollection<LaunchChange>? history = null, SalesSource source = SalesSource.Manual) =>
        SalesRecord.Record(launch, history ?? NoHistory, day, cups, source, Manager, Now);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- BR-24

    [Fact]
    public void BR_24_a_sale_is_attached_to_the_version_live_at_the_branch_that_day()
    {
        var record = Sale(Live(), LiveDay.AddDays(3));

        Assert.Equal((Branch, Drink, VersionOne), (record.BranchId, record.RecipeId, record.RecipeVersionId));
        Assert.Equal(LiveDay.AddDays(3), record.TradingDate);
        Assert.Equal(143, record.CupsSold);
        Assert.Equal(SalesSource.Manual, record.Source);
        Assert.Equal((Manager, Now), (record.RecordedBy, record.RecordedAt));
    }

    [Fact]
    public void BR_24_a_branch_that_is_not_live_yet_cannot_record_a_sale()
    {
        Assert.All(new[] { Planned(), Ready() }, launch =>
        {
            var refusal = Refused(() => Sale(launch, LiveDay));

            Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
            Assert.Equal(("BR-24", "MSG-E22"), (refusal.Rule, refusal.Code));
        });
    }

    [Fact]
    public void BR_24_the_day_before_the_branch_went_live_is_refused_and_the_day_itself_is_not()
    {
        var launch = Live();

        Assert.Equal("BR-24", Refused(() => Sale(launch, LiveDay.AddDays(-1))).Rule);
        Assert.Equal(VersionOne, Sale(launch, LiveDay).RecipeVersionId);
    }

    [Fact]
    public void Trading_day_is_the_calendar_day_in_Vietnam()
    {
        // Late evening in UTC is already the next morning at the branch.
        Assert.Equal(new DateOnly(2026, 10, 6), TradingCalendar.DateOf(new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 5), TradingCalendar.DateOf(new DateTimeOffset(2026, 10, 5, 16, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void BR_24_a_withdrawn_drink_is_sold_up_to_the_day_it_was_withdrawn_and_no_later()
    {
        var launch = Live();
        launch.Withdraw();
        var withdrawnOn = LiveDay.AddDays(8);
        LaunchChange[] history = [new(LaunchChangeKind.Withdrawn, WentLive.AddDays(8))];

        // A day of the live period, entered late, is still a sale of that period.
        Assert.Equal(VersionOne, Sale(launch, LiveDay.AddDays(2), history: history).RecipeVersionId);
        Assert.Equal(VersionOne, Sale(launch, withdrawnOn, history: history).RecipeVersionId);
        Assert.Equal("BR-24", Refused(() => Sale(launch, withdrawnOn.AddDays(1), history: history)).Rule);

        // With no record of when it was withdrawn, no day can be shown to lie before it.
        Assert.Equal("BR-24", Refused(() => Sale(launch, LiveDay.AddDays(2))).Rule);
    }

    [Fact]
    public void BR_24_a_sale_entered_late_belongs_to_the_version_the_branch_sold_on_its_day()
    {
        var launch = Live();
        launch.MoveToVersion(VersionTwo, certifiedCountOnThatVersion: 0);
        var movedOn = LiveDay.AddDays(7);
        LaunchChange[] history = [new(LaunchChangeKind.VersionMoved, WentLive.AddDays(7), PreviousVersionId: VersionOne)];

        Assert.Equal(VersionOne, Sale(launch, movedOn.AddDays(-1), history: history).RecipeVersionId);
        Assert.Equal(VersionTwo, Sale(launch, movedOn, history: history).RecipeVersionId);
        Assert.Equal(VersionTwo, Sale(launch, movedOn.AddDays(3), history: history).RecipeVersionId);
    }

    [Fact]
    public void BR_24_through_two_version_moves_each_day_finds_its_own_version()
    {
        var launch = Live();
        launch.MoveToVersion(VersionTwo, 0);
        launch.MoveToVersion(330, 0);
        LaunchChange[] history =
        [
            new(LaunchChangeKind.VersionMoved, WentLive.AddDays(10), PreviousVersionId: VersionTwo),
            new(LaunchChangeKind.VersionMoved, WentLive.AddDays(4), PreviousVersionId: VersionOne),
        ];

        Assert.Equal(VersionOne, Sale(launch, LiveDay.AddDays(1), history: history).RecipeVersionId);
        Assert.Equal(VersionTwo, Sale(launch, LiveDay.AddDays(6), history: history).RecipeVersionId);
        Assert.Equal(330, Sale(launch, LiveDay.AddDays(12), history: history).RecipeVersionId);
    }

    [Fact]
    public void BR_24_nobody_who_records_a_sale_gets_to_name_the_version()
    {
        // No public member of the record takes a version: it only ever comes from the launch status.
        var parameters = typeof(SalesRecord).GetMethods().Where(method => method.DeclaringType == typeof(SalesRecord))
            .SelectMany(method => method.GetParameters()).Select(parameter => parameter.Name!)
            .Concat(typeof(SalesRecord).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.Name!));

        Assert.DoesNotContain(parameters, name => name.Contains("version", StringComparison.OrdinalIgnoreCase));
        Assert.False(typeof(SalesRecord).GetProperty(nameof(SalesRecord.RecipeVersionId))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void BR_36_an_existing_drink_is_live_from_the_start_and_sold_although_coverage_is_not_met()
    {
        var launch = BranchLaunchStatus.LiveForExistingRecipe(Branch, Drink, VersionOne, minCertifiedStaff: 2, WentLive);

        Assert.Equal((LaunchStatus.Live, false), (launch.Status, launch.CoverageMet));
        Assert.Equal(VersionOne, Sale(launch, LiveDay).RecipeVersionId);
    }

    [Fact]
    public void Sale_cannot_be_in_the_future_or_negative()
    {
        var launch = Live();
        var today = TradingCalendar.DateOf(Now);

        Assert.Contains(Refused(() => Sale(launch, today.AddDays(1))).Details, d => d.Field == "tradingDate");
        Assert.Contains(Refused(() => Sale(launch, today, cups: -1)).Details, d => d.Field == "cupsSold");
        // No cups sold on a trading day is a fact worth recording.
        Assert.Equal(0, Sale(launch, today, cups: 0).CupsSold);
    }

    // ---------------------------------------------------------------- BR-25

    [Fact]
    public void BR_25_a_second_record_for_the_same_drink_branch_and_day_is_refused()
    {
        var launch = Live();
        var book = new SalesBook([Sale(launch, LiveDay)]);

        var refusal = Refused(() => book.Add(Sale(launch, LiveDay, cups: 99)));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal(("BR-25", "MSG-E23"), (refusal.Rule, refusal.Code));
        Assert.False(book.TryAdd(Sale(launch, LiveDay, cups: 99)));
        Assert.Equal(143, book.Find(Branch, Drink, LiveDay)!.CupsSold); // the first one stands

        // Another day, or the same day at another branch, is another record.
        book.Add(Sale(launch, LiveDay.AddDays(1)));
        var elsewhere = BranchLaunchStatus.LiveForExistingRecipe(Branch + 1, Drink, VersionOne, 2, WentLive);
        book.Add(Sale(elsewhere, LiveDay));
    }

    [Fact]
    public void BR_25_an_imported_day_replaces_what_was_recorded_for_it()
    {
        var record = Sale(Live(), LiveDay, cups: 143, source: SalesSource.Manual);
        var later = Now.AddHours(2);

        Assert.True(record.ReplaceFromImport(151, recordedBy: 41, later));

        Assert.Equal((151, SalesSource.PosImport, 41L, later), (record.CupsSold, record.Source, record.RecordedBy, record.RecordedAt));
        Assert.Equal((LiveDay, VersionOne), (record.TradingDate, record.RecipeVersionId)); // the day and its version stay

        // The same file again changes nothing.
        Assert.False(record.ReplaceFromImport(151, recordedBy: 42, later.AddHours(1)));
        Assert.Equal(41, record.RecordedBy);
    }

    [Fact]
    public void Correction_changes_the_count_and_says_whether_anything_changed()
    {
        var record = Sale(Live(), LiveDay, cups: 143);

        Assert.False(record.Correct(143, Manager, Now));
        Assert.True(record.Correct(134, 41, Now.AddHours(1)));
        Assert.Equal((134, 41L, SalesSource.Manual), (record.CupsSold, record.RecordedBy, record.Source));
        Assert.Contains(Refused(() => record.Correct(-5, Manager, Now)).Details, d => d.Field == "cupsSold");
    }

    // ---------------------------------------------------------------- the POS layout

    [Fact]
    public void Layout_is_the_four_fixed_columns_in_order()
    {
        PosImportLine.EnsureLayout(["branch_code", "drink_code", "trading_date", "quantity"]);
        PosImportLine.EnsureLayout([" Branch_Code ", "DRINK_CODE", "trading_date", "quantity", "", ""]); // case, spaces, trailing blanks

        Assert.All(new string[]?[]
        {
            null, [], ["drink_code", "branch_code", "trading_date", "quantity"],
            ["branch_code", "drink_code", "trading_date"], ["branch", "drink", "date", "qty"],
            ["branch_code", "drink_code", "trading_date", "quantity", "price"],
        }, header =>
        {
            var refusal = Refused(() => PosImportLine.EnsureLayout(header));
            Assert.Equal((ErrorKind.Validation, "IMPORT_LAYOUT"), (refusal.Kind, refusal.Code));
        });
    }

    [Fact]
    public void Line_is_read_into_branch_drink_day_and_quantity()
    {
        var line = PosImportLine.Parse(18, [" B01 ", "R07", "2026-10-06", "143"], out var error);

        Assert.Null(error);
        Assert.Equal(new PosImportLine(18, "B01", "R07", new DateOnly(2026, 10, 6), 143), line);
    }

    [Theory]
    [InlineData("143.0", 143)]      // how a spreadsheet may write a whole number
    [InlineData("0", 0)]
    [InlineData("+12", 12)]
    public void Quantity_is_a_whole_number(string text, int expected)
    {
        var line = PosImportLine.Parse(2, ["B01", "R07", "2026-10-06", text], out _);

        Assert.Equal(expected, line!.Quantity);
    }

    [Theory]
    [InlineData("abc", "Quantity 'abc' is not a whole number")]
    [InlineData("12.5", "Quantity '12.5' is not a whole number")]
    [InlineData("1,234", "Quantity '1,234' is not a whole number")]
    [InlineData("12 cups", "Quantity '12 cups' is not a whole number")]
    [InlineData("-3", "Quantity -3 is negative")]
    public void Quantity_that_is_not_a_whole_non_negative_number_rejects_the_line(string text, string message)
    {
        var line = PosImportLine.Parse(9, ["B01", "R07", "2026-10-06", text], out var error);

        Assert.Null(line);
        Assert.Equal(new PosRowError(9, "IMPORT_INVALID_QUANTITY", message), error);
    }

    [Theory]
    [InlineData("2026-10-06", 2026, 10, 6)]
    [InlineData("2026-10-06T00:00:00", 2026, 10, 6)]
    [InlineData("46301", 2026, 10, 6)]            // the day serial of a spreadsheet date cell
    public void Trading_date_is_an_iso_date_or_a_spreadsheet_date(string text, int year, int month, int day)
    {
        var line = PosImportLine.Parse(2, ["B01", "R07", text, "1"], out _);

        Assert.Equal(new DateOnly(year, month, day), line!.TradingDate);
    }

    [Theory]
    [InlineData("06/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    [InlineData("2026-10-06T14:30:00")]
    [InlineData("12")]
    public void Trading_date_in_any_other_form_rejects_the_line(string text)
    {
        var line = PosImportLine.Parse(4, ["B01", "R07", text, "1"], out var error);

        Assert.Null(line);
        Assert.Equal((4, "IMPORT_INVALID_DATE"), (error!.Row, error.Code));
        Assert.Contains(text, error.Message);
    }

    [Fact]
    public void Missing_value_rejects_the_line_and_names_the_column()
    {
        Assert.Null(PosImportLine.Parse(5, ["B01", "", "2026-10-06", "1"], out var noDrink));
        Assert.Null(PosImportLine.Parse(6, ["B01", "R07"], out var shortLine));

        Assert.Equal(new PosRowError(5, "IMPORT_MISSING_VALUE", "Missing drink_code"), noDrink);
        Assert.Equal(new PosRowError(6, "IMPORT_MISSING_VALUE", "Missing trading_date"), shortLine);
    }

    // ---------------------------------------------------------------- aggregation

    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static readonly DailyCups[] TwoBranchesTwoWeeks =
    [
        new(1, Monday, 40), new(1, Monday.AddDays(1), 50), new(1, Monday.AddDays(7), 30),
        new(2, Monday, 20), new(2, Monday.AddDays(8), 60),
    ];

    [Fact]
    public void Sales_are_grouped_per_branch_with_cups_per_trading_day()
    {
        var buckets = SalesAggregator.Group(TwoBranchesTwoWeeks, SalesGrouping.Branch);

        Assert.Equal(
        [
            new SalesBucket("1", 1, Monday, Monday.AddDays(7), Cups: 120, TradingDays: 3),
            new SalesBucket("2", 2, Monday, Monday.AddDays(8), Cups: 80, TradingDays: 2),
        ], buckets);
        Assert.Equal([40.0m, 40.0m], buckets.Select(b => b.CupsPerDay));
    }

    [Fact]
    public void Sales_are_grouped_per_day_across_branches()
    {
        var buckets = SalesAggregator.Group(TwoBranchesTwoWeeks, SalesGrouping.Day);

        Assert.Equal(["2026-10-05", "2026-10-06", "2026-10-12", "2026-10-13"], buckets.Select(b => b.Key));
        Assert.Equal([60, 50, 30, 60], buckets.Select(b => b.Cups));
        // Two branches traded on the first day: 60 cups over two branch-days.
        Assert.Equal((2, 30.0m), (buckets[0].TradingDays, buckets[0].CupsPerDay));
    }

    [Fact]
    public void Sales_are_grouped_per_iso_week_from_monday_to_sunday()
    {
        var buckets = SalesAggregator.Group(TwoBranchesTwoWeeks, SalesGrouping.Week);

        Assert.Equal(
        [
            new SalesBucket("2026-W41", null, Monday, Monday.AddDays(6), Cups: 110, TradingDays: 3),
            new SalesBucket("2026-W42", null, Monday.AddDays(7), Monday.AddDays(13), Cups: 90, TradingDays: 2),
        ], buckets);
    }

    [Fact]
    public void Iso_week_belongs_to_the_iso_year_not_the_calendar_year()
    {
        // 2026 has 53 ISO weeks: the last one runs from 28 December 2026 to 3 January 2027.
        Assert.Equal("2026-W53", SalesAggregator.WeekKey(new DateOnly(2026, 12, 31)));
        Assert.Equal("2026-W53", SalesAggregator.WeekKey(new DateOnly(2027, 1, 3)));
        Assert.Equal("2027-W01", SalesAggregator.WeekKey(new DateOnly(2027, 1, 4)));
        // And 1 January 2026 is a Thursday, so it is in week 1 of 2026, which began in December 2025.
        Assert.Equal("2026-W01", SalesAggregator.WeekKey(new DateOnly(2025, 12, 29)));

        var week = Assert.Single(SalesAggregator.Group(
            [new DailyCups(1, new DateOnly(2026, 12, 31), 10), new DailyCups(1, new DateOnly(2027, 1, 1), 15)],
            SalesGrouping.Week));
        Assert.Equal(("2026-W53", 25), (week.Key, week.Cups));
    }

    [Fact]
    public void Trend_and_comparison_are_percentages_and_null_when_there_is_nothing_to_compare_with()
    {
        Assert.Equal(-18.2m, SalesAggregator.ChangePercent(previousCups: 110, cups: 90));
        Assert.Equal(25.0m, SalesAggregator.ChangePercent(80, 100));
        Assert.Null(SalesAggregator.ChangePercent(0, 100));

        Assert.Equal(73.0m, SalesAggregator.PercentOf(cups: 146, controlCups: 200));
        Assert.Null(SalesAggregator.PercentOf(146, 0));

        Assert.Equal(53.9m, SalesAggregator.Average(cups: 1510, tradingDays: 28)); // the example of the API contract
        Assert.Equal(0m, SalesAggregator.Average(0, 0));
    }

    [Fact]
    public void Nothing_sold_gives_no_groups()
    {
        Assert.All(Enum.GetValues<SalesGrouping>(), grouping => Assert.Empty(SalesAggregator.Group([], grouping)));
    }
}
