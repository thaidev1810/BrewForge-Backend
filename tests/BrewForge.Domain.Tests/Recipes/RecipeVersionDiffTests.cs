using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>
/// What changed between two versions of a recipe. Two versions share no
/// rows, so the steps are lined up by what they say.
/// </summary>
public sealed class RecipeVersionDiffTests
{
    private static StepSpec Brew(decimal grams = 18m, int seconds = 480, string? gate = "Water at 90 C",
        string action = "Brew the oolong", string? equipment = "TEA_BREWER", int order = 1) =>
        new(order, action, equipment, gate, seconds,
            [new IngredientSpec(Oolong, grams, "g"), new IngredientSpec(Water, 300m, "ml")], []);

    private static StepSpec Milk(int order = 2, int after = 1, decimal ml = 120m, string action = "Add the milk") =>
        new(order, action, null, "Pour down the side of the cup", 20, [new IngredientSpec(RecipeFixtures.Milk, ml, "ml")],
            [new DependencySpec(after, DependencyType.FinishToStart)]);

    private static StepSpec Garnish(int order = 3, int after = 2) =>
        new(order, "Garnish with peach", null, null, 15, [new IngredientSpec(Peach, 2m, "pcs")],
            [new DependencySpec(after, DependencyType.FinishToStart)]);

    private static RecipeVersionDiff Diff(StepSpec[] before, StepSpec[] after) =>
        RecipeVersionDiff.Between(Draft(before), Draft(after));

    private static StepChange Change(RecipeVersionDiff diff, string action) =>
        diff.Steps.Single(step => (step.After ?? step.Before)!.ActionText == action);

    [Fact]
    public void Two_versions_with_the_same_content_do_not_differ()
    {
        var diff = Diff([Brew(), Milk(), Garnish()], [Brew(), Milk(), Garnish()]);

        Assert.False(diff.HasChanges);
        Assert.All(diff.Steps, step => Assert.Equal((StepChangeKind.Unchanged, 0), (step.Kind, step.ChangedFields.Count)));
        Assert.Equal(3, diff.Count(StepChangeKind.Unchanged));
        Assert.Empty(diff.StepsToRelearn());
    }

    [Fact]
    public void Changed_quantity_is_reported_on_its_step_with_the_figure_before_and_after()
    {
        var diff = Diff([Brew(grams: 18m), Milk(), Garnish()], [Brew(grams: 16m), Milk(), Garnish()]);

        var brew = Change(diff, "Brew the oolong");
        Assert.Equal((StepChangeKind.Changed, 1, 2), (brew.Kind, diff.Count(StepChangeKind.Changed), diff.Count(StepChangeKind.Unchanged)));
        Assert.Equal([RecipeVersionDiff.Ingredients], brew.ChangedFields);
        // Only the ingredient that changed: the water is the same 300 ml.
        Assert.Equal(new IngredientChange(Oolong, 18m, "g", 16m, "g"), Assert.Single(brew.Ingredients));
        Assert.Equal(["Brew the oolong"], diff.StepsToRelearn().Select(step => step.ActionText));
    }

    [Fact]
    public void Every_field_of_a_step_that_differs_is_named()
    {
        var diff = Diff(
            [Brew(seconds: 480, gate: "Water at 90 C", equipment: "TEA_BREWER"), Milk()],
            [Brew(seconds: 420, gate: "Water at 85 C", equipment: "KETTLE"), Milk()]);

        Assert.Equal(
            [RecipeVersionDiff.EquipmentClass, RecipeVersionDiff.TechniqueGate, RecipeVersionDiff.DurationSeconds],
            Change(diff, "Brew the oolong").ChangedFields);
    }

    [Fact]
    public void Ingredient_that_only_one_version_uses_is_reported_with_one_side_empty()
    {
        var withPeach = new StepSpec(2, "Add the milk", null, "Pour down the side of the cup", 20,
            [new IngredientSpec(RecipeFixtures.Milk, 120m, "ml"), new IngredientSpec(Peach, 1m, "pcs")],
            [new DependencySpec(1, DependencyType.FinishToStart)]);

        var added = Change(Diff([Brew(), Milk()], [Brew(), withPeach]), "Add the milk");
        var dropped = Change(Diff([Brew(), withPeach], [Brew(), Milk()]), "Add the milk");

        Assert.Equal(new IngredientChange(Peach, null, null, 1m, "pcs"), Assert.Single(added.Ingredients));
        Assert.Equal(new IngredientChange(Peach, 1m, "pcs", null, null), Assert.Single(dropped.Ingredients));
    }

    [Fact]
    public void Inserted_step_is_added_and_the_steps_it_pushed_down_have_not_changed()
    {
        var rest = new StepSpec(2, "Rest the tea", null, null, 60, [], [new DependencySpec(1, DependencyType.FinishToStart)]);

        var diff = Diff([Brew(), Milk(), Garnish()], [Brew(), rest, Milk(order: 3, after: 2), Garnish(order: 4, after: 3)]);

        Assert.Equal(StepChangeKind.Added, Change(diff, "Rest the tea").Kind);
        Assert.Null(Change(diff, "Rest the tea").Before);
        // The garnish is step 4 now and still follows the milk: it is the same step.
        Assert.Equal(StepChangeKind.Unchanged, Change(diff, "Garnish with peach").Kind);
        // The milk used to follow the brewing and now follows the rest: what it waits for did change.
        Assert.Equal([RecipeVersionDiff.DependsOn], Change(diff, "Add the milk").ChangedFields);
        Assert.Equal(["Rest the tea", "Add the milk"], diff.StepsToRelearn().Select(step => step.ActionText));
    }

    [Fact]
    public void Dropped_step_is_removed_and_is_not_something_to_relearn()
    {
        var diff = Diff([Brew(), Milk(), Garnish()], [Brew(), Milk()]);

        var garnish = Change(diff, "Garnish with peach");
        Assert.Equal((StepChangeKind.Removed, true), (garnish.Kind, garnish.After is null));
        Assert.True(diff.HasChanges);
        Assert.Empty(diff.StepsToRelearn());
        Assert.Equal(["Garnish with peach"], diff.StepsRemoved().Select(step => step.ActionText));
    }

    [Fact]
    public void Reworded_step_is_one_changed_step_not_one_removed_and_one_added()
    {
        var diff = Diff([Brew(), Milk(), Garnish()], [Brew(), Milk(action: "Add the cold milk base"), Garnish()]);

        var milk = Assert.Single(diff.Steps, step => step.Kind == StepChangeKind.Changed);
        Assert.Equal(("Add the milk", "Add the cold milk base"), (milk.Before!.ActionText, milk.After!.ActionText));
        Assert.Equal([RecipeVersionDiff.ActionText], milk.ChangedFields);
        Assert.Equal((0, 0), (diff.Count(StepChangeKind.Added), diff.Count(StepChangeKind.Removed)));
    }

    [Fact]
    public void Capitals_and_spacing_are_not_a_change()
    {
        var diff = Diff([Brew(), Milk()], [Brew(action: "  brew   the OOLONG "), Milk()]);

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Step_moved_before_another_is_relearned_where_it_is_now()
    {
        var a = new StepSpec(1, "Warm the cup", null, null, 10, [], []);
        var b = new StepSpec(2, "Rinse the leaf", null, null, 10, [], []);
        var c = new StepSpec(3, "Brew the oolong", "TEA_BREWER", null, 480, [new IngredientSpec(Oolong, 18m, "g")], []);

        var diff = Diff([a, b, c], [b with { StepOrder = 1 }, a with { StepOrder = 2 }, c]);

        Assert.True(diff.HasChanges);
        Assert.Equal(StepChangeKind.Unchanged, Change(diff, "Brew the oolong").Kind);
        // One of the two that swapped is seen as taken out and put back in.
        Assert.Equal((1, 1), (diff.Count(StepChangeKind.Added), diff.Count(StepChangeKind.Removed)));
        Assert.Single(diff.StepsToRelearn());
    }

    [Fact]
    public void Steps_are_listed_in_the_order_of_the_newer_version_with_removed_ones_in_their_place()
    {
        var rest = new StepSpec(3, "Rest the tea", null, null, 60, [], [new DependencySpec(2, DependencyType.FinishToStart)]);

        var diff = Diff([Brew(), Milk(), Garnish()], [Brew(), Milk(), rest]);

        Assert.Equal(
            [(StepChangeKind.Unchanged, "Brew the oolong"), (StepChangeKind.Unchanged, "Add the milk"),
                (StepChangeKind.Changed, "Rest the tea")],
            diff.Steps.Select(step => (step.Kind, (step.After ?? step.Before)!.ActionText)));
    }

    [Fact]
    public void Versions_of_two_recipes_are_not_compared()
    {
        var other = RecipeVersion.CreateDraft(recipeId: 99, versionNo: 1, Author);
        other.ReplaceContent([Brew()], Author);

        Assert.Throws<ArgumentException>(() => RecipeVersionDiff.Between(Draft(Brew()), other));
    }
}
