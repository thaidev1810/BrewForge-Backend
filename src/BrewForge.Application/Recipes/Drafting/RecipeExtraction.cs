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

/// <summary>One step of an extraction and whether the passage it claims is really in the document.</summary>
public sealed record ExtractedStep(int StepOrder, string SourceQuote, bool Grounded);

/// <summary>A conforming extraction: the draft, and where it says it came from.</summary>
public sealed record ParsedExtraction(ParsedDraft Draft, IReadOnlyList<ExtractedStep> Steps,
    IReadOnlyList<string> Unmapped)
{
    public int GroundedSteps => Steps.Count(step => step.Grounded);
}

/// <summary>
/// Whether a passage a model quotes is a passage of the document. A model
/// asked to transcribe may instead write the recipe it knows; a quote that
/// cannot be found in the document is how that shows. Case, spacing and the
/// difference between straight and typographic punctuation are not held
/// against a quote: a model rarely reproduces those exactly, and they do not
/// change what was said.
/// </summary>
public static class SourceGrounding
{
    public static bool IsQuoteOf(string quote, string document)
    {
        var needle = Normalize(quote);
        return needle.Length > 0 && Normalize(document).Contains(needle, StringComparison.Ordinal);
    }

    internal static string Normalize(string text)
    {
        var normalized = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }
            if (pendingSpace) normalized.Append(' ');
            pendingSpace = false;
            normalized.Append(character switch
            {
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                _ => char.ToLowerInvariant(character),
            });
        }
        return normalized.ToString();
    }
}

public static class RecipeExtractionParser
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    /// <summary>
    /// Decides whether a model response is an acceptable extraction of the
    /// document (BR-07). It has to conform as a draft does, in the schema of
    /// an extraction, and at least one of its steps has to quote the
    /// document: an answer of which no step can be found there is not a
    /// transcription of this document at all. A step that cannot be found is
    /// otherwise kept and reported, for a person to judge.
    /// </summary>
    public static (DraftParseResult Result, ParsedExtraction? Extraction) Parse(string? rawResponse, string document,
        IEnumerable<Ingredient> ingredients, IEnumerable<StandardEquipment> equipment)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return Failure("the response is empty");

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(rawResponse);
        }
        catch (JsonException exception)
        {
            return Failure($"the response is not valid JSON: {exception.Message}");
        }

        using (json)
        {
            var evaluation = RecipeExtractSchema.Schema.Evaluate(json.RootElement, Options);
            if (!evaluation.IsValid)
            {
                var problems = (evaluation.Details ?? [])
                    .Where(detail => detail.Errors is { Count: > 0 })
                    .SelectMany(detail => detail.Errors!.Select(error =>
                        $"{(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {error.Value}"))
                    .Distinct()
                    .ToList();
                return (new DraftParseResult(null, problems.Count > 0 ? problems : ["the response does not satisfy the schema"]), null);
            }

            // The same reading as any draft: codes and classes of the catalogue, its own steps.
            var draft = RecipeDraftParser.Map(json.RootElement, ingredients, equipment);
            if (!draft.Conforms) return (draft, null);

            var steps = json.RootElement.GetProperty("steps").EnumerateArray()
                .Select(step =>
                {
                    var quote = step.GetProperty(RecipeExtractSchema.SourceQuote).GetString()!;
                    return new ExtractedStep(step.GetProperty("stepOrder").GetInt32(), quote,
                        SourceGrounding.IsQuoteOf(quote, document));
                })
                .OrderBy(step => step.StepOrder).ToList();
            if (steps.TrueForAll(step => !step.Grounded))
            {
                return Failure("/steps: no step quotes a passage that is in the document");
            }

            var unmapped = json.RootElement.GetProperty(RecipeExtractSchema.Unmapped).EnumerateArray()
                .Select(item => item.GetString()!).ToList();
            return (draft, new ParsedExtraction(draft.Draft!, steps, unmapped));
        }
    }

    private static (DraftParseResult, ParsedExtraction?) Failure(string problem) =>
        (new DraftParseResult(null, [problem]), null);
}
