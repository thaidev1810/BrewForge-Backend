using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>
/// A variant is a recipe version served another way: the same steps with
/// the quantities scaled. It is content of the version (BR-01), and the
/// version passes only if every way it is served passes (BR-08).
/// </summary>
public sealed class RecipeVariantTests
{
    private static VariantSpec Variant(string code, decimal scale, params (long Id, decimal Scale)[] ingredients) =>
        new(code, $"Serving {code}", scale, [.. ingredients.Select(i => new VariantIngredientSpec(i.Id, i.Scale))]);

    /// <summary>The fixture drink: 18 g of oolong in a brewer that takes 15 to 25 g, 300 ml of water, 120 ml of milk, 2 slices of peach.</summary>
    private static RecipeVersion Drink(params VariantSpec[] variants)
    {
        var version = ValidOolongMilkTea();
        version.ReplaceVariants(variants, Author);
        return version;
    }

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- what a variant serves

    [Fact]
    public void Variant_serves_the_same_steps_with_every_quantity_scaled()
    {
        var version = Drink(Variant("L", 1.25m));

        var served = version.Variants.Single().Apply(version);

        Assert.Equal(version.OrderedSteps(), served.Select(step => step.Step));
        Assert.Equal([(Oolong, 18m, 22.5m), (Water, 300m, 375m)],
            served[0].Ingredients.Select(i => (i.IngredientId, i.BaseQuantity, i.Quantity)));
        Assert.Equal((150m, 2.5m), (served[1].Ingredients.Single().Quantity, served[2].Ingredients.Single().Quantity));
    }

    [Fact]
    public void Ingredient_with_a_scale_of_its_own_does_not_follow_the_variant_and_one_scaled_to_nothing_is_left_out()
    {
        // A large hot drink: more of everything but the leaf, which is brewed as ever, and no garnish.
        var version = Drink(Variant("L-HOT", 1.5m, (Oolong, 1m), (Peach, 0m)));

        var served = version.Variants.Single().Apply(version);

        Assert.Equal([(Oolong, 18m), (Water, 450m)], served[0].Ingredients.Select(i => (i.IngredientId, i.Quantity)));
        Assert.Equal(180m, served[1].Ingredients.Single().Quantity);
        Assert.Empty(served[2].Ingredients);
        Assert.Equal("L-HOT", version.Variant("l-hot")!.VariantCode);
    }

    [Fact]
    public void Scaled_quantity_is_rounded_to_the_three_decimals_a_quantity_has()
    {
        var version = Drink(Variant("S", 0.333m));

        Assert.Equal(5.994m, version.Variants.Single().Apply(version)[0].Ingredients.First().Quantity);
    }

    // ---------------------------------------------------------------- what a variant may be

    [Fact]
    public void Variants_of_a_version_have_codes_of_their_own_and_a_scale_that_is_a_serving()
    {
        var version = ValidOolongMilkTea();

        var sameCode = Refused(() => version.ReplaceVariants([Variant("L", 1.2m), Variant("l", 1.3m)], Author));
        var noCode = Refused(() => version.ReplaceVariants([Variant(" ", 1.2m)], Author));
        var oddCode = Refused(() => version.ReplaceVariants([Variant("L / hot", 1.2m)], Author));
        var noScale = Refused(() => version.ReplaceVariants([Variant("L", 0m)], Author));
        var hugeScale = Refused(() => version.ReplaceVariants([Variant("L", 5.001m)], Author));
        var tooFine = Refused(() => version.ReplaceVariants([Variant("L", 1.2345m)], Author));
        var tooMany = Refused(() => version.ReplaceVariants(
            [.. Enumerable.Range(1, RecipeVariant.MaxVariants + 1).Select(i => Variant($"V{i}", 1m))], Author));

        Assert.Contains(sameCode.Details, d => d.Field == "variants");
        Assert.Contains(noCode.Details, d => d.Field == "variants[0].code");
        Assert.Contains(oddCode.Details, d => d.Field == "variants[0].code");
        Assert.All(new[] { noScale, hugeScale, tooFine }, refusal => Assert.Contains(refusal.Details, d => d.Field == "variants[0].scale"));
        Assert.Contains(tooMany.Details, d => d.Field == "variants");
        Assert.Empty(version.Variants);
    }

    [Fact]
    public void Variant_scales_only_ingredients_the_recipe_uses_and_each_of_them_once()
    {
        var version = ValidOolongMilkTea();

        var foreign = Refused(() => version.ReplaceVariants([Variant("L", 1.2m, (Retired, 1m))], Author));
        var twice = Refused(() => version.ReplaceVariants([Variant("L", 1.2m, (Oolong, 1m), (Oolong, 1.1m))], Author));
        var negative = Refused(() => version.ReplaceVariants([Variant("L", 1.2m, (Oolong, -0.1m))], Author));

        Assert.Contains(foreign.Details, d => d.Field == "variants[0].ingredients[0].ingredientId");
        Assert.Contains(twice.Details, d => d.Field == "variants[0].ingredients");
        Assert.Contains(negative.Details, d => d.Field == "variants[0].ingredients[0].scale");
    }

    [Fact]
    public void Replacing_the_steps_forgets_the_scale_of_an_ingredient_the_recipe_no_longer_uses()
    {
        var version = Drink(Variant("L-HOT", 1.5m, (Oolong, 1m), (Peach, 0m)));

        version.ReplaceContent([Step(1, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, 18m, "g")])], Author);

        var variant = Assert.Single(version.Variants);
        Assert.Equal([(Oolong, 1m)], variant.Ingredients.Select(i => (i.IngredientId, i.Scale)));
    }

    // ---------------------------------------------------------------- BR-08: every way it is served

    [Fact]
    public void BR_08_a_version_fails_when_one_of_its_variants_puts_too_much_through_a_machine()
    {
        // 18 g of leaf is within the brewer's 15-25 g; half as much again is 27 g.
        var version = Drink(Variant("M", 1m), Variant("L", 1.5m));

        var report = version.Validate(Catalog());

        Assert.False(report.Passed);
        var violation = Assert.Single(report.Violations);
        Assert.Equal(("BR-10", "L", 1, "27.0"), (violation.Rule, violation.Variant, violation.StepOrder, violation.Actual));
        Assert.Equal("Variant L: 27.0 g is outside the 15.0-25.0 g range of TEA_BREWER", violation.Message);
        // The other two checks have nothing against it: it is the equipment check that fails.
        Assert.True(report.Check(CheckType.Ordering).Passed);
        Assert.True(report.Check(CheckType.Ingredient).Passed);
        Assert.Equal("BR-08", Refused(() => version.Submit(report)).Rule);
    }

    [Fact]
    public void Variant_that_keeps_the_dose_of_the_machine_passes()
    {
        var version = Drink(Variant("L", 1.5m, (Oolong, 1m)), Variant("S", 0.9m));

        Assert.True(version.Validate(Catalog()).Passed);
    }

    [Fact]
    public void Violation_of_the_version_as_written_is_reported_once_not_again_for_every_variant()
    {
        // The retired brewer is wrong whatever the quantity.
        var version = Draft(Step(1, "Brew the oolong", "RETIRED_BREWER", 480, [(Oolong, 18m, "g")]));
        version.ReplaceVariants([Variant("M", 1m), Variant("L", 1.2m)], Author);

        var violations = version.Validate(Catalog()).Violations.ToList();

        Assert.Null(Assert.Single(violations).Variant);
    }

    [Fact]
    public void Version_as_served_keeps_the_order_and_the_settings_of_its_steps()
    {
        var version = Draft(
            Step(1, "Heat the water", "KETTLE", 120, [(Water, 300m, "ml")]) with { TemperatureC = 90m },
            Step(2, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, 18m, "g")], dependsOn: [1]));
        version.ReplaceVariants([Variant("L", 1.2m)], Author);

        var served = version.AsServed(version.Variants.Single());

        Assert.Equal((90m, "KETTLE"), (served.OrderedSteps()[0].TemperatureC, served.OrderedSteps()[0].EquipmentClass));
        Assert.Equal([1], served.OrderedSteps()[1].Dependencies.Select(d => d.DependsOnStep.StepOrder));
        Assert.Equal(21.6m, served.OrderedSteps()[1].Ingredients.Single().Quantity);
        // The version itself is as it was.
        Assert.Equal(18m, version.OrderedSteps()[1].Ingredients.Single().Quantity);
    }

    // ---------------------------------------------------------------- BR-01: content of the version

    [Fact]
    public void BR_01_the_variants_of_a_released_version_cannot_change()
    {
        var released = ReleasedVersion();

        var refusal = Refused(() => released.ReplaceVariants([Variant("L", 1.2m)], Author));

        Assert.Equal(("BR-01", "MSG-E06"), (refusal.Rule, refusal.Code));
    }

    [Fact]
    public void Changing_the_variants_makes_the_editor_the_author_and_is_refused_while_awaiting_review()
    {
        var version = ValidOolongMilkTea();
        version.ReplaceVariants([Variant("L", 1.1m)], editorId: 77);
        Assert.Equal(77, version.CreatedBy); // BR-12: whoever last touched the content cannot approve it

        version.Submit(version.Validate(Catalog()));

        Assert.Equal(RecipeVersion.StateRule, Refused(() => version.ReplaceVariants([], Author)).Rule);
    }

    [Fact]
    public void Content_hash_covers_the_variants_and_is_unchanged_for_a_version_that_has_none()
    {
        var plain = ValidOolongMilkTea().ComputeContentHash();

        Assert.Equal(plain, Drink().ComputeContentHash());
        Assert.NotEqual(plain, Drink(Variant("L", 1.2m)).ComputeContentHash());
        Assert.NotEqual(Drink(Variant("L", 1.2m)).ComputeContentHash(), Drink(Variant("L", 1.3m)).ComputeContentHash());
        Assert.NotEqual(Drink(Variant("L", 1.2m)).ComputeContentHash(), Drink(Variant("L", 1.2m, (Peach, 0m))).ComputeContentHash());
        // The order they were entered in is not content.
        Assert.Equal(Drink(Variant("S", 0.9m), Variant("L", 1.2m)).ComputeContentHash(),
            Drink(Variant("L", 1.2m), Variant("S", 0.9m)).ComputeContentHash());
    }

    [Fact]
    public void Variants_are_carried_over_as_specifications_to_another_version()
    {
        var source = Drink(Variant("L-HOT", 1.5m, (Oolong, 1m), (Peach, 0m)));
        var copy = ValidOolongMilkTea();

        copy.ReplaceVariants(source.VariantSpecs(), Author);

        Assert.Equal(source.ComputeContentHash(), copy.ComputeContentHash());
    }
}
