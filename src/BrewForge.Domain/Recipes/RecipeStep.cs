namespace BrewForge.Domain.Recipes;

/// <summary>What a caller supplies to describe one step of a draft.</summary>
public sealed record StepSpec(int StepOrder, string? ActionText, string? EquipmentClass, string? TechniqueGate,
    int? DurationSeconds, IReadOnlyList<IngredientSpec> Ingredients, IReadOnlyList<DependencySpec> DependsOn);

public sealed record IngredientSpec(long IngredientId, decimal Quantity, string? Unit);

/// <summary>A dependency on another step of the same draft, named by its <c>step_order</c>.</summary>
public sealed record DependencySpec(int StepOrder, DependencyType Type);

/// <summary>An ordered step of a recipe version. Owned by <see cref="RecipeVersion"/>.</summary>
public sealed class RecipeStep
{
    private readonly List<StepIngredient> _ingredients = [];
    private readonly List<StepDependency> _dependencies = [];

    private RecipeStep() { }

    internal RecipeStep(StepSpec spec)
    {
        StepOrder = spec.StepOrder;
        ActionText = spec.ActionText!.Trim();
        EquipmentClass = string.IsNullOrWhiteSpace(spec.EquipmentClass) ? null : spec.EquipmentClass.Trim();
        TechniqueGate = string.IsNullOrWhiteSpace(spec.TechniqueGate) ? null : spec.TechniqueGate.Trim();
        DurationSeconds = spec.DurationSeconds;
        _ingredients.AddRange(spec.Ingredients.Select(i => new StepIngredient(i.IngredientId, i.Quantity, i.Unit!.Trim())));
    }

    public long Id { get; private set; }
    public long RecipeVersionId { get; private set; }

    /// <summary>1-based and unique within the version.</summary>
    public int StepOrder { get; private set; }
    public string ActionText { get; private set; } = null!;

    /// <summary>References <c>standard_equipment.equipment_class</c>; null when the step uses no machine.</summary>
    public string? EquipmentClass { get; private set; }

    /// <summary>The manual technique a trainer confirms by observation (BR-17).</summary>
    public string? TechniqueGate { get; private set; }
    public int? DurationSeconds { get; private set; }

    public IReadOnlyList<StepIngredient> Ingredients => _ingredients;

    /// <summary>The steps this step depends on.</summary>
    public IReadOnlyList<StepDependency> Dependencies => _dependencies;

    internal void DependOn(RecipeStep prerequisite, DependencyType type) =>
        _dependencies.Add(new StepDependency(this, prerequisite, type));

    internal void SetActionText(string actionText) => ActionText = actionText.Trim();
}

/// <summary>An ingredient consumed by a step, with the quantity the validator checks.</summary>
public sealed class StepIngredient
{
    private StepIngredient() { }

    internal StepIngredient(long ingredientId, decimal quantity, string unit)
    {
        IngredientId = ingredientId;
        Quantity = quantity;
        Unit = unit;
    }

    public long Id { get; private set; }
    public long StepId { get; private set; }
    public long IngredientId { get; private set; }
    public decimal Quantity { get; private set; }

    /// <summary>Must be convertible to the ingredient's own unit (BR-11).</summary>
    public string Unit { get; private set; } = null!;
}

/// <summary>A directed edge of the step dependency graph, which must be acyclic (BR-09).</summary>
public sealed class StepDependency
{
    private StepDependency() { }

    internal StepDependency(RecipeStep step, RecipeStep dependsOnStep, DependencyType type)
    {
        Step = step;
        DependsOnStep = dependsOnStep;
        DependencyType = type;
    }

    public long Id { get; private set; }
    public long StepId { get; private set; }
    public RecipeStep Step { get; private set; } = null!;
    public long DependsOnStepId { get; private set; }
    public RecipeStep DependsOnStep { get; private set; } = null!;
    public DependencyType DependencyType { get; private set; } = DependencyType.FinishToStart;
}
