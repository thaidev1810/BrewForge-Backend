using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;

namespace BrewForge.Domain.Courses;

/// <summary>
/// Builds the seven modules of a course from a released recipe version, and
/// rebuilds them when the version changes (BR-29, BR-30).
///
/// What is generated is structure: which lessons exist and which recipe step
/// each one stands for. The quantities, thresholds, technique gates and
/// shelf-life rules a lesson shows are not written anywhere here; they are
/// read from the bound version when the lesson is rendered (BR-22).
/// </summary>
public static class CourseModuleGenerator
{
    /// <summary>Where each module comes from. The order is the order of the enumeration.</summary>
    public static ModuleSource SourceOf(ModuleType type, bool boundToVersion) => !boundToVersion
        ? ModuleSource.Authored
        : type switch
        {
            ModuleType.ProductOverview or ModuleType.Ingredients or ModuleType.Equipment or ModuleType.Sop =>
                ModuleSource.Generated,
            ModuleType.Technique => ModuleSource.Mixed,
            _ => ModuleSource.Authored,
        };

    /// <summary>Exactly seven modules, in the fixed order (BR-29).</summary>
    internal static List<CourseModule> CreateModules(RecipeVersion? version, RecipeVersionDiff? relearn = null)
    {
        var modules = Enum.GetValues<ModuleType>()
            .Select(type => new CourseModule(type, SourceOf(type, version is not null)))
            .ToList();
        if (version is not null)
        {
            // The authored modules start empty: there is nothing to generate for them.
            foreach (var module in modules.Where(module => module.Source != ModuleSource.Authored))
            {
                Generate(module, version, previousVersion: null, relearn);
            }
        }
        return modules;
    }

    /// <summary>
    /// Rebuilds one module from the version. A GENERATED module is replaced;
    /// a MIXED module has its gate list rebuilt and keeps its prose; an
    /// AUTHORED module is refused (BR-30).
    ///
    /// With <paramref name="relearn"/> the course is a recertification: it
    /// teaches somebody certified on the older version what differs in this
    /// one, so the procedure and the gates are those of the steps that were
    /// added or changed, and the steps that are gone are named.
    /// </summary>
    internal static void Generate(CourseModule module, RecipeVersion version, RecipeVersion? previousVersion,
        RecipeVersionDiff? relearn = null)
    {
        var steps = InDependencyOrder(version);
        if (relearn is not null)
        {
            var changed = relearn.StepsToRelearn().Select(step => step.Id).ToHashSet();
            steps = [.. steps.Where(step => changed.Contains(step.Id))];
        }

        switch (module.ModuleType)
        {
            case ModuleType.ProductOverview when module.Source == ModuleSource.Generated:
                module.ReplaceGenerated(
                    [(relearn is null ? "Product overview" : $"What changed since version {relearn.Before.VersionNo}", null)],
                    durationMinutes: 5);
                break;

            case ModuleType.Ingredients when module.Source == ModuleSource.Generated:
                var ingredients = steps.SelectMany(s => s.Ingredients).Select(i => i.IngredientId).Distinct().Count();
                module.ReplaceGenerated([("Ingredients of this recipe", null)], Math.Max(5, 2 * ingredients));
                break;

            case ModuleType.Equipment when module.Source == ModuleSource.Generated:
                var machines = steps.Select(s => s.EquipmentClass).Where(c => c is not null).Distinct().Count();
                module.ReplaceGenerated([("Equipment used in this recipe", null)], Math.Max(5, 5 * machines));
                break;

            case ModuleType.Sop when module.Source == ModuleSource.Generated:
                var seconds = steps.Sum(s => (long)(s.DurationSeconds ?? 0));
                var removed = relearn?.StepsRemoved() ?? [];
                module.ReplaceGenerated(
                    steps.Select(s => (StepTitle(s), (long?)s.Id)).Concat(removed.Select(s => (RemovedTitle(s), (long?)null))),
                    durationMinutes: (int)Math.Ceiling(seconds / 60m) + 2 * steps.Count + removed.Count);
                break;

            case ModuleType.Technique when module.Source == ModuleSource.Mixed:
                var previousOrders = (previousVersion ?? version).Steps.ToDictionary(s => s.Id, s => s.StepOrder);
                module.SyncGeneratedItems(
                    [.. steps.Where(s => s.TechniqueGate is not null).Select(s => (s.StepOrder, GateTitle(s), s.Id))],
                    previousOrders);
                break;

            default:
                throw DomainException.RuleViolation(CourseModule.AuthoringRule,
                    $"The {module.ModuleType.Code()} module is authored by the trainer and is never regenerated.");
        }
    }

    /// <summary>
    /// The steps in an order that respects their dependencies. A released
    /// version has passed the ordering check, so the order always exists.
    /// </summary>
    internal static IReadOnlyList<RecipeStep> InDependencyOrder(RecipeVersion version)
    {
        var byOrder = version.Steps.ToDictionary(step => step.StepOrder);
        var order = StepDependencyGraph.From(version.Steps).TopologicalOrder()
                    ?? [.. byOrder.Keys.Order()];
        return [.. order.Select(stepOrder => byOrder[stepOrder])];
    }

    private static string StepTitle(RecipeStep step) => Shorten($"Step {step.StepOrder} - {LowerFirst(step.ActionText)}");

    private static string RemovedTitle(RecipeStep step) => Shorten($"No longer done - {LowerFirst(step.ActionText)}");

    private static string GateTitle(RecipeStep step) => Shorten($"Technique gate - step {step.StepOrder}");

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string Shorten(string title) => title.Length <= 160 ? title : title[..157] + "...";
}
