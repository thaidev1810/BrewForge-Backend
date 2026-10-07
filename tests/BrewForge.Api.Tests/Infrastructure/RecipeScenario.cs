using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>Builds recipes and drafts through the API, the way a specialist would.</summary>
public static class RecipeScenario
{
    public const string Recipes = "/api/v1/recipes";
    public const string Versions = "/api/v1/recipe-versions";

    public static string UniqueCode(string prefix = "T") => $"{prefix}{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    // ---------------------------------------------------------------- request bodies

    public static object Use(string ingredientCode, decimal quantity, string unit) =>
        new { ingredientCode, quantity, unit };

    public static object Step(int order, string action, string? equipment = null, int? seconds = null,
        object[]? uses = null, int[]? dependsOn = null, string? gate = null) => new
    {
        stepOrder = order,
        actionText = action,
        equipmentClass = equipment,
        techniqueGate = gate,
        durationSeconds = seconds,
        ingredients = uses ?? [],
        dependsOn = (dependsOn ?? []).Select(target => new { stepOrder = target, type = "FINISH_TO_START" }),
    };

    /// <summary>Passes all three checks against the seeded catalogue.</summary>
    public static object ValidContent() => new
    {
        steps = new[]
        {
            Step(1, "Brew the oolong", "TEA_BREWER", 480, [Use("ING-OOLONG", 18m, "g"), Use("ING-WATER", 300m, "ml")],
                gate: "Water at 90 C"),
            Step(2, "Add the milk base", seconds: 20, uses: [Use("ING-MILKBASE", 120m, "ml")], dependsOn: [1],
                gate: "Pour down the side"),
            Step(3, "Garnish with peach", seconds: 15, uses: [Use("ING-PEACH", 2m, "pcs")], dependsOn: [2]),
        },
    };

    /// <summary>Fails the equipment check only: 40 g in the 15-25 g brewer.</summary>
    public static object OverdosedContent() => new
    {
        steps = new[]
        {
            Step(1, "Brew far too much oolong", "TEA_BREWER", 480, [Use("ING-OOLONG", 40m, "g")]),
            Step(2, "Add the milk base", seconds: 20, uses: [Use("ING-MILKBASE", 120m, "ml")], dependsOn: [1]),
        },
    };

    // ---------------------------------------------------------------- model answers

    /// <summary>A model answer in the shape of recipe-draft.schema.json.</summary>
    public static string ModelDraft(params object[] steps) =>
        JsonSerializer.Serialize(new { drinkName = "Test drink", category = "TEA", steps });

    public static object ModelStep(int order, string action, string? equipment = null, int? seconds = null,
        (string Code, decimal Quantity, string Unit)[]? uses = null, int[]? dependsOn = null) => new
    {
        stepOrder = order,
        actionText = action,
        equipmentClass = equipment,
        techniqueGate = (string?)null,
        durationSeconds = seconds,
        ingredients = (uses ?? []).Select(u => new { ingredientCode = u.Code, quantity = u.Quantity, unit = u.Unit }),
        dependsOnSteps = (dependsOn ?? []).Select(d => new { stepOrder = d, dependencyType = "FINISH_TO_START" }),
    };

    /// <summary>A conforming answer that also passes validation.</summary>
    public static string ValidModelDraft() => ModelDraft(
        ModelStep(1, "Brew the oolong", "TEA_BREWER", 480, [("ING-OOLONG", 20m, "g")]),
        ModelStep(2, "Add the milk base", seconds: 20, uses: [("ING-MILKBASE", 100m, "ml")], dependsOn: [1]));

    // ---------------------------------------------------------------- scenarios

    public static async Task<long> NewRecipeAsync(this BrewForgeApiFactory factory, string category = "TEA",
        string origin = "NEW")
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var code = UniqueCode();
        var created = await (await specialist.PostAsJsonAsync(Recipes,
                new { recipeCode = code, name = $"Test drink {code}", category, origin }))
            .ShouldBeAsync(HttpStatusCode.Created);
        return created.GetProperty("id").GetInt64();
    }

    /// <summary>A new recipe with one DRAFT version holding the given content.</summary>
    public static async Task<(long RecipeId, long VersionId)> NewDraftAsync(this BrewForgeApiFactory factory,
        object? content = null)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var recipeId = await factory.NewRecipeAsync();
        var version = await (await specialist.PostAsJsonAsync($"{Recipes}/{recipeId}/versions", new { }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var versionId = version.GetProperty("id").GetInt64();

        if (content is not null)
        {
            await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}", content)).ShouldBeAsync(HttpStatusCode.OK);
        }
        return (recipeId, versionId);
    }

    public static async Task<JsonElement> GetVersionAsync(this BrewForgeApiFactory factory, long versionId)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        return await (await specialist.GetAsync($"{Versions}/{versionId}")).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static JsonElement Check(this JsonElement validation, string checkType) =>
        validation.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("checkType").GetString() == checkType);
}
