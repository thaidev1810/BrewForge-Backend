using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>What a recipe document of the chain looks like, and the answers a model might give about it.</summary>
internal static class ExistingDocument
{
    public const string Text =
        """
        Oolong milk tea - house recipe

        1. Brew 18 g oolong leaf with 300 ml water in the tea brewer for 8 minutes. Water at 90 C.
        2. Add 120 ml milk tea base. Pour down the side of the cup.
        3. Garnish with 2 peach slices.

        Serve with a smile.
        """;

    public static object Step(int order, string action, string quote, string? equipment = null, int? seconds = null,
        (string Code, decimal Quantity, string Unit)[]? uses = null, int[]? dependsOn = null, string? gate = null) => new
    {
        stepOrder = order,
        actionText = action,
        equipmentClass = equipment,
        techniqueGate = gate,
        durationSeconds = seconds,
        ingredients = (uses ?? []).Select(u => new { ingredientCode = u.Code, quantity = u.Quantity, unit = u.Unit }),
        dependsOnSteps = (dependsOn ?? []).Select(d => new { stepOrder = d, dependencyType = "FINISH_TO_START" }),
        sourceQuote = quote,
    };

    public static string Answer(object[] steps, params string[] unmapped) =>
        JsonSerializer.Serialize(new { drinkName = "Oolong milk tea", category = "TEA", steps, unmapped });

    public static object Brew(string quote = "Brew 18 g oolong leaf with 300 ml water in the tea brewer for 8 minutes.") =>
        Step(1, "Brew the oolong", quote, "TEA_BREWER", 480, [("ING-OOLONG", 18m, "g"), ("ING-WATER", 300m, "ml")], gate: "Water at 90 C");

    public static object Milk(string quote = "Add 120 ml milk tea base.") =>
        Step(2, "Add the milk tea base", quote, seconds: 20, uses: [("ING-MILKBASE", 120m, "ml")], dependsOn: [1],
            gate: "Pour down the side of the cup");

    public static object Garnish(string quote = "Garnish with 2 peach slices.") =>
        Step(3, "Garnish with peach", quote, seconds: 15, uses: [("ING-PEACH", 2m, "pcs")], dependsOn: [2]);

    /// <summary>A faithful transcription: three steps, each quoting its line, and the one line that has no place.</summary>
    public static string Faithful() => Answer([Brew(), Milk(), Garnish()], "Serve with a smile.");
}

/// <summary>
/// The parts of an extraction that need no database: the schema, the check
/// that a quoted passage is in the document, the parser, and the reader of
/// uploaded documents.
/// </summary>
public sealed class DocumentExtractionUnitTests
{
    private static readonly List<Ingredient> Ingredients =
    [
        WithId(Ingredient.Create("ING-OOLONG", "Oolong tea leaf", IngredientUnit.Gram, 8, null), 1),
        WithId(Ingredient.Create("ING-WATER", "Filtered water", IngredientUnit.Millilitre, 24, null), 2),
        WithId(Ingredient.Create("ING-MILKBASE", "Milk tea base", IngredientUnit.Millilitre, 8, null), 3),
        WithId(Ingredient.Create("ING-PEACH", "Peach slice", IngredientUnit.Piece, 24, null), 4),
    ];

    /// <summary>An id as the entity would have after being stored.</summary>
    private static Ingredient WithId(Ingredient ingredient, long id)
    {
        typeof(Ingredient).GetProperty(nameof(Ingredient.Id))!.SetValue(ingredient, id);
        return ingredient;
    }

    private static readonly List<StandardEquipment> Equipment =
        [StandardEquipment.Create("EQ-BREW-01", "TEA_BREWER", 15m, 25m, DosingUnit.Gram)];

    // ---------------------------------------------------------------- the schema

    [Fact]
    public void Extraction_schema_is_the_draft_schema_plus_the_quote_of_each_step_and_what_was_not_expressed()
    {
        var draft = JsonDocument.Parse(RecipeDraftSchema.Text).RootElement;
        var extract = JsonDocument.Parse(RecipeExtractSchema.Text).RootElement;
        static string[] Required(JsonElement schema) => [.. schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)];
        static JsonElement StepOf(JsonElement schema) => schema.GetProperty("properties").GetProperty("steps").GetProperty("items");

        Assert.Equal([.. Required(draft), "unmapped"], Required(extract));
        Assert.Equal([.. Required(StepOf(draft)), "sourceQuote"], Required(StepOf(extract)));
        // Still closed: nothing beyond what is listed.
        Assert.False(extract.GetProperty("additionalProperties").GetBoolean());
        Assert.False(StepOf(extract).GetProperty("additionalProperties").GetBoolean());
        // The draft schema of the developer pack is untouched by it.
        Assert.DoesNotContain("sourceQuote", RecipeDraftSchema.Text);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A Word document with the given body, as small as one can be.</summary>
    internal static byte[] Docx(string bodyXml)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                         "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                         bodyXml + "</w:body></w:document>");
        }
        return buffer.ToArray();
    }
}
