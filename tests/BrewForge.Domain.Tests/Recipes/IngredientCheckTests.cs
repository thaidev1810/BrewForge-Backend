using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>BR-11: an ingredient may not be used beyond its shelf life, in hours from preparation.</summary>
public sealed class IngredientCheckTests
{
    private const int Hour = 3600;

    private static IReadOnlyList<Violation> Run(RecipeVersion version) => new IngredientCheck().Run(version, Catalog());

    // Fresh milk has a shelf life of 4 h; oolong 8 h; water and peach 24 h.

    [Fact]
    public void Ingredient_used_beyond_its_shelf_life_window_is_rejected()
    {
        // Milk goes in at step 1, and the recipe then steeps for five hours.
        var version = Draft(
            Step(1, "Add the milk", seconds: 60, uses: [(Milk, 120m, "ml")]),
            Step(2, "Cold steep", seconds: 5 * Hour, dependsOn: [1]),
            Step(3, "Serve", seconds: 30, dependsOn: [2]));

        var violation = Assert.Single(Run(version));

        Assert.Equal("BR-11", violation.Rule);
        Assert.Equal(1, violation.StepOrder); // attached to the step that opened the window
        Assert.Contains("ING-MILK", violation.Message);
        Assert.Equal("<= 4 h", violation.Expected);
        Assert.Equal("5.03 h", violation.Actual);
    }

    [Fact]
    public void Window_exactly_equal_to_the_shelf_life_is_accepted()
    {
        var version = Draft(
            Step(1, "Add the milk", seconds: Hour, uses: [(Milk, 120m, "ml")]),
            Step(2, "Rest", seconds: 3 * Hour, dependsOn: [1]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Window_one_second_over_the_shelf_life_is_rejected()
    {
        var version = Draft(
            Step(1, "Add the milk", seconds: Hour, uses: [(Milk, 120m, "ml")]),
            Step(2, "Rest", seconds: 3 * Hour + 1, dependsOn: [1]));

        Assert.Single(Run(version));
    }

    [Fact]
    public void Window_starts_at_the_first_use_not_at_the_start_of_the_recipe()
    {
        // Twelve hours of cold brewing happen before the milk is ever opened.
        var version = Draft(
            Step(1, "Cold brew", seconds: 12 * Hour, uses: [(Water, 500m, "ml")]),
            Step(2, "Add the milk", seconds: 30, uses: [(Milk, 120m, "ml")], dependsOn: [1]),
            Step(3, "Serve", seconds: 30, dependsOn: [2]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Each_ingredient_is_measured_against_its_own_shelf_life()
    {
        // Six hours: too long for the milk (4 h), fine for the oolong (8 h).
        var version = Draft(
            Step(1, "Combine", seconds: 60, uses: [(Oolong, 18m, "g"), (Milk, 120m, "ml")]),
            Step(2, "Steep", seconds: 6 * Hour, dependsOn: [1]));

        Assert.Contains("ING-MILK", Assert.Single(Run(version)).Message);
    }

    [Fact]
    public void Ingredient_used_twice_is_reported_once_at_its_first_use()
    {
        var version = Draft(
            Step(1, "First splash", seconds: 60, uses: [(Milk, 60m, "ml")]),
            Step(2, "Steep", seconds: 5 * Hour, dependsOn: [1]),
            Step(3, "Second splash", seconds: 60, uses: [(Milk, 60m, "ml")], dependsOn: [2]));

        Assert.Equal(1, Assert.Single(Run(version)).StepOrder);
    }

    [Fact]
    public void Step_without_a_duration_adds_nothing_to_the_window()
    {
        var version = Draft(
            Step(1, "Add the milk", uses: [(Milk, 120m, "ml")]),
            Step(2, "Stir", dependsOn: [1]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Inactive_ingredient_is_rejected()
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Brew", uses: [(Retired, 18m, "g")]))));

        Assert.Equal("BR-11", violation.Rule);
        Assert.Contains("ING-RETIRED", violation.Message);
        Assert.Equal("ACTIVE", violation.Expected);
        Assert.Equal("INACTIVE", violation.Actual);
    }

    [Fact]
    public void Ingredient_missing_from_the_catalogue_is_rejected()
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Brew", uses: [(Unknown, 18m, "g")]))));

        Assert.Equal("BR-11", violation.Rule);
        Assert.Equal(1, violation.StepOrder);
    }

    [Theory]
    [InlineData("g")]    // milk is a volume
    [InlineData("pcs")]
    [InlineData("cup")]  // not a unit at all
    public void Unit_that_cannot_be_converted_to_the_ingredient_unit_is_rejected(string unit)
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Add the milk", uses: [(Milk, 120m, unit)]))));

        Assert.Equal("BR-11", violation.Rule);
        Assert.Equal("a unit convertible to ml", violation.Expected);
        Assert.Equal(unit, violation.Actual);
    }

    [Theory]
    [InlineData(Milk, "0.12", "l")]
    [InlineData(Oolong, "0.018", "kg")]
    [InlineData(Oolong, "18000", "mg")]
    public void Unit_of_the_same_dimension_is_accepted(long ingredient, string quantity, string unit)
    {
        var amount = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Empty(Run(Draft(Step(1, "Use it", uses: [(ingredient, amount, unit)]))));
    }

    [Fact]
    public void A_sound_recipe_passes()
    {
        Assert.Empty(Run(ValidOolongMilkTea()));
    }
}
