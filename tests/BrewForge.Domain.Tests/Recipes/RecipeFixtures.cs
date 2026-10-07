using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>A small catalogue and a terse way to write a recipe, shared by the validator tests.</summary>
internal static class RecipeFixtures
{
    public const long Author = 4;

    // Ingredient ids
    public const long Oolong = 1;      // g,   shelf life 8 h
    public const long Milk = 2;        // ml,  shelf life 4 h
    public const long Peach = 3;       // pcs, shelf life 24 h
    public const long Retired = 4;     // g,   INACTIVE
    public const long Water = 5;       // ml,  shelf life 24 h
    public const long Unknown = 999;   // not in the catalogue

    public static ValidationCatalog Catalog(Action<List<StandardEquipment>, List<Ingredient>>? adjust = null)
    {
        var equipment = new List<StandardEquipment>
        {
            WithId(StandardEquipment.Create("EQ-BREW-01", "TEA_BREWER", 15m, 25m, DosingUnit.Gram), 1),
            WithId(StandardEquipment.Create("EQ-STM-01", "MILK_STEAMER", 100m, 350m, DosingUnit.Millilitre), 2),
            WithId(StandardEquipment.Create("EQ-EXT-01", "TIMED_EXTRACTOR", 25m, 32m, DosingUnit.Second), 3),
            WithId(StandardEquipment.Create("EQ-KET-01", "KETTLE", 80m, 100m, DosingUnit.DegreeCelsius), 4),
            Inactive(WithId(StandardEquipment.Create("EQ-OLD-01", "RETIRED_BREWER", 15m, 25m, DosingUnit.Gram), 5)),
        };
        var ingredients = new List<Ingredient>
        {
            WithId(Ingredient.Create("ING-OOLONG", "Oolong tea leaf", IngredientUnit.Gram, 8, null), Oolong),
            WithId(Ingredient.Create("ING-MILK", "Fresh milk", IngredientUnit.Millilitre, 4, null), Milk),
            WithId(Ingredient.Create("ING-PEACH", "Peach slice", IngredientUnit.Piece, 24, null), Peach),
            Inactive(WithId(Ingredient.Create("ING-RETIRED", "Discontinued leaf", IngredientUnit.Gram, 8, null), Retired)),
            WithId(Ingredient.Create("ING-WATER", "Filtered water", IngredientUnit.Millilitre, 24, null), Water),
        };
        adjust?.Invoke(equipment, ingredients);
        return new ValidationCatalog(equipment, ingredients);
    }

    public static RecipeVersion Draft(params StepSpec[] steps)
    {
        var version = RecipeVersion.CreateDraft(recipeId: 12, versionNo: 1, Author);
        version.ReplaceContent(steps, Author);
        return version;
    }

    public static StepSpec Step(int order, string action = "Do something", string? equipment = null,
        int? seconds = null, (long Id, decimal Quantity, string Unit)[]? uses = null, int[]? dependsOn = null) =>
        new(order, action, equipment, null, seconds,
            [.. (uses ?? []).Select(u => new IngredientSpec(u.Id, u.Quantity, u.Unit))],
            [.. (dependsOn ?? []).Select(d => new DependencySpec(d, DependencyType.FinishToStart))]);

    /// <summary>A recipe that passes all three checks.</summary>
    public static RecipeVersion ValidOolongMilkTea() => Draft(
        Step(1, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, 18m, "g"), (Water, 300m, "ml")]),
        Step(2, "Add the milk", seconds: 20, uses: [(Milk, 120m, "ml")], dependsOn: [1]),
        Step(3, "Garnish with peach", seconds: 15, uses: [(Peach, 2m, "pcs")], dependsOn: [2]));

    public static CheckResult Check(this ValidationReport report, CheckType type) =>
        report.Checks.Single(check => check.CheckType == type);

    public static T WithId<T>(T entity, long id) where T : class
    {
        typeof(T).GetProperty("Id")!.SetValue(entity, id);
        return entity;
    }

    private static Ingredient Inactive(Ingredient ingredient)
    {
        ingredient.Deactivate();
        return ingredient;
    }

    private static StandardEquipment Inactive(StandardEquipment equipment)
    {
        equipment.Deactivate();
        return equipment;
    }
}
