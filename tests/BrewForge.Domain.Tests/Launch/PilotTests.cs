using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Sales;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Launch;

/// <summary>UC-21, UC-24, UC-25 and the launch gate: BR-23, BR-26, BR-27, BR-28 and BR-36.</summary>
public sealed class PilotTests
{
    private const long RecipeId = 12;
    private const long VersionId = 310;
    private const long Control = 8;
    private const long Manager = 3;
    private const long B1 = 7;
    private const long B2 = 9;

    /// <summary>Four weeks, Monday 7 September to Sunday 4 October 2026.</summary>
    private static readonly DateOnly Start = new(2026, 9, 7);
    private static readonly DateOnly End = new(2026, 10, 4);
    private static readonly DateOnly AfterTheEnd = End.AddDays(1);

    /// <summary>06:00 in Vietnam on the first day of the pilot.</summary>
    private static readonly DateTimeOffset WentLive = new(2026, 9, 6, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 2, 0, 0, TimeSpan.Zero);

    private static readonly PilotCriterion Absolute40 = new(CriterionType.Absolute, CupsPerDayPerBranch: 40);
    private static readonly PilotCriterion Relative60 = new(CriterionType.Relative, ControlRecipeId: Control, MinPercentOfControl: 60);
    private static readonly PilotCriterion Retention25 = new(CriterionType.Retention, MaxDropSecondHalfPct: 25);

    // ---------------------------------------------------------------- fixtures

    private static Recipe Drink(RecipeOrigin origin = RecipeOrigin.New) =>
        WithId(Recipe.Create("R12", "Oolong milk tea", RecipeCategory.Tea, origin, 2, Now), RecipeId);

    private static PilotProgram Pilot(IEnumerable<PilotProgram>? others = null, RecipeVersion? version = null,
        Recipe? recipe = null, long[]? branches = null, params PilotCriterion[] criteria) =>
        WithId(PilotProgram.Create(recipe ?? Drink(), version ?? ReleasedVersion(VersionId, RecipeId), others ?? [],
            "Oolong pilot", Start, End, PilotCriteria.Create(criteria.Length > 0 ? criteria : [Absolute40]),
            minCertifiedStaff: 2, branches ?? [B1, B2], Manager), 5);

    private static PilotProgram Running(params PilotCriterion[] criteria)
    {
        var pilot = Pilot(criteria: criteria);
        pilot.Start(Start.AddDays(-3));
        return pilot;
    }

    /// <summary>A RUNNING pilot whose branches went live on its first day.</summary>
    private static PilotProgram RunningAndLive(params PilotCriterion[] criteria)
    {
        var pilot = Running(criteria);
        foreach (var branch in pilot.Branches)
        {
            pilot.GoLive(branch.BranchId, BranchLaunchStatus.Plan(branch.BranchId, RecipeId, VersionId, 2), certifiedCount: 2, WentLive);
        }
        return pilot;
    }

    private static PilotProgram Ended(params PilotCriterion[] criteria)
    {
        var pilot = RunningAndLive(criteria);
        Assert.True(pilot.EndIfDue(AfterTheEnd));
        return pilot;
    }

    /// <summary>A branch selling the same number of cups on every day of a range.</summary>
    private static IEnumerable<DailyCups> Daily(long branch, DateOnly from, DateOnly to, int cups) =>
        Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => new DailyCups(branch, from.AddDays(i), cups));

    private static PilotEvaluation Evaluate(PilotProgram pilot, IEnumerable<DailyCups> sales,
        IEnumerable<DailyCups>? control = null, DateOnly? today = null) =>
        PilotEvaluator.Evaluate(pilot, today ?? AfterTheEnd, [.. sales], [.. control ?? []]);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- UC-21

    [Fact]
    public void Pilot_binds_a_released_version_its_branches_a_period_and_the_criteria()
    {
        var pilot = Pilot(criteria: [Absolute40, Relative60, Retention25]);

        Assert.Equal((VersionId, "Oolong pilot", Start, End, 2, PilotState.Draft, Manager),
            (pilot.RecipeVersionId, pilot.Name, pilot.StartDate, pilot.EndDate, pilot.MinCertifiedStaff, pilot.State, pilot.CreatedBy));
        Assert.Equal([B1, B2], pilot.Branches.Select(b => b.BranchId));
        Assert.All(pilot.Branches, branch => Assert.Equal(PilotBranchState.Preparing, branch.ReadinessState));
        Assert.Equal([Absolute40, Relative60, Retention25], pilot.Criteria().Criteria);
        Assert.Null(pilot.Decision);
    }

    [Fact]
    public void Criteria_are_stored_in_the_shape_of_the_contract()
    {
        var json = PilotCriteria.Create([Absolute40, Relative60, Retention25]).ToJson();

        Assert.Equal(
            """{"criteria":[{"type":"ABSOLUTE","cupsPerDayPerBranch":40},{"type":"RELATIVE","controlRecipeId":8,"minPercentOfControl":60},{"type":"RETENTION","maxDropSecondHalfPct":25}]}""",
            json);
        Assert.Equal([Absolute40, Relative60, Retention25], PilotCriteria.FromJson(json).Criteria);
        Assert.Equal(Control, PilotCriteria.FromJson(json).ControlRecipeId);
    }

    [Fact]
    public void Criteria_are_validated_by_kind()
    {
        string[] Fields(params PilotCriterion[] criteria) =>
            [.. Refused(() => PilotCriteria.Create(criteria)).Details.Select(d => d.Field)];

        Assert.Contains("criteria", Fields());
        Assert.Contains("criteria", Fields(Absolute40, Absolute40 with { CupsPerDayPerBranch = 50 }));
        Assert.Contains("criteria[0].cupsPerDayPerBranch", Fields(new PilotCriterion(CriterionType.Absolute)));
        Assert.Contains("criteria[0].cupsPerDayPerBranch", Fields(Absolute40 with { CupsPerDayPerBranch = 0 }));
        Assert.Contains("criteria[0].controlRecipeId", Fields(new PilotCriterion(CriterionType.Relative, MinPercentOfControl: 60)));
        Assert.Contains("criteria[0].minPercentOfControl", Fields(new PilotCriterion(CriterionType.Relative, ControlRecipeId: Control)));
        Assert.Contains("criteria[0].maxDropSecondHalfPct", Fields(Retention25 with { MaxDropSecondHalfPct = 101 }));
        // A field of another kind is a mistake, not something to ignore.
        Assert.Contains("criteria[1]", Fields(Absolute40, Retention25 with { CupsPerDayPerBranch = 40 }));
    }

    [Fact]
    public void Pilot_is_validated()
    {
        string[] Fields(Action create) => [.. Refused(create).Details.Select(d => d.Field)];
        PilotProgram Create(string? name = "Pilot", DateOnly? end = null, int min = 2, long[]? branches = null,
            PilotCriterion? criterion = null) =>
            PilotProgram.Create(Drink(), ReleasedVersion(VersionId, RecipeId), [], name, Start, end ?? End,
                PilotCriteria.Create([criterion ?? Absolute40]), min, branches ?? [B1], Manager);

        Assert.Contains("name", Fields(() => Create(name: " ")));
        Assert.Contains("endDate", Fields(() => Create(end: Start.AddDays(-1))));
        Assert.Contains("minCertifiedStaff", Fields(() => Create(min: 0)));
        Assert.Contains("branchIds", Fields(() => Create(branches: [])));
        Assert.Contains("branchIds", Fields(() => Create(branches: [B1, B1])));
        // A drink is not measured against itself.
        Assert.Contains("criteria", Fields(() => Create(criterion: Relative60 with { ControlRecipeId = RecipeId })));
        // A pilot of a single day is a pilot.
        Assert.Equal(Start, Create(end: Start).EndDate);
    }

    [Fact]
    public void Pilot_tests_a_released_version_only()
    {
        var draft = WithId(RecipeVersion.CreateDraft(RecipeId, 2, 2), 311);

        var refusal = Refused(() => Pilot(version: draft));

        Assert.Equal((ErrorKind.RuleViolation, "PILOT_VERSION_NOT_RELEASED"), (refusal.Kind, refusal.Rule));
    }

    // ---------------------------------------------------------------- BR-28

    [Fact]
    public void BR_28_a_version_holds_one_draft_or_running_pilot_at_a_time()
    {
        var draft = Pilot();
        var running = Running();

        Assert.All(new[] { draft, running }, active =>
        {
            var refusal = Refused(() => Pilot(others: [active]));
            Assert.Equal((ErrorKind.RuleViolation, "BR-28"), (refusal.Kind, refusal.Rule));
        });
    }

    [Fact]
    public void BR_28_an_ended_or_cancelled_pilot_frees_its_version_for_another()
    {
        var ended = Ended();
        var cancelled = Pilot();
        cancelled.Cancel();
        // A pilot of another version of the drink is no obstacle either.
        var ofAnotherVersion = Pilot(version: ReleasedVersion(999, RecipeId, versionNo: 2));

        var next = Pilot(others: [ended, cancelled, ofAnotherVersion]);

        Assert.Equal(PilotState.Draft, next.State);
    }

    [Fact]
    public void BR_28_a_version_under_test_cannot_be_superseded_by_a_release()
    {
        var released = ReleasedVersion(VersionId, RecipeId, versionNo: 1);
        RecipeVersion Candidate()
        {
            var next = RecipeVersion.CreateDraft(RecipeId, 2, Author);
            next.ReplaceContent([Step(1, "Brew a lighter oolong", "TEA_BREWER", 420, [(Oolong, 16m, "g")])], Author);
            next.Submit(next.Validate(Catalog()));
            return next;
        }
        RecipeRelease Prepare(RecipeVersion candidate, params PilotProgram[] pilots) =>
            RecipeRelease.Prepare(candidate, released, candidate.Validate(Catalog()), approverId: Author + 70,
                highestVersionNo: 2, Now, pilots);

        Assert.All(new[] { Pilot(), Running() }, active =>
        {
            var refusal = Refused(() => Prepare(Candidate(), active));
            Assert.Equal((ErrorKind.RuleViolation, "BR-28"), (refusal.Kind, refusal.Rule));
        });
        Assert.Equal(VersionState.Released, released.State);

        // Once the pilot has ended, or was cancelled, the next version can be released.
        var cancelled = Pilot();
        cancelled.Cancel();
        Assert.Equal(2, Prepare(Candidate(), Ended(), cancelled).VersionNo);
    }

    // ---------------------------------------------------------------- BR-26

    [Fact]
    public void BR_26_the_criteria_are_read_only_from_the_moment_the_pilot_starts()
    {
        var pilot = Running();
        var frozen = pilot.CriteriaJson;

        var refusal = Refused(() => pilot.Update(RecipeId, "Easier", Start, End,
            PilotCriteria.Create([Absolute40 with { CupsPerDayPerBranch = 5 }]), 2, [B1, B2]));

        Assert.Equal((ErrorKind.RuleViolation, "BR-26"), (refusal.Kind, refusal.Rule));
        Assert.Equal(frozen, pilot.CriteriaJson);
        Assert.Equal("Oolong pilot", pilot.Name);
    }

    [Fact]
    public void BR_26_holds_in_every_state_after_draft()
    {
        var ended = Ended();
        var cancelled = Pilot();
        cancelled.Cancel();

        Assert.All(new[] { Running(), ended, cancelled }, pilot =>
            Assert.Equal("BR-26", Refused(() => pilot.Update(RecipeId, "x", Start, End, PilotCriteria.Create([Absolute40]), 2, [B1])).Rule));
    }

    [Fact]
    public void Draft_pilot_can_still_be_changed()
    {
        var pilot = Pilot();

        pilot.Update(RecipeId, "Renamed", Start.AddDays(7), End.AddDays(7), PilotCriteria.Create([Retention25]), 3, [B2, 11]);

        Assert.Equal(("Renamed", Start.AddDays(7), End.AddDays(7), 3), (pilot.Name, pilot.StartDate, pilot.EndDate, pilot.MinCertifiedStaff));
        Assert.Equal([Retention25], pilot.Criteria().Criteria);
        Assert.Equal([B2, 11], pilot.Branches.Select(b => b.BranchId));
    }

    // ---------------------------------------------------------------- the pilot state model

    [Fact]
    public void Pilot_state_model_is_the_one_of_the_data_dictionary()
    {
        var pilot = Pilot();
        pilot.Start(Start);
        Assert.Equal(PilotState.Running, pilot.State);

        // It ends by the calendar, the day after its last day, and not before.
        Assert.False(pilot.EndIfDue(End));
        Assert.Equal(PilotState.Running, pilot.State);
        Assert.True(pilot.EndIfDue(AfterTheEnd));
        Assert.Equal(PilotState.Ended, pilot.State);
        Assert.False(pilot.EndIfDue(AfterTheEnd.AddDays(30)));

        // What is not in the table does not exist.
        Assert.Equal(PilotProgram.StateRule, Refused(() => pilot.Start(Start)).Rule);
        Assert.Equal(PilotProgram.StateRule, Refused(pilot.Cancel).Rule);
        Assert.False(Pilot().EndIfDue(AfterTheEnd)); // a DRAFT does not end, it is started or cancelled

        var cancelledWhileRunning = Running();
        cancelledWhileRunning.Cancel();
        Assert.Equal(PilotState.Cancelled, cancelledWhileRunning.State);
        Assert.Equal(PilotProgram.StateRule, Refused(() => cancelledWhileRunning.Start(Start)).Rule);
    }

    [Fact]
    public void Pilot_whose_period_is_over_is_not_started()
    {
        var refusal = Refused(() => Pilot().Start(AfterTheEnd));

        Assert.Equal("PILOT_PERIOD_OVER", refusal.Rule);
    }

    // ---------------------------------------------------------------- BR-23: the gate

    [Fact]
    public void BR_23_a_branch_may_not_go_from_preparing_straight_to_live()
    {
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, minCertifiedStaff: 2);
        launch.RecomputeCoverage(1);

        var refusal = Refused(() => launch.GoLive(Now));

        Assert.Equal((ErrorKind.RuleViolation, "BR-23"), (refusal.Kind, refusal.Rule));
        Assert.Contains(refusal.Details, d => d.Field == "certifiedCount");
        Assert.Equal((LaunchStatus.Preparing, null), (launch.Status, launch.LiveSince));
    }

    [Fact]
    public void BR_23_ready_requires_the_threshold_and_live_is_reached_only_from_ready()
    {
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, minCertifiedStaff: 2);

        launch.RecomputeCoverage(2);
        Assert.Equal((LaunchStatus.Ready, true), (launch.Status, launch.CoverageMet));

        launch.GoLive(Now);
        Assert.Equal((LaunchStatus.Live, Now), (launch.Status, launch.LiveSince));
        Assert.Equal(BranchLaunchStatus.StateRule, Refused(() => launch.GoLive(Now)).Rule); // LIVE is not READY
    }

    [Fact]
    public void BR_23_ready_is_lost_again_when_certified_staff_are()
    {
        // Two certified, then one of them is flagged for re-certification before the branch opens.
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, minCertifiedStaff: 2);
        launch.RecomputeCoverage(2);

        launch.RecomputeCoverage(1);

        Assert.Equal((LaunchStatus.Preparing, false, 1), (launch.Status, launch.CoverageMet, launch.CertifiedCount));
        Assert.Equal("BR-23", Refused(() => launch.GoLive(Now)).Rule);
    }

    [Fact]
    public void BR_23_the_gate_of_a_pilot_counts_the_certified_staff_at_the_moment_of_going_live()
    {
        var pilot = Running();
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, 2);
        launch.RecomputeCoverage(2); // READY when it was last counted...

        // ...but only one is certified now.
        var refusal = Refused(() => pilot.GoLive(B1, launch, certifiedCount: 1, Now));

        Assert.Equal("BR-23", refusal.Rule);
        Assert.Equal(LaunchStatus.Preparing, launch.Status);
        Assert.Equal(PilotBranchState.Preparing, pilot.BranchOf(B1).ReadinessState);
        Assert.Null(pilot.BranchOf(B1).WentLiveAt);
    }

    [Fact]
    public void Branch_of_a_running_pilot_goes_live_through_the_gate()
    {
        var pilot = Running();
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, 2);
        pilot.RecomputeReadiness(B1, 2);
        Assert.Equal(PilotBranchState.Ready, pilot.BranchOf(B1).ReadinessState);

        var previous = pilot.GoLive(B1, launch, certifiedCount: 2, Now);

        Assert.Null(previous);
        Assert.Equal((LaunchStatus.Live, VersionId, Now, true), (launch.Status, launch.RecipeVersionId!.Value, launch.LiveSince!.Value, launch.CoverageMet));
        Assert.Equal((PilotBranchState.Live, Now), (pilot.BranchOf(B1).ReadinessState, pilot.BranchOf(B1).WentLiveAt));
        Assert.Equal(PilotBranchState.Preparing, pilot.BranchOf(B2).ReadinessState); // each branch has its own gate

        // Losing coverage later does not close a branch that is live, and it is not opened twice.
        pilot.RecomputeReadiness(B1, 0);
        Assert.Equal(PilotBranchState.Live, pilot.BranchOf(B1).ReadinessState);
        Assert.Equal(PilotProgram.StateRule, Refused(() => pilot.GoLive(B1, launch, 2, Now)).Rule);
    }

    [Fact]
    public void Gate_opens_only_while_the_pilot_is_running_and_only_for_its_own_branches()
    {
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, 2);

        Assert.Equal(PilotProgram.StateRule, Refused(() => Pilot().GoLive(B1, launch, 2, Now)).Rule);
        Assert.Equal(ErrorKind.NotFound, Refused(() => Running().GoLive(404, BranchLaunchStatus.Plan(404, RecipeId, VersionId, 2), 2, Now)).Kind);
        Assert.Equal(LaunchStatus.Preparing, launch.Status);
    }

    [Fact]
    public void BR_23_a_branch_selling_an_earlier_version_moves_to_the_pilot_version_only_when_covered_on_it()
    {
        // The branch sells version 300 of the drink; the pilot tests version 310 there.
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, recipeVersionId: 300, 2);
        launch.RecomputeCoverage(2);
        launch.GoLive(WentLive);
        var pilot = Running();

        Assert.Equal("BR-23", Refused(() => pilot.GoLive(B1, launch, certifiedCount: 1, Now)).Rule);
        Assert.Equal((LaunchStatus.Live, 300), (launch.Status, launch.RecipeVersionId!.Value)); // still selling what it sold

        var previous = pilot.GoLive(B1, launch, certifiedCount: 2, Now);

        Assert.Equal(300, previous);
        Assert.Equal((LaunchStatus.Live, VersionId, WentLive, true), (launch.Status, launch.RecipeVersionId!.Value, launch.LiveSince!.Value, launch.CoverageMet));
        Assert.Equal(PilotBranchState.Live, pilot.BranchOf(B1).ReadinessState);
    }

    [Fact]
    public void Drink_withdrawn_at_a_branch_is_planned_again_from_the_start()
    {
        var launch = BranchLaunchStatus.Plan(B1, RecipeId, 300, 2);
        launch.RecomputeCoverage(2);
        launch.GoLive(WentLive);
        launch.Withdraw();

        launch.Replan(VersionId, minCertifiedStaff: 3);

        Assert.Equal((LaunchStatus.Preparing, VersionId, 3, 0, false, null),
            (launch.Status, launch.RecipeVersionId!.Value, launch.MinCertifiedStaff, launch.CertifiedCount, launch.CoverageMet, launch.LiveSince));
        // A drink that is on sale is not planned again: it changes version through the gate.
        var live = BranchLaunchStatus.LiveForExistingRecipe(B1, RecipeId, 300, 2, WentLive);
        Assert.Equal(BranchLaunchStatus.StateRule, Refused(() => live.Replan(VersionId, 2)).Rule);
    }

    // ---------------------------------------------------------------- BR-36

    [Fact]
    public void BR_36_an_existing_drink_is_created_live_with_coverage_not_met_and_is_not_blocked()
    {
        var launch = BranchLaunchStatus.LiveForExistingRecipe(B1, RecipeId, VersionId, minCertifiedStaff: 2, Now);

        Assert.Equal((LaunchStatus.Live, false, 0, Now), (launch.Status, launch.CoverageMet, launch.CertifiedCount, launch.LiveSince));

        // Counting its certified staff changes the warning, never the status.
        launch.RecomputeCoverage(1);
        Assert.Equal((LaunchStatus.Live, false), (launch.Status, launch.CoverageMet));
        launch.RecomputeCoverage(2);
        Assert.Equal((LaunchStatus.Live, true), (launch.Status, launch.CoverageMet));
        launch.RecomputeCoverage(0);
        Assert.Equal((LaunchStatus.Live, false), (launch.Status, launch.CoverageMet));
    }

    [Fact]
    public void BR_36_a_drink_being_launched_takes_the_other_path_through_the_gate()
    {
        // The same threshold, the same count of zero: the new drink is held back, the existing one is on sale.
        var launching = BranchLaunchStatus.Plan(B1, RecipeId, VersionId, minCertifiedStaff: 2);
        var existing = BranchLaunchStatus.LiveForExistingRecipe(B1, 99, 500, minCertifiedStaff: 2, Now);

        Assert.Equal((LaunchStatus.Preparing, LaunchStatus.Live), (launching.Status, existing.Status));
        Assert.Equal("BR-23", Refused(() => launching.GoLive(Now)).Rule);
    }

    [Fact]
    public void BR_36_an_existing_drink_skips_the_pilot()
    {
        var refusal = Refused(() => Pilot(recipe: Drink(RecipeOrigin.Existing)));

        Assert.Equal((ErrorKind.RuleViolation, "BR-36"), (refusal.Kind, refusal.Rule));
    }

    // ---------------------------------------------------------------- UC-24: the evaluator

    [Fact]
    public void Evaluation_gives_a_verdict_per_criterion_per_branch_and_overall()
    {
        var pilot = Ended(Absolute40, Relative60, Retention25);
        // B1 sells 50 a day, B2 45; the control sells 70 a day at both.
        var sales = Daily(B1, Start, End, 50).Concat(Daily(B2, Start, End, 45));
        var control = Daily(B1, Start, End, 70).Concat(Daily(B2, Start, End, 70));

        var evaluation = Evaluate(pilot, sales, control);

        Assert.Equal((5, PilotState.Ended, Start, End, true, Verdict.Pass),
            (evaluation.PilotId, evaluation.State, evaluation.From, evaluation.To, evaluation.CoverageComplete, evaluation.Overall));
        Assert.Equal(
        [
            new CriterionResult(CriterionType.Absolute, 40, 47.5m, Verdict.Pass),   // (50 + 45) / 2
            new CriterionResult(CriterionType.Relative, 60, 67.9m, Verdict.Pass),   // 2660 of 3920
            new CriterionResult(CriterionType.Retention, 25, 0m, Verdict.Pass),     // no drop at all
        ], evaluation.Criteria);

        var first = evaluation.ByBranch[0];
        Assert.Equal((B1, 28, 28, 1400, 50.0m, true, Verdict.Pass),
            (first.BranchId, first.ExpectedDays, first.TradingDays, first.CupsTotal, first.CupsPerDay, first.CoverageComplete, first.Verdict));
        Assert.Equal([50.0m, 71.4m, 0m], first.Criteria.Select(c => c.Actual));
        Assert.Equal([1, 2, 3, 4], evaluation.ByWeek.Select(w => w.Week));
        Assert.All(evaluation.ByWeek, week => Assert.Equal(665, week.Cups)); // 7 days of 50 + 45
        Assert.Equal((Start, Start.AddDays(6)), (evaluation.ByWeek[0].From, evaluation.ByWeek[0].To));
    }

    [Fact]
    public void Absolute_criterion_fails_below_the_cups_per_day_per_branch()
    {
        var pilot = Ended(Absolute40);

        var evaluation = Evaluate(pilot, Daily(B1, Start, End, 50).Concat(Daily(B2, Start, End, 20)));

        // 35 a day across both branches: under 40, although one branch is well over.
        Assert.Equal(new CriterionResult(CriterionType.Absolute, 40, 35.0m, Verdict.Fail), Assert.Single(evaluation.Criteria));
        Assert.Equal(Verdict.Fail, evaluation.Overall);
        Assert.Equal([Verdict.Pass, Verdict.Fail], evaluation.ByBranch.Select(b => b.Verdict));
    }

    [Fact]
    public void Relative_criterion_measures_against_the_control_drink_on_the_same_branches_and_days()
    {
        var pilot = Ended(Relative60);
        var sales = Daily(B1, Start, End, 30).Concat(Daily(B2, Start, End, 30));
        // The control also sold before and after the pilot, and at a branch outside it: none of that counts.
        var control = Daily(B1, Start.AddDays(-10), End.AddDays(10), 60).Concat(Daily(B2, Start, End, 60)).Concat(Daily(55, Start, End, 500));

        var evaluation = Evaluate(pilot, sales, control);

        Assert.Equal(new CriterionResult(CriterionType.Relative, 60, 50.0m, Verdict.Fail), Assert.Single(evaluation.Criteria));
    }

    [Fact]
    public void Relative_criterion_cannot_be_judged_when_the_control_sold_nothing()
    {
        var evaluation = Evaluate(Ended(Relative60), Daily(B1, Start, End, 30).Concat(Daily(B2, Start, End, 30)));

        Assert.Equal(new CriterionResult(CriterionType.Relative, 60, null, Verdict.Incomplete), Assert.Single(evaluation.Criteria));
        Assert.Equal(Verdict.Incomplete, evaluation.Overall);
    }

    [Fact]
    public void Retention_criterion_measures_the_drop_from_the_first_half_of_the_period_to_the_second()
    {
        var halfway = Start.AddDays(14);
        IEnumerable<DailyCups> Sales(int firstHalf, int secondHalf) =>
            new[] { B1, B2 }.SelectMany(branch =>
                Daily(branch, Start, halfway.AddDays(-1), firstHalf).Concat(Daily(branch, halfway, End, secondHalf)));

        // 60 a day, then 48: a drop of 20 percent, inside the 25 allowed.
        Assert.Equal(new CriterionResult(CriterionType.Retention, 25, 20.0m, Verdict.Pass),
            Assert.Single(Evaluate(Ended(Retention25), Sales(60, 48)).Criteria));
        // 60 a day, then 40: a third gone.
        Assert.Equal(new CriterionResult(CriterionType.Retention, 25, 33.3m, Verdict.Fail),
            Assert.Single(Evaluate(Ended(Retention25), Sales(60, 40)).Criteria));
        // A drink that grew has a negative drop, which passes.
        Assert.Equal(new CriterionResult(CriterionType.Retention, 25, -50.0m, Verdict.Pass),
            Assert.Single(Evaluate(Ended(Retention25), Sales(40, 60)).Criteria));
    }

    [Fact]
    public void Branch_with_missing_trading_days_is_reported_as_incomplete_and_not_scored()
    {
        var pilot = Ended(Absolute40);
        // B2 has no record for the last three days. What it does have is poor, and must not count.
        var sales = Daily(B1, Start, End, 50).Concat(Daily(B2, Start, End.AddDays(-3), 10));

        var evaluation = Evaluate(pilot, sales);

        Assert.False(evaluation.CoverageComplete);
        var incomplete = evaluation.ByBranch.Single(b => b.BranchId == B2);
        Assert.Equal((28, 25, false, Verdict.Incomplete), (incomplete.ExpectedDays, incomplete.TradingDays, incomplete.CoverageComplete, incomplete.Verdict));
        Assert.Empty(incomplete.Criteria);
        Assert.Equal(250, incomplete.CupsTotal); // reported, though not scored
        // The pilot is judged on the branch whose figures are whole: 50, not (50 + 10) / 2.
        Assert.Equal(new CriterionResult(CriterionType.Absolute, 40, 50.0m, Verdict.Pass), Assert.Single(evaluation.Criteria));
        Assert.Equal(Verdict.Pass, evaluation.Overall);
    }

    [Fact]
    public void Pilot_with_no_branch_that_can_be_scored_has_no_verdict()
    {
        var evaluation = Evaluate(Ended(Absolute40), Daily(B1, Start, End.AddDays(-1), 80));

        Assert.Equal((false, Verdict.Incomplete), (evaluation.CoverageComplete, evaluation.Overall));
        Assert.Equal(Verdict.Incomplete, Assert.Single(evaluation.Criteria).Verdict);
        Assert.All(evaluation.ByBranch, branch => Assert.Equal(Verdict.Incomplete, branch.Verdict));
    }

    [Fact]
    public void Branch_is_expected_to_trade_from_the_day_it_went_live()
    {
        // B1 is live from the first day; B2 opens a week into the pilot.
        var pilot = Running(Absolute40);
        pilot.GoLive(B1, BranchLaunchStatus.Plan(B1, RecipeId, VersionId, 2), 2, WentLive);
        pilot.GoLive(B2, BranchLaunchStatus.Plan(B2, RecipeId, VersionId, 2), 2, WentLive.AddDays(7));
        pilot.EndIfDue(AfterTheEnd);

        var evaluation = Evaluate(pilot, Daily(B1, Start, End, 50).Concat(Daily(B2, Start.AddDays(7), End, 60)));

        Assert.Equal([28, 21], evaluation.ByBranch.Select(b => b.ExpectedDays));
        Assert.True(evaluation.CoverageComplete);
        Assert.Equal(54.3m, Assert.Single(evaluation.Criteria).Actual); // 2660 cups over 49 branch-days
    }

    [Fact]
    public void Branch_that_never_went_live_is_incomplete()
    {
        var pilot = Running(Absolute40);
        pilot.GoLive(B1, BranchLaunchStatus.Plan(B1, RecipeId, VersionId, 2), 2, WentLive);
        pilot.EndIfDue(AfterTheEnd);

        var evaluation = Evaluate(pilot, Daily(B1, Start, End, 50));

        var never = evaluation.ByBranch.Single(b => b.BranchId == B2);
        Assert.Equal((0, 0, false, Verdict.Incomplete), (never.ExpectedDays, never.TradingDays, never.CoverageComplete, never.Verdict));
        Assert.False(evaluation.CoverageComplete);
        Assert.Equal(Verdict.Pass, evaluation.Overall); // on the branch that ran
    }

    [Fact]
    public void Running_pilot_is_evaluated_up_to_today()
    {
        var pilot = RunningAndLive(Absolute40, Retention25);
        var tenthDay = Start.AddDays(9);

        var evaluation = Evaluate(pilot, Daily(B1, Start, tenthDay, 50).Concat(Daily(B2, Start, tenthDay, 50)), today: tenthDay);

        Assert.Equal(PilotState.Running, evaluation.State);
        Assert.All(evaluation.ByBranch, branch => Assert.Equal((10, 10, true), (branch.ExpectedDays, branch.TradingDays, branch.CoverageComplete)));
        Assert.Equal(new CriterionResult(CriterionType.Absolute, 40, 50.0m, Verdict.Pass), evaluation.Criteria[0]);
        // The second half has not begun: retention cannot be judged yet.
        Assert.Equal(new CriterionResult(CriterionType.Retention, 25, null, Verdict.Incomplete), evaluation.Criteria[1]);
        Assert.Equal(Verdict.Incomplete, evaluation.Overall);
        Assert.Equal([1, 2], evaluation.ByWeek.Select(w => w.Week));
    }

    // ---------------------------------------------------------------- BR-27

    [Fact]
    public void BR_27_a_decision_is_recorded_only_against_an_ended_pilot()
    {
        var cancelled = Pilot();
        cancelled.Cancel();

        Assert.All(new[] { Pilot(), Running(), cancelled }, pilot =>
        {
            var refusal = Refused(() => pilot.Decide(LaunchDecisionType.Rollout, "{}", Manager, Now));

            Assert.Equal((ErrorKind.RuleViolation, "BR-27"), (refusal.Kind, refusal.Rule));
            Assert.Null(pilot.Decision);
        });
    }

    [Fact]
    public void BR_27_the_decision_stores_the_figures_as_evaluated_at_that_moment_and_is_taken_once()
    {
        var pilot = Ended();
        const string figures = """{"overall":"PASS","criteria":[{"type":"ABSOLUTE","target":40,"actual":51.2,"verdict":"PASS"}]}""";

        var decision = pilot.Decide(LaunchDecisionType.Rollout, figures, Manager, Now);

        Assert.Same(decision, pilot.Decision);
        Assert.Equal((LaunchDecisionType.Rollout, figures, Manager, Now), (decision.Decision, decision.EvaluatedJson, decision.DecidedBy, decision.DecidedAt));
        Assert.Equal(PilotState.Ended, pilot.State);

        // It is not changed, by another decision or by anything else: the record has no way to change.
        var refusal = Refused(() => pilot.Decide(LaunchDecisionType.Discontinue, "{}", Manager, Now.AddDays(1)));
        Assert.Equal("BR-27", refusal.Rule);
        Assert.Equal((LaunchDecisionType.Rollout, figures), (pilot.Decision!.Decision, pilot.Decision.EvaluatedJson));
        Assert.DoesNotContain(typeof(LaunchDecision).GetProperties(), property => property.SetMethod is { IsPublic: true });
        Assert.DoesNotContain(typeof(LaunchDecision).GetMethods(), m => m.DeclaringType == typeof(LaunchDecision) && !m.IsSpecialName);
    }
}
