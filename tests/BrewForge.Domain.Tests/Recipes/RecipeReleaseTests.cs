using System.Text.RegularExpressions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>UC-09 and UC-10: review, release, supersede. BR-01 to BR-04 and BR-12.</summary>
public sealed class RecipeReleaseTests
{
    private const long Manager = 9;
    private const long OtherManager = 10;
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 30, 0, TimeSpan.Zero);

    private static long _nextId = 1000;

    /// <summary>A VALIDATED version whose steps have ids, as they would after being stored.</summary>
    private static RecipeVersion Validated(int versionNo = 1, long recipeId = 12)
    {
        var version = RecipeVersion.CreateDraft(recipeId, versionNo, Author);
        version.ReplaceContent(
        [
            Step(1, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, 18m, "g")]),
            Step(2, "Add the milk", seconds: 20, uses: [(Milk, 120m, "ml")], dependsOn: [1]),
        ], Author);
        foreach (var step in version.Steps) WithId(step, Interlocked.Increment(ref _nextId));
        version.Submit(version.Validate(Catalog()));
        return version;
    }

    private static ValidationReport Passing(RecipeVersion version) => version.Validate(Catalog());

    private static ValidationReport Failing() =>
        new([new CheckResult(CheckType.Equipment, [new Violation(1, 1, "BR-10", "out of range", "15.0 - 25.0", "40.0")]),
            new CheckResult(CheckType.Ordering, []), new CheckResult(CheckType.Ingredient, [])]);

    private static RecipeVersion Released(int versionNo = 1, long recipeId = 12)
    {
        var version = Validated(versionNo, recipeId);
        var release = RecipeRelease.Prepare(version, null, Passing(version), Manager, versionNo, Now);
        release.SupersedePrevious();
        release.Seal();
        return version;
    }

    private static StepDecision[] AcceptAll(RecipeVersion version) =>
        [.. version.Steps.Select(step => new StepDecision(step.Id, ReviewAction.Accept, null))];

    // ---------------------------------------------------------------- review

    [Fact]
    public void Accepting_every_step_leaves_the_version_validated()
    {
        var version = Validated();

        var outcome = version.Review(AcceptAll(version), Manager);

        Assert.Equal(ReviewOutcome.Accepted, outcome);
        Assert.Equal(VersionState.Validated, version.State);
        Assert.Equal(Author, version.CreatedBy);
    }

    [Fact]
    public void Editing_a_step_replaces_its_text_and_puts_the_version_back_to_draft()
    {
        var version = Validated();
        var first = version.OrderedSteps()[0];
        var second = version.OrderedSteps()[1];

        var outcome = version.Review(
        [
            new StepDecision(first.Id, ReviewAction.Edit, "Brew the oolong for exactly eight minutes"),
            new StepDecision(second.Id, ReviewAction.Accept, null),
        ], Manager);

        Assert.Equal(ReviewOutcome.Edited, outcome);
        Assert.Equal("Brew the oolong for exactly eight minutes", first.ActionText);
        Assert.Equal("Add the milk", second.ActionText);
        Assert.Equal(VersionState.Draft, version.State);
        // BR-12: whoever last edited the content is its author on record.
        Assert.Equal(Manager, version.CreatedBy);
    }

    [Fact]
    public void Rejecting_one_step_rejects_the_version_and_changes_no_text()
    {
        var version = Validated();
        var steps = version.OrderedSteps();

        var outcome = version.Review(
        [
            new StepDecision(steps[0].Id, ReviewAction.Edit, "An edit that must not be applied"),
            new StepDecision(steps[1].Id, ReviewAction.Reject, null),
        ], Manager);

        Assert.Equal(ReviewOutcome.Rejected, outcome);
        Assert.Equal(VersionState.Rejected, version.State);
        Assert.Equal("Brew the oolong", steps[0].ActionText);
        Assert.Equal(Author, version.CreatedBy);
    }

    [Fact]
    public void Rejected_version_is_reopened_as_a_draft_by_editing_it()
    {
        var version = Validated();
        version.Review([.. version.Steps.Select(s => new StepDecision(s.Id, ReviewAction.Reject, null))], Manager);

        version.ReplaceContent([Step(1, "Reworked")], Author);

        Assert.Equal(VersionState.Draft, version.State);
    }

    [Fact]
    public void Review_must_decide_every_step_exactly_once()
    {
        var version = Validated();
        var steps = version.OrderedSteps();

        var missing = Assert.Throws<DomainException>(() =>
            version.Review([new StepDecision(steps[0].Id, ReviewAction.Accept, null)], Manager));
        var twice = Assert.Throws<DomainException>(() => version.Review(
        [
            new StepDecision(steps[0].Id, ReviewAction.Accept, null),
            new StepDecision(steps[0].Id, ReviewAction.Accept, null),
            new StepDecision(steps[1].Id, ReviewAction.Accept, null),
        ], Manager));
        var foreign = Assert.Throws<DomainException>(() => version.Review(
            [.. AcceptAll(version), new StepDecision(424242, ReviewAction.Accept, null)], Manager));

        Assert.All([missing, twice, foreign], refusal =>
        {
            Assert.Equal(ErrorKind.Validation, refusal.Kind);
            Assert.Contains(refusal.Details, d => d.Field == "decisions");
        });
        Assert.Equal(VersionState.Validated, version.State);
    }

    [Fact]
    public void Edit_without_a_text_is_refused()
    {
        var version = Validated();
        var steps = version.OrderedSteps();

        var refusal = Assert.Throws<DomainException>(() => version.Review(
        [
            new StepDecision(steps[0].Id, ReviewAction.Edit, "  "),
            new StepDecision(steps[1].Id, ReviewAction.Accept, null),
        ], Manager));

        Assert.Contains(refusal.Details, d => d.Field == "decisions[0].editedText");
    }

    [Fact]
    public void Only_a_validated_version_can_be_reviewed()
    {
        var draft = Draft(Step(1));

        var refusal = Assert.Throws<DomainException>(() => draft.Review([], Manager));

        Assert.Equal(RecipeVersion.StateRule, refusal.Rule);
    }

    // ---------------------------------------------------------------- release

    [Fact]
    public void Release_seals_the_version()
    {
        var version = Validated();

        var release = RecipeRelease.Prepare(version, null, Passing(version), Manager, highestVersionNo: 1, Now);
        release.SupersedePrevious();
        release.Seal();

        Assert.Equal(VersionState.Released, version.State);
        Assert.True(version.IsImmutable);
        Assert.Equal(Manager, version.ApprovedBy);
        Assert.Equal(Now, version.ReleasedAt);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), version.ContentHash);
        Assert.Equal(version.ComputeContentHash(), version.ContentHash);
    }

    [Fact]
    public void BR_12_the_approver_may_not_be_the_author()
    {
        var version = Validated();

        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(version, null, Passing(version), approverId: Author, 1, Now));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-12", refusal.Rule);
        Assert.Equal("MSG-E09", refusal.Code);
        Assert.Contains(refusal.Details, d => d is { Field: "approvedBy", Issue: "equals createdBy" });
        // Nothing changed.
        Assert.Equal(VersionState.Validated, version.State);
        Assert.False(version.IsImmutable);
        Assert.Null(version.ApprovedBy);
    }

    [Fact]
    public void BR_12_a_manager_who_edited_the_draft_in_review_may_not_release_it()
    {
        var version = Validated();
        var steps = version.OrderedSteps();
        version.Review(
        [
            new StepDecision(steps[0].Id, ReviewAction.Edit, "Brew the oolong gently"),
            new StepDecision(steps[1].Id, ReviewAction.Accept, null),
        ], Manager);
        version.Submit(Passing(version));

        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(version, null, Passing(version), Manager, 1, Now));
        Assert.Equal("BR-12", refusal.Rule);

        // Another manager may.
        RecipeRelease.Prepare(version, null, Passing(version), OtherManager, 1, Now).Seal();
        Assert.Equal(OtherManager, version.ApprovedBy);
    }

    [Fact]
    public void Release_is_refused_with_MSG_E08_when_the_fresh_validation_fails()
    {
        var version = Validated();

        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(version, null, Failing(), Manager, 1, Now));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("MSG-E08", refusal.Code);
        Assert.Equal("BR-08", refusal.Rule);
        Assert.Equal(VersionState.Validated, version.State);
    }

    [Fact]
    public void Revalidation_is_checked_before_separation_of_duty()
    {
        // UC-10 fixes the order: a version that no longer passes is refused for that, whoever asks.
        var version = Validated();

        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(version, null, Failing(), approverId: Author, 1, Now));

        Assert.Equal("MSG-E08", refusal.Code);
    }

    [Theory]
    [InlineData(VersionState.Draft)]
    [InlineData(VersionState.Rejected)]
    public void Only_a_validated_version_can_be_released(VersionState state)
    {
        var version = Validated();
        version.Review([.. version.Steps.Select(s => new StepDecision(s.Id, ReviewAction.Reject, null))], Manager);
        if (state == VersionState.Draft) version.ReplaceContent([Step(1)], Author);
        Assert.Equal(state, version.State);

        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(version, null, Passing(version), Manager, 1, Now));

        Assert.Equal(RecipeVersion.StateRule, refusal.Rule); // BR-05: never straight from a draft
    }

    // ---------------------------------------------------------------- BR-01

    [Fact]
    public void BR_01_nothing_about_a_released_version_can_be_changed()
    {
        var version = Released();
        var hash = version.ContentHash;

        Action[] attempts =
        [
            () => version.ReplaceContent([Step(1, "Tampered")], Author),
            () => version.Submit(Passing(version)),
            () => version.Review(AcceptAll(version), Manager),
            () => RecipeRelease.Prepare(version, null, Passing(version), OtherManager, 1, Now),
            version.EnsureMutable,
        ];

        Assert.All(attempts, attempt =>
        {
            var refusal = Assert.Throws<DomainException>(attempt);
            Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
            Assert.Equal("BR-01", refusal.Rule);
            Assert.Equal("MSG-E06", refusal.Code);
        });
        Assert.Equal(VersionState.Released, version.State);
        Assert.Equal(hash, version.ComputeContentHash());
        Assert.Equal(2, version.Steps.Count);
    }

    // ---------------------------------------------------------------- BR-02

    [Fact]
    public void BR_02_releasing_a_new_version_supersedes_the_one_released_before()
    {
        var first = Released(versionNo: 1);
        var second = Validated(versionNo: 2);
        var later = Now.AddDays(3);

        var release = RecipeRelease.Prepare(second, first, Passing(second), Manager, highestVersionNo: 2, later);
        release.SupersedePrevious();
        release.Seal();

        Assert.Equal(VersionState.Superseded, first.State);
        Assert.Equal(later, first.SupersededAt);
        Assert.True(first.IsImmutable);           // retained, and still sealed
        Assert.NotNull(first.ContentHash);
        Assert.Equal(VersionState.Released, second.State);
        Assert.Single(new[] { first, second }, v => v.State == VersionState.Released);
    }

    [Fact]
    public void BR_02_a_version_cannot_be_sealed_while_another_is_still_released()
    {
        var first = Released(versionNo: 1);
        var second = Validated(versionNo: 2);
        var release = RecipeRelease.Prepare(second, first, Passing(second), Manager, 2, Now);

        var refusal = Assert.Throws<DomainException>(release.Seal); // without SupersedePrevious()

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-02", refusal.Rule);
        Assert.Equal(VersionState.Validated, second.State);
        Assert.Equal(VersionState.Released, first.State);
    }

    [Fact]
    public void Superseded_is_the_end_of_the_line()
    {
        var first = Released(versionNo: 1);
        var second = Validated(versionNo: 2);
        var release = RecipeRelease.Prepare(second, first, Passing(second), Manager, 2, Now);
        release.SupersedePrevious();
        release.Seal();

        // A superseded version cannot be released again: a rollback copies it instead (BR-04).
        var refusal = Assert.Throws<DomainException>(() =>
            RecipeRelease.Prepare(first, second, Passing(first), OtherManager, 2, Now));
        Assert.Equal("BR-01", refusal.Rule);
        Assert.Equal(VersionState.Superseded, first.State);
    }

    // ---------------------------------------------------------------- BR-03

    [Fact]
    public void BR_03_a_version_normally_keeps_the_number_it_was_drafted_under()
    {
        var first = Released(versionNo: 1);
        var second = Validated(versionNo: 2);

        var release = RecipeRelease.Prepare(second, first, Passing(second), Manager, highestVersionNo: 2, Now);

        Assert.Equal(2, release.VersionNo);
    }

    [Fact]
    public void BR_03_a_draft_released_out_of_order_takes_the_next_free_number()
    {
        // Drafts 2 and 3 exist; 3 is released first. Releasing 2 afterwards
        // must not put a lower number after a higher one.
        var third = Released(versionNo: 3);
        var second = Validated(versionNo: 2);

        var release = RecipeRelease.Prepare(second, third, Passing(second), Manager, highestVersionNo: 3, Now);
        release.SupersedePrevious();
        release.Seal();

        Assert.Equal(4, second.VersionNo);
        Assert.True(second.VersionNo > third.VersionNo);
    }

    // ---------------------------------------------------------------- content hash

    [Fact]
    public void Same_content_has_the_same_hash_and_different_content_a_different_one()
    {
        var a = Draft(Step(1, "Brew", "TEA_BREWER", 480, [(Oolong, 18m, "g"), (Water, 300m, "ml")]), Step(2, "Serve", dependsOn: [1]));
        var sameInAnotherOrder = Draft(Step(2, "Serve", dependsOn: [1]), Step(1, "Brew", "TEA_BREWER", 480, [(Water, 300m, "ml"), (Oolong, 18m, "g")]));
        var oneGramMore = Draft(Step(1, "Brew", "TEA_BREWER", 480, [(Oolong, 19m, "g"), (Water, 300m, "ml")]), Step(2, "Serve", dependsOn: [1]));
        var noDependency = Draft(Step(1, "Brew", "TEA_BREWER", 480, [(Oolong, 18m, "g"), (Water, 300m, "ml")]), Step(2, "Serve"));

        Assert.Equal(a.ComputeContentHash(), sameInAnotherOrder.ComputeContentHash());
        Assert.NotEqual(a.ComputeContentHash(), oneGramMore.ComputeContentHash());
        Assert.NotEqual(a.ComputeContentHash(), noDependency.ComputeContentHash());
    }

    // ---------------------------------------------------------------- the state model

    [Fact]
    public void Ai_repair_limit_is_three()
    {
        AiRepairPolicy.EnsureAnotherRepairAllowed(0);
        AiRepairPolicy.EnsureAnotherRepairAllowed(2);

        var refusal = Assert.Throws<DomainException>(() => AiRepairPolicy.EnsureAnotherRepairAllowed(3));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal(AiRepairPolicy.Rule, refusal.Rule);
    }
}
