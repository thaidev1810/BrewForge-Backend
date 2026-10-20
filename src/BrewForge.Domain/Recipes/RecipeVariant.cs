namespace BrewForge.Domain.Recipes;

/// <summary>What a caller supplies to describe one variant of a draft.</summary>
public sealed record VariantSpec(string? Code, string? Name, decimal Scale,
    IReadOnlyList<VariantIngredientSpec> Ingredients);

/// <summary>An ingredient that does not follow the scale of its variant. A scale of 0 leaves it out.</summary>
public sealed record VariantIngredientSpec(long IngredientId, decimal Scale);

/// <summary>An ingredient of a step as a variant serves it, beside what the version itself prescribes.</summary>
public sealed record ScaledIngredient(long IngredientId, decimal BaseQuantity, decimal Quantity, string Unit);

/// <summary>A step as a variant serves it. Only the quantities differ from the step of the version.</summary>
public sealed record ScaledStep(RecipeStep Step, IReadOnlyList<ScaledIngredient> Ingredients);

/// <summary>
/// One way a recipe version is served: a size, hot or iced. It is the same
/// procedure, step for step, with the quantities scaled: every ingredient by
/// the scale of the variant, except those given a scale of their own. It
/// prescribes nothing the version does not, so it has no steps: it is read
/// by applying it to them. Owned by <see cref="RecipeVersion"/> and frozen
/// with it on release (BR-01).
/// </summary>
public sealed class RecipeVariant
{
    public const int MaxVariants = 12;
    public const decimal MaxScale = 5m;

    private readonly List<RecipeVariantIngredient> _ingredients = [];

    private RecipeVariant() { }

    internal RecipeVariant(VariantSpec spec)
    {
        VariantCode = spec.Code!.Trim().ToUpperInvariant();
        Name = spec.Name!.Trim();
        Scale = spec.Scale;
        _ingredients.AddRange(spec.Ingredients.Select(i => new RecipeVariantIngredient(i.IngredientId, i.Scale)));
    }

    public long Id { get; private set; }
    public long RecipeVersionId { get; private set; }

    /// <summary>Unique within the version: what the menu and the till call this serving, such as L or M-HOT.</summary>
    public string VariantCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>What every quantity of the version is multiplied by, unless the ingredient has a scale of its own.</summary>
    public decimal Scale { get; private set; }

    public IReadOnlyList<RecipeVariantIngredient> Ingredients => _ingredients;

    public decimal ScaleOf(long ingredientId) =>
        _ingredients.FirstOrDefault(i => i.IngredientId == ingredientId)?.Scale ?? Scale;

    /// <summary>
    /// The steps of the version as this variant serves them. A quantity is
    /// rounded to the three decimals a quantity has; an ingredient scaled to
    /// nothing is left out of its step.
    /// </summary>
    public IReadOnlyList<ScaledStep> Apply(RecipeVersion version) =>
    [
        .. version.OrderedSteps().Select(step => new ScaledStep(step,
        [
            .. step.Ingredients
                .Select(i => new ScaledIngredient(i.IngredientId, i.Quantity,
                    decimal.Round(i.Quantity * ScaleOf(i.IngredientId), 3, MidpointRounding.AwayFromZero), i.Unit))
                .Where(i => i.Quantity > 0),
        ])),
    ];

    /// <summary>An ingredient the version no longer uses has no scale to keep.</summary>
    internal void ForgetIngredientsNotIn(IReadOnlySet<long> usedIngredientIds) =>
        _ingredients.RemoveAll(i => !usedIngredientIds.Contains(i.IngredientId));

    internal VariantSpec ToSpec() =>
        new(VariantCode, Name, Scale, [.. _ingredients.Select(i => new VariantIngredientSpec(i.IngredientId, i.Scale))]);
}

/// <summary>The scale of one ingredient within a variant, where it differs from the scale of the variant.</summary>
public sealed class RecipeVariantIngredient
{
    private RecipeVariantIngredient() { }

    internal RecipeVariantIngredient(long ingredientId, decimal scale)
    {
        IngredientId = ingredientId;
        Scale = scale;
    }

    public long Id { get; private set; }
    public long RecipeVariantId { get; private set; }
    public long IngredientId { get; private set; }

    /// <summary>0 leaves the ingredient out of the variant altogether.</summary>
    public decimal Scale { get; private set; }
}
