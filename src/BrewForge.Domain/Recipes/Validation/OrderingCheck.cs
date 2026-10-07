using BrewForge.Domain.Common;

namespace BrewForge.Domain.Recipes.Validation;

/// <summary>
/// Ordering and dependency (BR-09). Builds the step dependency graph and
/// topologically sorts it. A cycle is a hard failure with no override, and
/// every step on the cycle is reported. A step that depends on a later step
/// has a precondition no execution in step order can satisfy, and is
/// reported too.
/// </summary>
public sealed class OrderingCheck : IRecipeCheck
{
    public const string Rule = "BR-09";

    public CheckType CheckType => CheckType.Ordering;

    public IReadOnlyList<Violation> Run(RecipeVersion version, ValidationCatalog catalog)
    {
        var violations = new List<Violation>();
        var steps = version.Steps;

        if (steps.Count == 0)
        {
            violations.Add(new Violation(null, null, Rule, "The recipe has no steps", "at least one step", "0 steps"));
            return violations;
        }

        var byOrder = steps.ToDictionary(step => step.StepOrder);

        // A dependency must point at a step of this same version.
        foreach (var step in steps)
        {
            foreach (var dependency in step.Dependencies)
            {
                if (dependency.DependsOnStep is not { } target || !ReferenceEquals(byOrder.GetValueOrDefault(target.StepOrder), target))
                {
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"Step {step.StepOrder} depends on a step that is not part of this recipe version",
                        "a step of this version", $"step id {dependency.DependsOnStepId}"));
                }
            }
        }

        var graph = StepDependencyGraph.From(steps);
        var onCycle = new HashSet<int>();

        if (graph.TopologicalOrder() is null)
        {
            foreach (var cycle in graph.Cycles())
            {
                var names = string.Join(", ", cycle);
                foreach (var stepOrder in cycle)
                {
                    onCycle.Add(stepOrder);
                    var step = byOrder[stepOrder];
                    violations.Add(new Violation(step.Id, stepOrder, Rule,
                        $"A circular dependency was detected between steps {names}. Resolve the cycle before validating.",
                        "an acyclic dependency graph", $"cycle through steps {names}", ErrorCodes.CircularDependency));
                }
            }
        }

        // Outside a cycle, a dependency on a later step is still unsatisfiable.
        // (Inside one it is the same defect, already reported above.)
        foreach (var step in steps)
        {
            foreach (var prerequisite in graph.PrerequisitesOf(step.StepOrder))
            {
                if (prerequisite <= step.StepOrder) continue;
                if (onCycle.Contains(step.StepOrder) && onCycle.Contains(prerequisite)) continue;

                violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                    $"Step {step.StepOrder} depends on step {prerequisite}, which comes after it",
                    $"a step before step {step.StepOrder}", $"step {prerequisite}"));
            }
        }

        return violations;
    }
}
