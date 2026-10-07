using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

public sealed class RecipeVersionTests
{
    [Fact]
    public void New_version_is_a_mutable_draft()
    {
        // BR-05: every draft, authored or AI-generated, starts in DRAFT.
        var version = RecipeVersion.CreateDraft(recipeId: 12, versionNo: 3, authorId: Author);

        Assert.Equal(VersionState.Draft, version.State);
        Assert.False(version.IsImmutable);
        Assert.Null(version.ApprovedBy);
        Assert.Null(version.ReleasedAt);
        Assert.Equal(3, version.VersionNo);
        Assert.Empty(version.Steps);
    }

    [Fact]
    public void Content_is_stored_as_steps_with_ingredients_and_dependencies()
    {
        var version = ValidOolongMilkTea();

        var steps = version.OrderedSteps();
        Assert.Equal([1, 2, 3], steps.Select(s => s.StepOrder));
        Assert.Equal("TEA_BREWER", steps[0].EquipmentClass);
        Assert.Equal(480, steps[0].DurationSeconds);
        Assert.Equal([(Oolong, 18m, "g"), (Water, 300m, "ml")],
            steps[0].Ingredients.Select(i => (i.IngredientId, i.Quantity, i.Unit)));
        Assert.Empty(steps[0].Dependencies);
        Assert.Same(steps[0], Assert.Single(steps[1].Dependencies).DependsOnStep);
        Assert.Same(steps[1], Assert.Single(steps[2].Dependencies).DependsOnStep);
    }

    [Fact]
    public void Replacing_the_content_discards_the_previous_steps()
    {
        var version = ValidOolongMilkTea();

        version.ReplaceContent([Step(1, "Only step")], Author);

        Assert.Equal("Only step", Assert.Single(version.Steps).ActionText);
    }

    [Fact]
    public void Whoever_last_edits_the_content_becomes_the_author_on_record()
    {
        // BR-12 speaks of the user who "authors or last edits" the draft.
        var version = ValidOolongMilkTea();

        version.ReplaceContent([Step(1, "Edited by someone else")], editorId: 77);

        Assert.Equal(77, version.CreatedBy);
    }

    [Fact]
    public void Validated_version_cannot_be_edited_directly()
    {
        var version = ValidOolongMilkTea();
        version.Submit(version.Validate(Catalog()));

        var refusal = Assert.Throws<DomainException>(() => version.ReplaceContent([Step(1)], Author));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal(RecipeVersion.StateRule, refusal.Rule);
        Assert.Equal(VersionState.Validated, version.State);
        Assert.Equal(3, version.Steps.Count);
    }

    [Fact]
    public void Validated_version_cannot_be_submitted_again()
    {
        var version = ValidOolongMilkTea();
        var report = version.Validate(Catalog());
        version.Submit(report);

        var refusal = Assert.Throws<DomainException>(() => version.Submit(report));

        Assert.Equal(RecipeVersion.StateRule, refusal.Rule);
    }

    // ---------------------------------------------------------------- structural rules

    [Fact]
    public void Step_depending_on_itself_is_rejected_under_BR_09()
    {
        var version = RecipeVersion.CreateDraft(12, 1, Author);

        var refusal = Assert.Throws<DomainException>(() =>
            version.ReplaceContent([Step(1), Step(2, dependsOn: [2])], Author));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-09", refusal.Rule);
        Assert.Equal("MSG-E07", refusal.Code);
        Assert.Empty(version.Steps);
    }

    [Theory]
    [InlineData(new[] { 1, 3 })]    // gap
    [InlineData(new[] { 2, 3 })]    // does not start at 1
    [InlineData(new[] { 1, 1 })]    // duplicate
    [InlineData(new[] { 0, 1 })]    // not 1-based
    public void Step_numbering_must_be_contiguous_from_one(int[] orders)
    {
        var version = RecipeVersion.CreateDraft(12, 1, Author);

        var refusal = Assert.Throws<DomainException>(() =>
            version.ReplaceContent([.. orders.Select(order => Step(order))], Author));

        Assert.Equal(ErrorKind.Validation, refusal.Kind);
        Assert.Contains(refusal.Details, d => d.Field == "steps");
    }

    [Fact]
    public void Steps_may_be_supplied_in_any_order()
    {
        var version = Draft(Step(2, "Second", dependsOn: [1]), Step(1, "First"));

        Assert.Equal(["First", "Second"], version.OrderedSteps().Select(s => s.ActionText));
    }

    [Fact]
    public void Recipe_may_have_forty_steps_but_not_forty_one()
    {
        var version = RecipeVersion.CreateDraft(12, 1, Author);

        version.ReplaceContent([.. Enumerable.Range(1, 40).Select(order => Step(order))], Author);
        Assert.Equal(40, version.Steps.Count);

        var refusal = Assert.Throws<DomainException>(() =>
            version.ReplaceContent([.. Enumerable.Range(1, 41).Select(order => Step(order))], Author));
        Assert.Contains(refusal.Details, d => d.Field == "steps");
        Assert.Equal(40, version.Steps.Count); // the refused edit changed nothing
    }

    [Fact]
    public void Invalid_fields_are_reported_with_their_position_in_the_draft()
    {
        var version = RecipeVersion.CreateDraft(12, 1, Author);
        StepSpec[] steps =
        [
            Step(1, action: ""),
            Step(2, seconds: 0, uses: [(Oolong, 0m, "g"), (Milk, 1.2345m, "")]),
            Step(3, uses: [(Oolong, 1m, "g"), (Oolong, 2m, "g")], dependsOn: [9]),
        ];

        var refusal = Assert.Throws<DomainException>(() => version.ReplaceContent(steps, Author));

        Assert.Equal(
        [
            "steps[0].actionText",
            "steps[1].durationSeconds",
            "steps[1].ingredients[0].quantity",
            "steps[1].ingredients[1].quantity",
            "steps[1].ingredients[1].unit",
            "steps[2].ingredients",
            "steps[2].dependsOn",
        ], refusal.Details.Select(d => d.Field));
    }

    [Fact]
    public void Action_text_longer_than_its_column_is_rejected()
    {
        var version = RecipeVersion.CreateDraft(12, 1, Author);

        var refusal = Assert.Throws<DomainException>(() =>
            version.ReplaceContent([Step(1, action: new string('a', 501))], Author));

        Assert.Equal("MSG-E11", refusal.Code);
    }
}
