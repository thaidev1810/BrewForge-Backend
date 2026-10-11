using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>BR-10: a dose must lie within the Min-Max range of an active equipment class.</summary>
public sealed class EquipmentCheckTests
{
    private static IReadOnlyList<Violation> Run(RecipeVersion version) => new EquipmentCheck().Run(version, Catalog());

    private static RecipeVersion BrewerWith(decimal quantity, string unit = "g") =>
        Draft(Step(1, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, quantity, unit)]));

    // TEA_BREWER is 15.0 - 25.0 g.

    [Fact]
    public void Dose_one_tenth_above_the_maximum_is_rejected()
    {
        var violation = Assert.Single(Run(BrewerWith(25.1m)));

        Assert.Equal("BR-10", violation.Rule);
        Assert.Equal("MSG-E10", violation.Code);
        Assert.Equal(1, violation.StepOrder);
        Assert.Equal("25.1 g is outside the 15.0-25.0 g range of TEA_BREWER", violation.Message);
        Assert.Equal("15.0 - 25.0", violation.Expected);
        Assert.Equal("25.1", violation.Actual);
    }

    [Fact]
    public void Dose_exactly_on_the_maximum_is_accepted() => Assert.Empty(Run(BrewerWith(25.0m)));

    [Fact]
    public void Dose_exactly_on_the_minimum_is_accepted() => Assert.Empty(Run(BrewerWith(15.0m)));

    [Fact]
    public void Dose_one_tenth_below_the_minimum_is_rejected()
    {
        var violation = Assert.Single(Run(BrewerWith(14.9m)));

        Assert.Equal("14.9", violation.Actual);
    }

    [Theory]
    [InlineData("25.001", false)] // the smallest quantity the schema can store above the limit
    [InlineData("25.000", true)]
    [InlineData("24.999", true)]
    [InlineData("15.000", true)]
    [InlineData("14.999", false)]
    [InlineData("18", true)]
    public void Boundary_is_inclusive_to_the_last_stored_decimal(string quantity, bool accepted)
    {
        var violations = Run(BrewerWith(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(accepted, violations.Count == 0);
    }

    [Theory]
    [InlineData("0.02", "kg", true)]     // 20 g
    [InlineData("0.025", "kg", true)]    // exactly 25 g
    [InlineData("0.026", "kg", false)]   // 26 g: a quantity has 3 decimals, so 1 g is the finest step in kg
    [InlineData("20000", "mg", true)]    // 20 g
    [InlineData("25001", "mg", false)]   // 25.001 g
    public void Dose_is_converted_to_the_dosing_unit_before_it_is_compared(string quantity, string unit, bool accepted)
    {
        var violations = Run(BrewerWith(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), unit));

        Assert.Equal(accepted, violations.Count == 0);
    }

    [Fact]
    public void Violation_reports_the_dose_in_the_dosing_unit_of_the_machine()
    {
        var violation = Assert.Single(Run(BrewerWith(0.03m, "kg")));

        Assert.Equal("30.0 g is outside the 15.0-25.0 g range of TEA_BREWER", violation.Message);
    }

    [Fact]
    public void Ingredient_of_another_dimension_is_not_a_dose_of_the_machine()
    {
        // 300 ml of water goes into a brewer that is dosed in grams of leaf.
        var version = Draft(Step(1, "Brew", "TEA_BREWER", 480, [(Oolong, 18m, "g"), (Water, 300m, "ml")]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Every_ingredient_of_the_dosing_dimension_is_checked_and_each_failure_is_reported()
    {
        var version = Draft(Step(1, "Steam", "MILK_STEAMER", 30, [(Milk, 400m, "ml"), (Water, 50m, "ml")]));

        var violations = Run(version);

        Assert.Equal(["400.0", "50.0"], violations.Select(v => v.Actual));
        Assert.All(violations, v => Assert.Equal("100.0 - 350.0", v.Expected));
    }

    [Fact]
    public void Violation_is_attached_to_the_step_that_caused_it()
    {
        var version = Draft(
            Step(1, "Brew within range", "TEA_BREWER", 480, [(Oolong, 18m, "g")]),
            Step(2, "Rest", seconds: 60),
            Step(3, "Brew too much", "TEA_BREWER", 480, [(Oolong, 40m, "g")]));

        Assert.Equal(3, Assert.Single(Run(version)).StepOrder);
    }

    [Fact]
    public void Step_without_equipment_is_not_range_checked()
    {
        var version = Draft(Step(1, "Weigh a kilo of leaf by hand", uses: [(Oolong, 1000m, "g")]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Equipment_class_missing_from_the_catalogue_is_rejected()
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Brew", "PLASMA_BREWER", 60, [(Oolong, 18m, "g")]))));

        Assert.Equal("BR-10", violation.Rule);
        Assert.Contains("PLASMA_BREWER", violation.Message);
    }

    [Fact]
    public void Inactive_equipment_class_is_rejected_even_when_the_dose_fits()
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Brew", "RETIRED_BREWER", 60, [(Oolong, 18m, "g")]))));

        Assert.Equal("ACTIVE", violation.Expected);
        Assert.Equal("INACTIVE", violation.Actual);
    }

    // TIMED_EXTRACTOR is 25 - 32 sec.

    [Theory]
    [InlineData(25, true)]
    [InlineData(32, true)]
    [InlineData(24, false)]
    [InlineData(33, false)]
    public void Time_dosed_class_checks_the_duration_of_the_step(int seconds, bool accepted)
    {
        var violations = Run(Draft(Step(1, "Extract", "TIMED_EXTRACTOR", seconds)));

        Assert.Equal(accepted, violations.Count == 0);
    }

    [Fact]
    public void Time_dosed_class_requires_a_duration()
    {
        var violation = Assert.Single(Run(Draft(Step(1, "Extract", "TIMED_EXTRACTOR"))));

        Assert.Equal("no duration", violation.Actual);
    }

    // A class dosed in degC or bar is checked against the temperature or the pressure of the step: BrewingParameterTests.
}
