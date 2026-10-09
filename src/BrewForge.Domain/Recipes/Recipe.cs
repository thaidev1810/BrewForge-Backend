using BrewForge.Domain.Common;

namespace BrewForge.Domain.Recipes;

/// <summary>
/// The logical beverage. Owns many versions but has at most one in RELEASED
/// state (BR-02).
/// </summary>
public sealed class Recipe : INeverDeleted
{
    private Recipe() { }

    public long Id { get; private set; }

    /// <summary>Unique and stable: the drink code used in the POS export.</summary>
    public string RecipeCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public RecipeCategory Category { get; private set; }
    public RecipeOrigin Origin { get; private set; } = RecipeOrigin.New;
    public long CreatedBy { get; private set; }
    public RecipeStatus Status { get; private set; } = RecipeStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; }

    // A recipe is the parent of versions that are retained forever (BR-01).
    string INeverDeleted.RetentionRule => "BR-01";

    public static Recipe Create(string recipeCode, string name, RecipeCategory category, RecipeOrigin origin,
        long createdBy, DateTimeOffset now)
    {
        recipeCode = recipeCode?.Trim() ?? "";
        name = name?.Trim() ?? "";
        new FieldErrors()
            .RequiredMax("recipeCode", recipeCode, 24)
            .RequiredMax("name", name, 120)
            .Check(Enum.IsDefined(category), "category", "is not a valid recipe category")
            .Check(Enum.IsDefined(origin), "origin", "is not a valid recipe origin")
            .ThrowIfAny();

        return new Recipe
        {
            RecipeCode = recipeCode,
            Name = name,
            Category = category,
            Origin = origin,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
    }
}
