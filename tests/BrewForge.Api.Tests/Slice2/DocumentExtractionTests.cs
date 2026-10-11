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

    // ---------------------------------------------------------------- grounding

    [Theory]
    [InlineData("Add 120 ml milk tea base.", true)]
    [InlineData("add 120 ML   milk\ttea base.", true)]
    [InlineData("Brew 18 g oolong leaf with 300 ml water in the tea brewer for 8 minutes. Water at 90 C.", true)]
    [InlineData("Add 100 ml milk tea base.", false)]
    [InlineData("Shake with ice for ten seconds.", false)]
    [InlineData("   ", false)]
    public void Quote_is_grounded_when_the_document_says_it_whatever_the_case_and_spacing(string quote, bool grounded) =>
        Assert.Equal(grounded, SourceGrounding.IsQuoteOf(quote, ExistingDocument.Text));

    [Fact]
    public void Typographic_quotes_and_dashes_are_not_held_against_a_quote()
    {
        const string document = "Stir “gently” for 10–15 seconds — don’t whisk.";

        Assert.True(SourceGrounding.IsQuoteOf("Stir \"gently\" for 10-15 seconds - don't whisk.", document));
        Assert.False(SourceGrounding.IsQuoteOf("Stir \"firmly\" for 10-15 seconds", document));
    }

    // ---------------------------------------------------------------- the parser

    [Fact]
    public void Faithful_answer_is_an_extraction_with_every_step_grounded()
    {
        var (result, extraction) = RecipeExtractionParser.Parse(ExistingDocument.Faithful(), ExistingDocument.Text, Ingredients, Equipment);

        Assert.True(result.Conforms, string.Join("; ", result.Problems));
        Assert.Equal(3, result.Draft!.Steps.Count);
        Assert.Equal((3, 3), (extraction!.Steps.Count, extraction.GroundedSteps));
        Assert.Equal(["Serve with a smile."], extraction.Unmapped);
    }

    [Fact]
    public void Step_the_document_does_not_contain_is_kept_and_marked()
    {
        var answer = ExistingDocument.Answer([ExistingDocument.Brew(), ExistingDocument.Milk("Shake hard with ice.")]);

        var (result, extraction) = RecipeExtractionParser.Parse(answer, ExistingDocument.Text, Ingredients, Equipment);

        Assert.True(result.Conforms);
        Assert.Equal([(1, true), (2, false)], extraction!.Steps.Select(step => (step.StepOrder, step.Grounded)));
    }

    [Fact]
    public void Answer_is_not_an_extraction_when_no_step_is_in_the_document_or_the_quotes_are_missing()
    {
        var invented = ExistingDocument.Answer([ExistingDocument.Brew("Steep the leaves until fragrant."), ExistingDocument.Milk("Top with foam.")]);
        var aDraft = ValidModelDraft(); // conforms as a draft, and says nothing of where it came from
        var unknownCode = ExistingDocument.Answer(
            [ExistingDocument.Step(1, "Brew the oolong", "Brew 18 g oolong leaf", uses: [("ING-INVENTED", 18m, "g")])]);

        Assert.All(new[] { invented, aDraft, unknownCode, "not json", "" }, answer =>
        {
            var (result, extraction) = RecipeExtractionParser.Parse(answer, ExistingDocument.Text, Ingredients, Equipment);
            Assert.False(result.Conforms);
            Assert.Null(extraction);
            Assert.NotEmpty(result.Problems);
        });
    }

    // ---------------------------------------------------------------- uploaded documents

    [Fact]
    public async Task Text_and_markdown_are_read_as_utf8()
    {
        var reader = new DocumentTextReader();
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Trà sữa ô long: 18 g")).ToArray();

        Assert.Equal("Trà sữa ô long: 18 g", await reader.ReadAsync(new MemoryStream(withBom), "recipe.txt", CancellationToken.None));
        Assert.Equal("# Oolong", await reader.ReadAsync(new MemoryStream("# Oolong"u8.ToArray()), "RECIPE.MD", CancellationToken.None));
    }

    [Fact]
    public async Task Word_document_is_read_paragraph_by_paragraph_with_table_rows_on_one_line()
    {
        var reader = new DocumentTextReader();

        var text = await reader.ReadAsync(new MemoryStream(Docx(
            "<w:p><w:r><w:t>Oolong milk tea</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t xml:space=\"preserve\">1. Brew 18 g </w:t></w:r><w:r><w:t>oolong leaf.</w:t></w:r></w:p>" +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Milk tea base</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>120 ml</w:t></w:r></w:p></w:tc></w:tr></w:tbl>")),
            "recipe.docx", CancellationToken.None);

        Assert.Equal(["Oolong milk tea", "1. Brew 18 g oolong leaf.", "Milk tea base | 120 ml"],
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')));
    }

    [Theory]
    [InlineData("recipe.pdf", "text")]
    [InlineData("recipe", "text")]
    [InlineData("recipe.txt", "")]
    [InlineData("recipe.docx", "this is not a zip archive")]
    public async Task File_that_is_not_a_document_that_can_be_read_is_refused(string fileName, string content)
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() =>
            new DocumentTextReader().ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), fileName, CancellationToken.None));

        Assert.Equal(ErrorKind.Validation, refusal.Kind);
        Assert.Contains(refusal.Details, detail => detail.Field == "file");
    }

    [Fact]
    public async Task File_that_is_not_text_or_is_too_large_is_refused()
    {
        var reader = new DocumentTextReader();
        byte[] notText = [0xFF, 0xFE, 0x00, 0xD8, 0x41];

        await Assert.ThrowsAsync<DomainException>(() => reader.ReadAsync(new MemoryStream(notText), "recipe.txt", CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() =>
            reader.ReadAsync(new MemoryStream(new byte[DocumentTextReader.MaxBytes + 1]), "recipe.txt", CancellationToken.None));
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
