using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>BR-08: a candidate passes only if all three checks pass. A partial pass is a failure.</summary>
public sealed class RecipeValidatorTests
{
    [Fact]
    public void All_three_checks_pass_for_a_sound_recipe()
    {
        var report = ValidOolongMilkTea().Validate(Catalog());

        Assert.True(report.Passed);
        Assert.Equal([CheckType.Equipment, CheckType.Ordering, CheckType.Ingredient], report.Checks.Select(c => c.CheckType));
        Assert.All(report.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public void Two_checks_pass_and_equipment_fails_so_the_result_is_a_failure()
    {
        var version = Draft(Step(1, "Brew far too much", "TEA_BREWER", 480, [(Oolong, 40m, "g")]));

        var report = version.Validate(Catalog());

        Assert.False(report.Check(CheckType.Equipment).Passed);
        Assert.True(report.Check(CheckType.Ordering).Passed);
        Assert.True(report.Check(CheckType.Ingredient).Passed);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Two_checks_pass_and_ordering_fails_so_the_result_is_a_failure()
    {
        var version = Draft(
            Step(1, "Brew", "TEA_BREWER", 480, [(Oolong, 18m, "g")], dependsOn: [2]),
            Step(2, "Add the milk", uses: [(Milk, 120m, "ml")], dependsOn: [1]));

        var report = version.Validate(Catalog());

        Assert.True(report.Check(CheckType.Equipment).Passed);
        Assert.False(report.Check(CheckType.Ordering).Passed);
        Assert.True(report.Check(CheckType.Ingredient).Passed);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Two_checks_pass_and_ingredient_fails_so_the_result_is_a_failure()
    {
        var version = Draft(Step(1, "Brew", "TEA_BREWER", 480, [(Retired, 18m, "g")]));

        var report = version.Validate(Catalog());

        Assert.True(report.Check(CheckType.Equipment).Passed);
        Assert.True(report.Check(CheckType.Ordering).Passed);
        Assert.False(report.Check(CheckType.Ingredient).Passed);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Checks_are_independent_so_every_violation_is_found_in_one_run()
    {
        var version = Draft(
            Step(1, "Brew too much of a retired leaf, after step 2", "TEA_BREWER", 480, [(Retired, 40m, "g")], dependsOn: [2]),
            Step(2, "Add the milk", uses: [(Milk, 120m, "g")]));

        var report = version.Validate(Catalog());

        Assert.All(report.Checks, check => Assert.False(check.Passed));
        Assert.Single(report.Check(CheckType.Equipment).Violations);
        Assert.Single(report.Check(CheckType.Ordering).Violations);
        Assert.Equal(2, report.Check(CheckType.Ingredient).Violations.Count);
    }

    [Fact]
    public void A_report_from_which_a_check_is_missing_is_not_a_pass()
    {
        var partial = new ValidationReport(
        [
            new CheckResult(CheckType.Equipment, []),
            new CheckResult(CheckType.Ordering, []),
        ]);

        Assert.False(partial.Passed);
    }

    [Fact]
    public void Validation_reads_the_catalogue_as_it_stands_now()
    {
        var version = ValidOolongMilkTea();
        Assert.True(version.Validate(Catalog()).Passed);

        // The same recipe, after the brewer's range was tightened in the catalogue.
        var tightened = Catalog((equipment, _) =>
            equipment.Single(e => e.EquipmentClass == "TEA_BREWER").Update(15m, 17m, DosingUnit.Gram));

        Assert.False(version.Validate(tightened).Passed);
    }

    // ---------------------------------------------------------------- submit

    [Fact]
    public void Submit_moves_a_draft_to_validated_when_all_checks_pass()
    {
        var version = ValidOolongMilkTea();

        version.Submit(version.Validate(Catalog()));

        Assert.Equal(VersionState.Validated, version.State);
    }

    [Fact]
    public void Submit_is_refused_on_a_partial_pass_and_the_draft_stays_a_draft()
    {
        var version = Draft(Step(1, "Brew far too much", "TEA_BREWER", 480, [(Oolong, 40m, "g")]));

        var refusal = Assert.Throws<DomainException>(() => version.Submit(version.Validate(Catalog())));

        Assert.Equal(ErrorKind.Refused, refusal.Kind);
        Assert.Equal("MSG-E03", refusal.Code);
        Assert.Equal("BR-08", refusal.Rule);
        Assert.Contains(refusal.Details, d => d.Field == "steps[1]" && d.Issue.StartsWith("BR-10"));
        Assert.Equal(VersionState.Draft, version.State);
    }
}
