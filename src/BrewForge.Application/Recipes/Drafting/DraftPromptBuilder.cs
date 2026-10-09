using System.Globalization;
using System.Text;
using System.Text.Json;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Application.Recipes.Drafting;

/// <summary>
/// Assembles the prompts of UC-06 and UC-08 from the description and the
/// active master data. The model is told the catalogue explicitly, because
/// it may only use what exists there.
/// </summary>
public static class DraftPromptBuilder
{
    private const string SystemPrompt =
        """
        You write beverage preparation procedures for BrewForge, a Vietnamese specialty tea and coffee chain.
        You answer with one JSON document that satisfies the supplied JSON schema, and nothing else.

        Rules you must follow:
        - Use only the ingredient codes listed under INGREDIENTS. Never invent a code.
        - Use only the equipment classes listed under EQUIPMENT, or null when a step uses no machine.
        - When a step names an equipment class, every ingredient quantity of that step measured in the
          class's dosing unit must lie within the class's min-max range.
        - Give quantities in the unit listed for the ingredient (g, ml or pcs).
        - Number the steps 1, 2, 3, ... with no gaps, in the order a barista performs them.
        - In dependsOnSteps list only earlier steps. A step never depends on itself, and the dependencies
          must not form a cycle.
        - An ingredient must not stay in use longer than its shelf life in hours, counted from the step
          that first uses it to the end of the last step.
        - actionText is what the barista physically does, in the imperative.
        - techniqueGate is a manual technique a trainer can confirm by watching, or null.
        """;

    public static DraftModelRequest ForNewDraft(Recipe recipe, string description,
        IEnumerable<Ingredient> ingredients, IEnumerable<StandardEquipment> equipment)
    {
        var user = new StringBuilder()
            .AppendLine($"Write the preparation procedure for this drink.")
            .AppendLine()
            .AppendLine($"DRINK: {recipe.Name} (category {recipe.Category.Code()})")
            .AppendLine("DESCRIPTION:")
            .AppendLine(description.Trim())
            .AppendLine();
        AppendCatalogue(user, ingredients, equipment);

        return new DraftModelRequest(SystemPrompt, user.ToString(), RecipeDraftSchema.Text);
    }

    public static DraftModelRequest ForRepair(Recipe recipe, RecipeVersion version, ValidationReport report,
        IEnumerable<Ingredient> ingredients, IEnumerable<StandardEquipment> equipment)
    {
        var catalogue = ingredients.ToList();
        var codes = catalogue.ToDictionary(i => i.Id, i => i.IngredientCode);

        var user = new StringBuilder()
            .AppendLine("The draft below failed validation. Return a corrected draft that resolves every violation")
            .AppendLine("and changes nothing else.")
            .AppendLine()
            .AppendLine($"DRINK: {recipe.Name} (category {recipe.Category.Code()})")
            .AppendLine()
            .AppendLine("CURRENT DRAFT:")
            .AppendLine(DraftJson(recipe, version, codes))
            .AppendLine()
            .AppendLine("VIOLATIONS:");
        foreach (var check in report.Checks.Where(check => !check.Passed))
        {
            foreach (var violation in check.Violations)
            {
                user.AppendLine(CultureInfo.InvariantCulture,
                    $"- [{check.CheckType.Code()}] step {violation.StepOrder?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {violation.Message} (expected {violation.Expected}, actual {violation.Actual})");
            }
        }
        user.AppendLine();
        AppendCatalogue(user, catalogue, equipment);

        return new DraftModelRequest(SystemPrompt, user.ToString(), RecipeDraftSchema.Text);
    }

    private static void AppendCatalogue(StringBuilder prompt, IEnumerable<Ingredient> ingredients,
        IEnumerable<StandardEquipment> equipment)
    {
        prompt.AppendLine("INGREDIENTS (code | name | unit | shelf life in hours):");
        foreach (var ingredient in ingredients.Where(i => i.IsActive).OrderBy(i => i.IngredientCode, StringComparer.Ordinal))
        {
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"- {ingredient.IngredientCode} | {ingredient.Name} | {ingredient.Unit.Code()} | {ingredient.ShelfLifeHours}");
        }

        prompt.AppendLine().AppendLine("EQUIPMENT (class | min | max | dosing unit):");
        foreach (var item in equipment.Where(e => e.IsActive).OrderBy(e => e.EquipmentClass, StringComparer.Ordinal))
        {
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.EquipmentClass} | {item.MinThreshold:0.###} | {item.MaxThreshold:0.###} | {item.DosingUnit.Code()}");
        }
    }

    /// <summary>The current content in the same shape the model is asked to return.</summary>
    private static string DraftJson(Recipe recipe, RecipeVersion version, Dictionary<long, string> ingredientCodes) =>
        JsonSerializer.Serialize(new
        {
            drinkName = recipe.Name,
            category = recipe.Category.Code(),
            steps = version.OrderedSteps().Select(step => new
            {
                stepOrder = step.StepOrder,
                actionText = step.ActionText,
                equipmentClass = step.EquipmentClass,
                techniqueGate = step.TechniqueGate,
                durationSeconds = step.DurationSeconds,
                ingredients = step.Ingredients.Select(i => new
                {
                    ingredientCode = ingredientCodes.GetValueOrDefault(i.IngredientId, $"#{i.IngredientId}"),
                    quantity = i.Quantity,
                    unit = i.Unit,
                }),
                dependsOnSteps = step.Dependencies.Select(d => new
                {
                    stepOrder = d.DependsOnStep.StepOrder,
                    dependencyType = d.DependencyType.Code(),
                }),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
}
