using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrewForge.Domain.MasterData;
using Json.Schema;

namespace BrewForge.Application.Recipes.Drafting;

/// <summary>
/// The schema of an extraction: what the model may return when it transcribes
/// an existing recipe document. It is the draft schema of UC-06 with two
/// additions, made here from that schema so the two can never drift apart:
/// every step says which passage of the document it was taken from, and
/// whatever the document says that the structure has no place for is listed
/// instead of being dropped in silence.
/// </summary>
public static class RecipeExtractSchema
{
    public const string SourceQuote = "sourceQuote";
    public const string Unmapped = "unmapped";

    public const int MaxUnmapped = 30;

    private static readonly Lazy<string> SchemaText = new(() =>
    {
        var schema = JsonNode.Parse(RecipeDraftSchema.Text)!.AsObject();
        schema["$id"] = "https://brewforge.local/schemas/recipe-extract.schema.json";
        schema["title"] = "BrewForgeRecipeExtraction";
        schema["description"] =
            "The only shape the language model may return when it transcribes an existing recipe document. " +
            "The draft schema, plus the passage each step was taken from and the passages that could not be expressed.";

        schema["required"]!.AsArray().Add(Unmapped);
        schema["properties"]![Unmapped] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = MaxUnmapped,
            ["description"] = "Passages of the document that say something about the preparation and that no field of a step can hold. Copied exactly. Empty when everything was expressed.",
            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 300 },
        };

        var step = schema["properties"]!["steps"]!["items"]!.AsObject();
        step["required"]!.AsArray().Add(SourceQuote);
        step["properties"]![SourceQuote] = new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 3,
            ["maxLength"] = 500,
            ["description"] = "The passage of the document this step was taken from, copied exactly as it is written there.",
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    });

    private static readonly Lazy<JsonSchema> Compiled = new(() => JsonSchema.FromText(SchemaText.Value));

    public static string Text => SchemaText.Value;

    internal static JsonSchema Schema => Compiled.Value;
}
