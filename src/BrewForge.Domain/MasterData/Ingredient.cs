using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

/// <summary>The <c>ingredient_unit</c> enumeration.</summary>
public enum IngredientUnit
{
    [Code("g")] Gram,
    [Code("ml")] Millilitre,
    [Code("pcs")] Piece,
}

/// <summary>A raw material with its unit, shelf-life rule and storage rule.</summary>
public sealed class Ingredient : INeverDeleted
{
    private Ingredient() { }

    public long Id { get; private set; }

    /// <summary>Unique and stable: recipes, the LLM prompt and the POS mapping refer to it.</summary>
    public string IngredientCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public IngredientUnit Unit { get; private set; }

    /// <summary>Hours from preparation during which the ingredient may be used (BR-11).</summary>
    public int ShelfLifeHours { get; private set; }
    public string? StorageRule { get; private set; }

    /// <summary>
    /// The lowest and the highest water temperature this leaf is brewed at,
    /// in degrees Celsius. Both or neither: most ingredients are not brewed
    /// and have no window.
    /// </summary>
    public decimal? BrewTempMinC { get; private set; }
    public decimal? BrewTempMaxC { get; private set; }

    public bool HasBrewingWindow => BrewTempMinC is not null && BrewTempMaxC is not null;

    public CatalogStatus Status { get; private set; } = CatalogStatus.Active;

    public bool IsActive => Status == CatalogStatus.Active;

    string INeverDeleted.RetentionRule => "BR-16";

    public static Ingredient Create(string ingredientCode, string name, IngredientUnit unit,
        int shelfLifeHours, string? storageRule)
    {
        ingredientCode = ingredientCode?.Trim() ?? "";
        new FieldErrors().RequiredMax("ingredientCode", ingredientCode, 24).ThrowIfAny();

        var ingredient = new Ingredient { IngredientCode = ingredientCode };
        ingredient.Update(name, unit, shelfLifeHours, storageRule);
        return ingredient;
    }

    public void Update(string name, IngredientUnit unit, int shelfLifeHours, string? storageRule)
    {
        name = name?.Trim() ?? "";
        storageRule = string.IsNullOrWhiteSpace(storageRule) ? null : storageRule.Trim();
        new FieldErrors()
            .RequiredMax("name", name, 120)
            .Check(Enum.IsDefined(unit), "unit", "is not a valid ingredient unit")
            .Check(shelfLifeHours > 0, "shelfLifeHours", "must be greater than 0")
            .MaxLength("storageRule", storageRule, 255)
            .ThrowIfAny();

        Name = name;
        Unit = unit;
        ShelfLifeHours = shelfLifeHours;
        StorageRule = storageRule;
    }

    /// <summary>Sets or, with two nulls, removes the temperature window the leaf is brewed in.</summary>
    public void SetBrewingWindow(decimal? minC, decimal? maxC)
    {
        new FieldErrors()
            .Check(minC is null == maxC is null, "brewTempMaxC", "the window needs both a minimum and a maximum, or neither")
            .Check(minC is null or >= 0, "brewTempMinC", "must not be below 0")
            .Check(maxC is null or <= 100, "brewTempMaxC", "must not be above 100")
            .Check(minC is null || maxC is null || minC <= maxC, "brewTempMinC", "must not be above the maximum")
            .ThrowIfAny();

        BrewTempMinC = minC is { } min ? decimal.Round(min, 1) : null;
        BrewTempMaxC = maxC is { } max ? decimal.Round(max, 1) : null;
    }

    /// <summary>An ingredient is never deleted, only deactivated (BR-16).</summary>
    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Reactivate() => Status = CatalogStatus.Active;
}
