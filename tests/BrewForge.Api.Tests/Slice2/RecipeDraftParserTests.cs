using System.Text.Json;
using System.Text.Json.Nodes;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Infrastructure.Llm;
using Microsoft.Extensions.Options;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>
/// BR-07: what counts as a conforming model response. The first group
/// repeats the seven malformed documents the developer pack tested the
/// schema with; each must be refused for its own reason.
/// </summary>
public sealed class RecipeDraftParserTests
{
    private static readonly Ingredient[] Ingredients =
    [
        WithId(Ingredient.Create("ING-OOLONG", "Oolong tea leaf", IngredientUnit.Gram, 8, null), 1),
        WithId(Ingredient.Create("ING-MILKBASE", "Milk tea base", IngredientUnit.Millilitre, 8, null), 2),
    ];

    private static readonly StandardEquipment[] Equipment =
    [
        StandardEquipment.Create("EQ-BREW-01", "TEA_BREWER", 15m, 25m, DosingUnit.Gram),
    ];

    private static DraftParseResult Parse(string raw) => RecipeDraftParser.Parse(raw, Ingredients, Equipment);

    /// <summary>A conforming document as a mutable tree, for one deliberate defect at a time.</summary>
    private static JsonObject Valid() => JsonNode.Parse(ValidModelDraft())!.AsObject();

    private static JsonObject FirstStep(JsonObject draft) => draft["steps"]![0]!.AsObject();

    private static JsonObject FirstIngredient(JsonObject draft) => FirstStep(draft)["ingredients"]![0]!.AsObject();

    [Fact]
    public void Conforming_answer_is_mapped_to_step_specifications()
    {
        var result = Parse(ValidModelDraft());

        Assert.True(result.Conforms, string.Join("; ", result.Problems));
        var steps = result.Draft!.Steps;
        Assert.Equal([1, 2], steps.Select(s => s.StepOrder));
        Assert.Equal("TEA_BREWER", steps[0].EquipmentClass);
        Assert.Equal(new IngredientSpec(1, 20m, "g"), Assert.Single(steps[0].Ingredients));
        Assert.Equal(new DependencySpec(1, DependencyType.FinishToStart), Assert.Single(steps[1].DependsOn));
    }

    // ---------------------------------------------------------------- the seven malformed documents

    [Fact]
    public void Free_text_instead_of_step_objects_is_refused()
    {
        var draft = Valid();
        draft["steps"] = "Brew the tea, add milk, serve.";

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Unknown_unit_is_refused()
    {
        var draft = Valid();
        FirstIngredient(draft)["unit"] = "cup";

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Zero_quantity_is_refused()
    {
        var draft = Valid();
        FirstIngredient(draft)["quantity"] = 0;

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Invented_extra_field_is_refused()
    {
        var draft = Valid();
        FirstStep(draft)["barista_mood"] = "cheerful";

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Category_outside_the_enumeration_is_refused()
    {
        var draft = Valid();
        draft["category"] = "SMOOTHIE";

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Missing_dependsOnSteps_is_refused()
    {
        var draft = Valid();
        FirstStep(draft).Remove("dependsOnSteps");

        Assert.False(Parse(draft.ToJsonString()).Conforms);
    }

    [Fact]
    public void Forty_one_steps_are_refused_and_forty_are_accepted()
    {
        static string Draft(int count) => ModelDraft([.. Enumerable.Range(1, count).Select(order => ModelStep(order, $"Step {order}"))]);

        Assert.True(Parse(Draft(40)).Conforms);
        Assert.False(Parse(Draft(41)).Conforms);
    }

    // ---------------------------------------------------------------- beyond the schema

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"drinkName\": ")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Anything_that_is_not_a_draft_document_is_refused(string raw)
    {
        var result = Parse(raw);

        Assert.False(result.Conforms);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Ingredient_code_that_was_not_supplied_is_refused()
    {
        var draft = Valid();
        FirstIngredient(draft)["ingredientCode"] = "ING-UNICORN";

        var result = Parse(draft.ToJsonString());

        Assert.False(result.Conforms);
        Assert.Contains(result.Problems, p => p.Contains("ING-UNICORN"));
    }

    [Fact]
    public void Equipment_class_that_was_not_supplied_is_refused()
    {
        var draft = Valid();
        FirstStep(draft)["equipmentClass"] = "PLASMA_BREWER";

        Assert.Contains(Parse(draft.ToJsonString()).Problems, p => p.Contains("PLASMA_BREWER"));
    }

    [Fact]
    public void Step_that_depends_on_itself_or_on_a_missing_step_is_refused()
    {
        var onItself = Valid();
        FirstStep(onItself)["dependsOnSteps"] = new JsonArray(new JsonObject { ["stepOrder"] = 1, ["dependencyType"] = "FINISH_TO_START" });
        var onMissing = Valid();
        FirstStep(onMissing)["dependsOnSteps"] = new JsonArray(new JsonObject { ["stepOrder"] = 9, ["dependencyType"] = "FINISH_TO_START" });

        Assert.False(Parse(onItself.ToJsonString()).Conforms);
        Assert.False(Parse(onMissing.ToJsonString()).Conforms);
    }

    [Fact]
    public void Step_numbers_with_a_gap_are_refused()
    {
        Assert.False(Parse(ModelDraft(ModelStep(1, "First"), ModelStep(3, "Third"))).Conforms);
    }

    [Fact]
    public void Cycle_conforms_because_reporting_it_is_the_validators_job()
    {
        // BR-09 is a validation failure the specialist can see and repair, not a malformed answer.
        var result = Parse(ModelDraft(ModelStep(1, "Step A", dependsOn: [2]), ModelStep(2, "Step B", dependsOn: [1])));

        Assert.True(result.Conforms, string.Join("; ", result.Problems));
    }

    [Fact]
    public void Quantity_is_rounded_to_the_three_decimals_the_schema_stores()
    {
        var draft = Valid();
        FirstIngredient(draft)["quantity"] = 18.12345;

        Assert.Equal(18.123m, Parse(draft.ToJsonString()).Draft!.Steps[0].Ingredients[0].Quantity);
    }

    [Fact]
    public void Optional_fields_may_be_null_or_absent()
    {
        var draft = Valid();
        draft["notes"] = "Serve within ten minutes.";
        draft["servingSizeMl"] = 350;
        FirstStep(draft)["equipmentClass"] = null;
        FirstStep(draft).Remove("techniqueGate");

        var result = Parse(draft.ToJsonString());

        Assert.True(result.Conforms, string.Join("; ", result.Problems));
        Assert.Equal("Serve within ten minutes.", result.Draft!.Notes);
        Assert.Equal(350, result.Draft.ServingSizeMl);
        Assert.Null(result.Draft.Steps[0].EquipmentClass);
    }

    // ---------------------------------------------------------------- the adapter

    [Fact]
    public async Task Adapter_without_an_api_key_reports_that_it_is_not_configured()
    {
        using var http = new HttpClient();
        var adapter = new OpenAiRecipeDraftModel(http, Options.Create(new LlmOptions()));

        var failure = await Assert.ThrowsAsync<DraftModelException>(() =>
            adapter.CompleteAsync(new DraftModelRequest("system", "user", RecipeDraftSchema.Text), CancellationToken.None));

        Assert.Equal(DraftModelFailure.NotConfigured, failure.Failure);
    }

    [Fact]
    public async Task Adapter_sends_the_schema_as_a_strict_structured_output_and_returns_the_message_content()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"role":"assistant","content":"{\"drinkName\":\"x\"}"}}]}""");
        using var http = new HttpClient(handler);
        var adapter = new OpenAiRecipeDraftModel(http, Options.Create(new LlmOptions
        {
            ApiKey = "test-key", Model = "test-model", BaseUrl = "https://llm.test/v1",
        }));

        var content = await adapter.CompleteAsync(
            new DraftModelRequest("system text", "user text", RecipeDraftSchema.Text), CancellationToken.None);

        Assert.Equal("{\"drinkName\":\"x\"}", content);
        Assert.Equal("https://llm.test/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer test-key", handler.Authorization);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.Body!);
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("BrewForgeRecipeDraft", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.False(format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Adapter_turns_a_slow_endpoint_into_a_timeout()
    {
        using var http = new HttpClient(new CapturingHandler("{}", delay: TimeSpan.FromSeconds(30)));
        var adapter = new OpenAiRecipeDraftModel(http, Options.Create(new LlmOptions
        {
            ApiKey = "test-key", Model = "test-model", TimeoutSeconds = 1,
        }));

        var failure = await Assert.ThrowsAsync<DraftModelException>(() =>
            adapter.CompleteAsync(new DraftModelRequest("s", "u", RecipeDraftSchema.Text), CancellationToken.None));

        Assert.Equal(DraftModelFailure.TimedOut, failure.Failure);
    }

    private static T WithId<T>(T entity, long id) where T : class
    {
        typeof(T).GetProperty("Id")!.SetValue(entity, id);
        return entity;
    }

    private sealed class CapturingHandler(string responseBody, TimeSpan? delay = null) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (delay is { } wait) await Task.Delay(wait, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(responseBody) };
        }
    }
}
