using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>
/// The temperature and the pressure a step is done at, as figures the
/// validator reads: a machine set by temperature or by pressure is held to
/// its range (BR-10), and a leaf to the window it is brewed in (BR-11).
/// </summary>
public sealed class BrewingParameterTests
{
    private const long Extractor = 6;

    /// <summary>The fixture catalogue, with a machine dosed in bar and the oolong given its window of 85-95 C.</summary>
    private static ValidationCatalog TeaCatalog() => Catalog((equipment, ingredients) =>
    {
        equipment.Add(WithId(StandardEquipment.Create("EQ-EXT-02", "PRESSURE_EXTRACTOR", 8m, 10m, DosingUnit.Bar), Extractor));
        ingredients.Single(i => i.Id == Oolong).SetBrewingWindow(85m, 95m);
    });

    private static StepSpec Heat(decimal? temperature) =>
        Step(1, "Heat the water", "KETTLE", 120, [(Water, 300m, "ml")]) with { TemperatureC = temperature };

    private static StepSpec Extract(decimal? pressure) =>
        Step(1, "Extract under pressure", "PRESSURE_EXTRACTOR", 28) with { PressureBar = pressure };

    private static StepSpec Brew(decimal? temperature, long leaf = Oolong) =>
        Step(1, "Brew the leaf", "TEA_BREWER", 480, [(leaf, 18m, "g"), (Water, 300m, "ml")]) with { TemperatureC = temperature };

    private static IReadOnlyList<Violation> Equipment(params StepSpec[] steps) => new EquipmentCheck().Run(Draft(steps), TeaCatalog());

    private static IReadOnlyList<Violation> Ingredients(params StepSpec[] steps) => new IngredientCheck().Run(Draft(steps), TeaCatalog());

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- BR-10: a machine set by temperature or pressure

    // KETTLE is 80.0 - 100.0 degC.

    [Theory]
    [InlineData(80, true)]
    [InlineData(92.5, true)]
    [InlineData(100, true)]
    [InlineData(79.9, false)]
    public void BR_10_a_machine_set_by_temperature_is_held_to_its_range(double temperature, bool accepted) =>
        Assert.Equal(accepted, Equipment(Heat((decimal)temperature)).Count == 0);

    [Fact]
    public void BR_10_a_temperature_outside_the_range_is_reported_like_any_dose()
    {
        var violation = Assert.Single(Equipment(Heat(75m)));

        Assert.Equal(("BR-10", "MSG-E10", 1), (violation.Rule, violation.Code, violation.StepOrder));
        Assert.Equal("75.0 degC is outside the 80.0-100.0 degC range of KETTLE", violation.Message);
        Assert.Equal(("80.0 - 100.0", "75.0"), (violation.Expected, violation.Actual));
    }

    [Fact]
    public void BR_10_a_machine_set_by_temperature_must_be_told_its_temperature()
    {
        var violation = Assert.Single(Equipment(Heat(null)));

        Assert.Equal(("BR-10", "no temperature"), (violation.Rule, violation.Actual));
        Assert.Contains("needs a temperature", violation.Message);
    }

    // PRESSURE_EXTRACTOR is 8.0 - 10.0 bar.

    [Theory]
    [InlineData(8, true)]
    [InlineData(9.5, true)]
    [InlineData(10, true)]
    [InlineData(7.9, false)]
    [InlineData(10.1, false)]
    public void BR_10_a_machine_set_by_pressure_is_held_to_its_range(double pressure, bool accepted) =>
        Assert.Equal(accepted, Equipment(Extract((decimal)pressure)).Count == 0);

    [Fact]
    public void BR_10_a_machine_set_by_pressure_must_be_told_its_pressure() =>
        Assert.Equal("no pressure", Assert.Single(Equipment(Extract(null))).Actual);

    [Fact]
    public void Temperature_on_a_machine_that_is_not_set_by_it_is_no_concern_of_the_equipment_check() =>
        Assert.Empty(Equipment(Brew(5m)));

    // ---------------------------------------------------------------- BR-11: the window a leaf is brewed in

    // The oolong is brewed at 85 - 95 C.

    [Theory]
    [InlineData(85, true)]
    [InlineData(90, true)]
    [InlineData(95, true)]
    [InlineData(84.9, false)]
    [InlineData(100, false)]
    public void BR_11_a_step_that_brews_a_leaf_stays_within_its_window(double temperature, bool accepted) =>
        Assert.Equal(accepted, Ingredients(Brew((decimal)temperature)).Count == 0);

    [Fact]
    public void BR_11_water_too_hot_for_the_leaf_is_reported_on_the_step_with_the_window()
    {
        var violation = Assert.Single(Ingredients(Brew(100m)));

        Assert.Equal(("BR-11", 1), (violation.Rule, violation.StepOrder));
        Assert.Equal("ING-OOLONG is brewed at 85-95 C, and the step is done at 100 C", violation.Message);
        Assert.Equal(("85 - 95", "100"), (violation.Expected, violation.Actual));
    }

    [Fact]
    public void Step_that_states_no_temperature_is_not_held_to_a_window()
    {
        // The recipes written before a step could state one are not made invalid by the leaf gaining a window.
        Assert.Empty(Ingredients(Brew(null)));
    }

    [Fact]
    public void Ingredient_without_a_window_accepts_any_temperature() =>
        Assert.Empty(Ingredients(Step(1, "Warm the milk", seconds: 40, uses: [(Milk, 120m, "ml")]) with { TemperatureC = 65m }));

    [Fact]
    public void Whole_recipe_fails_when_only_the_water_is_wrong()
    {
        // BR-08: equipment and ordering pass, and one wrong temperature is still a failed validation.
        var report = Draft(Brew(100m)).Validate(TeaCatalog());

        Assert.False(report.Passed);
        Assert.True(report.Check(CheckType.Equipment).Passed);
        Assert.False(report.Check(CheckType.Ingredient).Passed);
    }

    // ---------------------------------------------------------------- the window of a leaf

    [Fact]
    public void Brewing_window_is_both_bounds_or_neither_and_lies_between_0_and_100()
    {
        var leaf = Ingredient.Create("ING-GREEN", "Green tea leaf", IngredientUnit.Gram, 6, null);
        Assert.False(leaf.HasBrewingWindow);

        leaf.SetBrewingWindow(75.04m, 85m);
        Assert.Equal((true, 75.0m, 85m), (leaf.HasBrewingWindow, leaf.BrewTempMinC, leaf.BrewTempMaxC));

        Assert.All(new (decimal?, decimal?)[] { (75m, null), (null, 85m), (90m, 80m), (-1m, 80m), (80m, 101m) }, window =>
            Assert.Equal(ErrorKind.Validation, Refused(() => leaf.SetBrewingWindow(window.Item1, window.Item2)).Kind));
        Assert.Equal((75.0m, 85m), (leaf.BrewTempMinC!.Value, leaf.BrewTempMaxC!.Value));

        leaf.SetBrewingWindow(null, null);
        Assert.False(leaf.HasBrewingWindow);
    }

    // ---------------------------------------------------------------- the step itself

    [Theory]
    [InlineData(-0.1, null)]
    [InlineData(100.1, null)]
    [InlineData(90.05, null)]
    [InlineData(null, 0.0)]
    [InlineData(null, 20.1)]
    [InlineData(null, 9.25)]
    public void Step_refuses_a_setting_that_no_recipe_could_mean(double? temperature, double? pressure)
    {
        var step = Step(1, "Brew") with { TemperatureC = (decimal?)temperature, PressureBar = (decimal?)pressure };

        var refusal = Refused(() => Draft(step));

        Assert.Contains(refusal.Details, d => d.Field == (temperature is null ? "steps[0].pressureBar" : "steps[0].temperatureC"));
    }

    [Fact]
    public void Step_keeps_the_settings_it_was_given()
    {
        var step = Draft(Step(1, "Extract") with { TemperatureC = 92.5m, PressureBar = 9m }).Steps.Single();

        Assert.Equal((92.5m, 9m), (step.TemperatureC, step.PressureBar));
    }

    [Fact]
    public void Content_hash_tells_two_temperatures_apart_and_is_unchanged_for_a_step_that_states_none()
    {
        var plain = Draft(Brew(null)).ComputeContentHash();

        Assert.Equal(plain, Draft(Brew(null)).ComputeContentHash());
        Assert.NotEqual(plain, Draft(Brew(90m)).ComputeContentHash());
        Assert.NotEqual(Draft(Brew(90m)).ComputeContentHash(), Draft(Brew(92m)).ComputeContentHash());
        Assert.NotEqual(Draft(Extract(9m)).ComputeContentHash(), Draft(Extract(9.5m)).ComputeContentHash());
    }

    [Fact]
    public void Changed_temperature_or_pressure_is_a_changed_step()
    {
        var cooler = RecipeVersionDiff.Between(Draft(Brew(90m)), Draft(Brew(88m)));
        var lighter = RecipeVersionDiff.Between(Draft(Extract(9m)), Draft(Extract(8.5m)));
        var stated = RecipeVersionDiff.Between(Draft(Brew(null)), Draft(Brew(90m)));

        Assert.Equal([RecipeVersionDiff.TemperatureC], cooler.Steps.Single().ChangedFields);
        Assert.Equal([RecipeVersionDiff.PressureBar], lighter.Steps.Single().ChangedFields);
        Assert.Equal(StepChangeKind.Changed, stated.Steps.Single().Kind);
    }
}
