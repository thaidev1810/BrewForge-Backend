using System.Reflection;
using System.Text.Json;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using Json.Schema;

namespace BrewForge.Application.Recipes.Drafting;

/// <summary>The fixed JSON schema of UC-06: the only shape the language model may return.</summary>
public static class RecipeDraftSchema
{
    public const string Name = "BrewForgeRecipeDraft";

    private static readonly Lazy<string> SchemaText = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("recipe-draft.schema.json")
                           ?? throw new InvalidOperationException("recipe-draft.schema.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<JsonSchema> Compiled = new(() => JsonSchema.FromText(SchemaText.Value));

    /// <summary>The schema document, exactly as in <c>docs/reference/recipe-draft.schema.json</c>.</summary>
    public static string Text => SchemaText.Value;

    internal static JsonSchema Schema => Compiled.Value;
}

/// <summary>A draft that conforms, ready to become the content of a version.</summary>
public sealed record ParsedDraft(IReadOnlyList<StepSpec> Steps, string? Notes, int? ServingSizeMl);

/// <summary>Either a conforming draft or the reasons it does not conform.</summary>
public sealed record DraftParseResult(ParsedDraft? Draft, IReadOnlyList<string> Problems)
{
    public bool Conforms => Draft is not null;
}

/// <summary>
/// Decides whether a model response is acceptable (BR-07). A response
/// conforms when it is JSON, satisfies the fixed schema, and refers only to
/// what the prompt gave it: the ingredient codes and equipment classes of the
/// catalogue, and its own steps. A response that names an ingredient that
/// does not exist cannot be stored, let alone validated, so it is treated
/// the same as one that breaks the schema.
/// </summary>
public static class RecipeDraftParser
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public static DraftParseResult Parse(string? rawResponse, IEnumerable<Ingredient> ingredients,
        IEnumerable<StandardEquipment> equipment)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return Failure("the response is empty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawResponse);
        }
        catch (JsonException exception)
        {
            return Failure($"the response is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var evaluation = RecipeDraftSchema.Schema.Evaluate(document.RootElement, Options);
            if (!evaluation.IsValid)
            {
                var problems = (evaluation.Details ?? [])
                    .Where(detail => detail.Errors is { Count: > 0 })
                    .SelectMany(detail => detail.Errors!.Select(error =>
                        $"{(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {error.Value}"))
                    .Distinct()
                    .ToList();
                return new DraftParseResult(null, problems.Count > 0 ? problems : ["the response does not satisfy the schema"]);
            }

            return Map(document.RootElement, ingredients, equipment);
        }
    }

    internal static DraftParseResult Map(JsonElement root, IEnumerable<Ingredient> ingredients,
        IEnumerable<StandardEquipment> equipment)
    {
        var ingredientIds = ingredients.ToDictionary(i => i.IngredientCode, i => i.Id, StringComparer.Ordinal);
        var equipmentClasses = equipment.Select(e => e.EquipmentClass).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var specs = new List<StepSpec>();

        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        var orders = steps.Select(step => step.GetProperty("stepOrder").GetInt32()).ToList();
        if (!orders.Order().SequenceEqual(Enumerable.Range(1, orders.Count)))
        {
            problems.Add("/steps: stepOrder values must be unique, start at 1 and be contiguous");
        }

        foreach (var step in steps)
        {
            var order = step.GetProperty("stepOrder").GetInt32();
            var at = $"/steps[stepOrder={order}]";

            var equipmentClass = OptionalString(step, "equipmentClass");
            if (equipmentClass is not null && !equipmentClasses.Contains(equipmentClass))
            {
                problems.Add($"{at}/equipmentClass: '{equipmentClass}' was not among the equipment classes supplied");
            }

            var ingredientSpecs = new List<IngredientSpec>();
            foreach (var ingredient in step.GetProperty("ingredients").EnumerateArray())
            {
                var code = ingredient.GetProperty("ingredientCode").GetString()!;
                if (!ingredientIds.TryGetValue(code, out var ingredientId))
                {
                    problems.Add($"{at}/ingredients: '{code}' was not among the ingredient codes supplied");
                    continue;
                }
                // The column is NUMERIC(10,3); a model is free to answer with more digits than that.
                var quantity = decimal.Round(ingredient.GetProperty("quantity").GetDecimal(), 3,
                    MidpointRounding.AwayFromZero);
                if (quantity <= 0)
                {
                    problems.Add($"{at}/ingredients: the quantity of '{code}' rounds to zero");
                    continue;
                }
                ingredientSpecs.Add(new IngredientSpec(ingredientId, quantity,
                    ingredient.GetProperty("unit").GetString()));
            }
            if (ingredientSpecs.Select(i => i.IngredientId).Distinct().Count() != ingredientSpecs.Count)
            {
                problems.Add($"{at}/ingredients: an ingredient appears more than once in the step");
            }

            var dependencySpecs = new List<DependencySpec>();
            foreach (var dependency in step.GetProperty("dependsOnSteps").EnumerateArray())
            {
                var target = dependency.GetProperty("stepOrder").GetInt32();
                if (target == order)
                {
                    problems.Add($"{at}/dependsOnSteps: a step may not depend on itself");
                }
                else if (!orders.Contains(target))
                {
                    problems.Add($"{at}/dependsOnSteps: step {target} is not in the draft");
                }
                else if (dependencySpecs.Any(d => d.StepOrder == target))
                {
                    problems.Add($"{at}/dependsOnSteps: step {target} is listed more than once");
                }
                else
                {
                    dependencySpecs.Add(new DependencySpec(target,
                        dependency.GetProperty("dependencyType").GetString() == "REQUIRES_OUTPUT"
                            ? DependencyType.RequiresOutput
                            : DependencyType.FinishToStart));
                }
            }

            specs.Add(new StepSpec(order, step.GetProperty("actionText").GetString(), equipmentClass,
                OptionalString(step, "techniqueGate"), OptionalInt(step, "durationSeconds"), ingredientSpecs,
                dependencySpecs));
        }

        return problems.Count > 0
            ? new DraftParseResult(null, problems)
            : new DraftParseResult(new ParsedDraft(specs, OptionalString(root, "notes"),
                OptionalInt(root, "servingSizeMl")), []);
    }

    private static DraftParseResult Failure(string problem) => new(null, [problem]);

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
