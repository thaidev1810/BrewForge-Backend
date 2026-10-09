using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>BR-09: the step dependency graph must be acyclic. A cycle is a hard failure.</summary>
public sealed class OrderingCheckTests
{
    private static IReadOnlyList<Violation> Run(RecipeVersion version) => new OrderingCheck().Run(version, Catalog());

    [Fact]
    public void Cycle_A_B_C_A_is_rejected_and_the_error_names_all_three_steps()
    {
        // A depends on C, B depends on A, C depends on B.
        var version = Draft(
            Step(1, "A", dependsOn: [3]),
            Step(2, "B", dependsOn: [1]),
            Step(3, "C", dependsOn: [2]));

        var violations = Run(version);

        // One violation per step on the cycle, each attached to its own step...
        Assert.Equal([1, 2, 3], violations.Select(v => v.StepOrder));
        Assert.All(violations, violation =>
        {
            Assert.Equal("BR-09", violation.Rule);
            Assert.Equal("MSG-E07", violation.Code);
            // ...and each naming all three.
            Assert.Contains("steps 1, 2, 3", violation.Message);
            Assert.Equal("cycle through steps 1, 2, 3", violation.Actual);
        });
    }

    [Fact]
    public void Two_step_cycle_is_rejected()
    {
        var version = Draft(Step(1, "A", dependsOn: [2]), Step(2, "B", dependsOn: [1]));

        Assert.Equal([1, 2], Run(version).Select(v => v.StepOrder));
    }

    [Fact]
    public void Only_the_steps_on_the_cycle_are_reported_as_cyclic()
    {
        var version = Draft(
            Step(1, "Innocent prerequisite"),
            Step(2, "A", dependsOn: [1, 4]),
            Step(3, "B", dependsOn: [2]),
            Step(4, "C", dependsOn: [3]),
            Step(5, "Innocent follower", dependsOn: [4]));

        var cyclic = Run(version).Where(v => v.Code == "MSG-E07").Select(v => v.StepOrder);

        Assert.Equal([2, 3, 4], cyclic);
    }

    [Fact]
    public void Cycle_is_reported_once_per_step_not_again_as_a_forward_dependency()
    {
        var version = Draft(
            Step(1, "A", dependsOn: [3]),
            Step(2, "B", dependsOn: [1]),
            Step(3, "C", dependsOn: [2]));

        Assert.Equal(3, Run(version).Count);
    }

    [Fact]
    public void Step_depending_on_a_later_step_is_an_unsatisfiable_precondition()
    {
        // No cycle here: step 1 simply needs something that happens after it.
        var version = Draft(Step(1, "Pour the tea", dependsOn: [2]), Step(2, "Brew the tea"));

        var violation = Assert.Single(Run(version));

        Assert.Equal("BR-09", violation.Rule);
        Assert.Equal(1, violation.StepOrder);
        Assert.Equal("Step 1 depends on step 2, which comes after it", violation.Message);
        Assert.Equal("a step before step 1", violation.Expected);
        Assert.Equal("step 2", violation.Actual);
    }

    [Fact]
    public void Forward_dependency_and_a_separate_cycle_are_both_reported()
    {
        var version = Draft(
            Step(1, "Needs a later step", dependsOn: [2]),
            Step(2, "Fine"),
            Step(3, "A", dependsOn: [4]),
            Step(4, "B", dependsOn: [3]));

        var violations = Run(version);

        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, v => v.StepOrder == 1 && v.Code is null);
        Assert.Equal([3, 4], violations.Where(v => v.Code == "MSG-E07").Select(v => v.StepOrder));
    }

    [Fact]
    public void Dependencies_that_all_point_backwards_pass()
    {
        var version = Draft(
            Step(1, "Brew"),
            Step(2, "Steam milk"),
            Step(3, "Combine", dependsOn: [1, 2]),
            Step(4, "Garnish", dependsOn: [3]));

        Assert.Empty(Run(version));
    }

    [Fact]
    public void Steps_with_no_dependencies_pass()
    {
        Assert.Empty(Run(Draft(Step(1), Step(2), Step(3))));
    }

    [Fact]
    public void Recipe_with_no_steps_fails()
    {
        var violation = Assert.Single(Run(Draft()));

        Assert.Null(violation.StepOrder);
        Assert.Equal("0 steps", violation.Actual);
    }
}
