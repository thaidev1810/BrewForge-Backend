namespace BrewForge.Domain.Recipes;

public enum StepChangeKind
{
    Unchanged,
    Changed,
    Added,
    Removed,
}

/// <summary>How the quantity of one ingredient of a step differs. A side that is null is a side that does not use it.</summary>
public sealed record IngredientChange(long IngredientId, decimal? QuantityBefore, string? UnitBefore,
    decimal? QuantityAfter, string? UnitAfter);

/// <summary>
/// One step of the comparison: the step as it was, as it is now, or both,
/// with the fields that differ when it is both.
/// </summary>
public sealed record StepChange(StepChangeKind Kind, RecipeStep? Before, RecipeStep? After,
    IReadOnlyList<string> ChangedFields, IReadOnlyList<IngredientChange> Ingredients);

/// <summary>
/// What changed between two versions of a recipe, step by step. A version
/// shares no step with another: each has rows of its own, so the steps are
/// lined up here by what they say.
///
/// Steps with the same action text are matched first, in order, as the
/// longest run of them both versions have in common. Between two matched
/// steps, what is left on each side is paired off in order as a step that
/// was reworded, and whatever then remains was added or removed. A step that
/// only moved down because another was inserted before it has not changed.
/// </summary>
public sealed class RecipeVersionDiff
{
    public const string ActionText = "actionText";
    public const string EquipmentClass = "equipmentClass";
    public const string TechniqueGate = "techniqueGate";
    public const string DurationSeconds = "durationSeconds";
    public const string TemperatureC = "temperatureC";
    public const string PressureBar = "pressureBar";
    public const string Ingredients = "ingredients";
    public const string DependsOn = "dependsOn";

    private RecipeVersionDiff(RecipeVersion before, RecipeVersion after, IReadOnlyList<StepChange> steps)
    {
        Before = before;
        After = after;
        Steps = steps;
    }

    public RecipeVersion Before { get; }
    public RecipeVersion After { get; }

    /// <summary>Every step of either version, in the order of the newer one, removed steps where they used to be.</summary>
    public IReadOnlyList<StepChange> Steps { get; }

    public bool HasChanges => Steps.Any(step => step.Kind != StepChangeKind.Unchanged);

    public int Count(StepChangeKind kind) => Steps.Count(step => step.Kind == kind);

    /// <summary>The steps of the newer version somebody trained on the older one has not been taught as they are now.</summary>
    public IReadOnlyList<RecipeStep> StepsToRelearn() =>
        [.. Steps.Where(step => step.Kind is StepChangeKind.Added or StepChangeKind.Changed).Select(step => step.After!)];

    public IReadOnlyList<RecipeStep> StepsRemoved() =>
        [.. Steps.Where(step => step.Kind == StepChangeKind.Removed).Select(step => step.Before!)];

    public static RecipeVersionDiff Between(RecipeVersion before, RecipeVersion after)
    {
        if (before.RecipeId != after.RecipeId)
        {
            throw new ArgumentException("Two versions are compared only when they are versions of one recipe.", nameof(after));
        }

        var old = before.Steps.OrderBy(step => step.StepOrder).ToList();
        var now = after.Steps.OrderBy(step => step.StepOrder).ToList();
        var pairs = Align(old, now);

        // Which step of the newer version each step of the older one became, to compare what they depend on.
        var became = pairs.Where(pair => pair is { Before: not null, After: not null })
            .ToDictionary(pair => pair.Before!.StepOrder, pair => pair.After!.StepOrder);

        return new RecipeVersionDiff(before, after, [.. pairs.Select(pair => Describe(pair.Before, pair.After, became))]);
    }

    private static List<(RecipeStep? Before, RecipeStep? After)> Align(List<RecipeStep> old, List<RecipeStep> now)
    {
        // Longest common subsequence over the action text.
        var length = new int[old.Count + 1, now.Count + 1];
        for (var i = old.Count - 1; i >= 0; i--)
        {
            for (var j = now.Count - 1; j >= 0; j--)
            {
                length[i, j] = SameText(old[i], now[j])
                    ? length[i + 1, j + 1] + 1
                    : Math.Max(length[i + 1, j], length[i, j + 1]);
            }
        }

        var pairs = new List<(RecipeStep?, RecipeStep?)>();
        var (leftOld, leftNow) = (new List<RecipeStep>(), new List<RecipeStep>());
        void FlushGap()
        {
            // What lies between two matches: reworded steps pair off in order, the rest came or went.
            var reworded = Math.Min(leftOld.Count, leftNow.Count);
            for (var k = 0; k < reworded; k++) pairs.Add((leftOld[k], leftNow[k]));
            for (var k = reworded; k < leftOld.Count; k++) pairs.Add((leftOld[k], null));
            for (var k = reworded; k < leftNow.Count; k++) pairs.Add((null, leftNow[k]));
            leftOld.Clear();
            leftNow.Clear();
        }

        var (a, b) = (0, 0);
        while (a < old.Count && b < now.Count)
        {
            if (SameText(old[a], now[b]))
            {
                FlushGap();
                pairs.Add((old[a++], now[b++]));
            }
            else if (length[a + 1, b] >= length[a, b + 1]) leftOld.Add(old[a++]);
            else leftNow.Add(now[b++]);
        }
        leftOld.AddRange(old.Skip(a));
        leftNow.AddRange(now.Skip(b));
        FlushGap();
        return pairs;
    }

    private static StepChange Describe(RecipeStep? before, RecipeStep? after, Dictionary<int, int> became)
    {
        if (before is null) return new StepChange(StepChangeKind.Added, null, after, [], IngredientsOf(null, after));
        if (after is null) return new StepChange(StepChangeKind.Removed, before, null, [], IngredientsOf(before, null));

        var ingredients = IngredientsOf(before, after);
        var fields = new List<string>();
        if (!SameText(before, after)) fields.Add(ActionText);
        if (!string.Equals(before.EquipmentClass, after.EquipmentClass, StringComparison.Ordinal)) fields.Add(EquipmentClass);
        if (!string.Equals(Normalize(before.TechniqueGate), Normalize(after.TechniqueGate), StringComparison.Ordinal)) fields.Add(TechniqueGate);
        if (before.DurationSeconds != after.DurationSeconds) fields.Add(DurationSeconds);
        if (before.TemperatureC != after.TemperatureC) fields.Add(TemperatureC);
        if (before.PressureBar != after.PressureBar) fields.Add(PressureBar);
        if (ingredients.Count > 0) fields.Add(Ingredients);
        if (!SameDependencies(before, after, became)) fields.Add(DependsOn);

        return new StepChange(fields.Count == 0 ? StepChangeKind.Unchanged : StepChangeKind.Changed, before, after,
            fields, ingredients);
    }

    /// <summary>The ingredients whose quantity or unit differs, or that only one side uses.</summary>
    private static List<IngredientChange> IngredientsOf(RecipeStep? before, RecipeStep? after)
    {
        var old = (before?.Ingredients ?? []).ToDictionary(i => i.IngredientId);
        var now = (after?.Ingredients ?? []).ToDictionary(i => i.IngredientId);
        return
        [
            .. old.Keys.Union(now.Keys).Order()
                .Select(id => (Id: id, Old: old.GetValueOrDefault(id), Now: now.GetValueOrDefault(id)))
                .Where(x => x.Old is null || x.Now is null || x.Old.Quantity != x.Now.Quantity
                            || !string.Equals(x.Old.Unit, x.Now.Unit, StringComparison.Ordinal))
                .Select(x => new IngredientChange(x.Id, x.Old?.Quantity, x.Old?.Unit, x.Now?.Quantity, x.Now?.Unit)),
        ];
    }

    /// <summary>The same prerequisites, once those of the older step are read as the steps they became.</summary>
    private static bool SameDependencies(RecipeStep before, RecipeStep after, Dictionary<int, int> became)
    {
        var old = before.Dependencies
            .Select(d => (Step: became.TryGetValue(d.DependsOnStep.StepOrder, out var order) ? order : -1, d.DependencyType))
            .ToHashSet();
        var now = after.Dependencies.Select(d => (Step: d.DependsOnStep.StepOrder, d.DependencyType)).ToHashSet();
        return old.SetEquals(now);
    }

    private static bool SameText(RecipeStep a, RecipeStep b) =>
        string.Equals(Normalize(a.ActionText), Normalize(b.ActionText), StringComparison.Ordinal);

    /// <summary>Case and spacing are not a change of procedure.</summary>
    private static string? Normalize(string? text) =>
        text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
