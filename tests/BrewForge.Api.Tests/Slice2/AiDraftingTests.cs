using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Recipes.Drafting;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>
/// UC-06 and UC-08 with a scripted language model: BR-05 (an AI draft is
/// only a draft), BR-06 (every call is logged), BR-07 (a non-conforming
/// answer is retried once, then rejected unseen) and the limit of three
/// automatic repairs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AiDraftingTests : IDisposable
{
    private const string NotJson = "Sure! Here is a lovely recipe: first you brew the tea...";

    private readonly BrewForgeApiFactory _factory;

    public AiDraftingTests(BrewForgeApiFactory factory)
    {
        _factory = factory;
        _factory.DraftModel.Reset();
    }

    public void Dispose() => _factory.DraftModel.Reset();

    private async Task<HttpResponseMessage> GenerateAsync(long versionId, string description = "A creamy oolong milk tea")
    {
        using var specialist = await _factory.ClientForAsync(TestUsers.RdSpecialist);
        return await specialist.PostAsJsonAsync($"{Versions}/{versionId}/generate-draft", new { description });
    }

    private async Task<HttpResponseMessage> RepairAsync(long versionId, object body)
    {
        using var specialist = await _factory.ClientForAsync(TestUsers.RdSpecialist);
        return await specialist.PostAsJsonAsync($"{Versions}/{versionId}/repair", body);
    }

    private Task<List<Domain.Recipes.AiDraftLog>> LogsAsync(long recipeId) =>
        _factory.WithDbAsync(db => db.AiDraftLogs.Where(l => l.RecipeId == recipeId).OrderBy(l => l.Id).ToListAsync());

    // ---------------------------------------------------------------- UC-06

    [Fact]
    public async Task Conforming_answer_becomes_the_content_of_the_draft_and_is_validated_at_once()
    {
        var (_, versionId) = await _factory.NewDraftAsync();
        _factory.DraftModel.Answer(ValidModelDraft());

        var result = await (await GenerateAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        var version = result.GetProperty("version");
        Assert.Equal("DRAFT", version.GetProperty("state").GetString()); // BR-05: never more than a draft
        Assert.False(version.GetProperty("isImmutable").GetBoolean());
        Assert.Equal(["Brew the oolong", "Add the milk base"],
            version.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("actionText").GetString()));
        Assert.Equal("ING-OOLONG",
            version.GetProperty("steps")[0].GetProperty("ingredients")[0].GetProperty("ingredientCode").GetString());

        // UC-06 step 6: the draft comes back with its validation result.
        Assert.True(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal(3, result.GetProperty("validation").GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Ai_draft_that_fails_validation_is_still_only_a_draft()
    {
        var (_, versionId) = await _factory.NewDraftAsync();
        _factory.DraftModel.Answer(ModelDraft(ModelStep(1, "Brew far too much", "TEA_BREWER", 480, [("ING-OOLONG", 40m, "g")])));

        var result = await (await GenerateAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.False(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal("DRAFT", result.GetProperty("version").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Every_call_is_logged_with_prompt_model_and_raw_response()
    {
        var (recipeId, versionId) = await _factory.NewDraftAsync();
        var answer = ValidModelDraft();
        _factory.DraftModel.Answer(answer);

        await (await GenerateAsync(versionId, "A creamy oolong milk tea with tapioca")).ShouldBeAsync(HttpStatusCode.OK);

        var log = Assert.Single(await LogsAsync(recipeId));
        Assert.Equal("fake-draft-model", log.ModelName);
        Assert.True(log.SchemaValid);
        Assert.Contains("A creamy oolong milk tea with tapioca", log.PromptText);
        Assert.Contains("ING-OOLONG", log.PromptText);       // the catalogue went into the prompt
        Assert.Contains("TEA_BREWER | 15 | 25 | g", log.PromptText);
        Assert.Equal(JsonSerializer.Deserialize<JsonElement>(answer).GetProperty("steps").GetArrayLength(),
            JsonSerializer.Deserialize<JsonElement>(log.RawResponse!).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Model_is_called_with_the_fixed_schema_and_only_the_active_catalogue()
    {
        using var admin = await _factory.ClientForAsync(TestUsers.Admin);
        var retired = UniqueCode("ING-");
        var created = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = retired, name = "Withdrawn syrup", unit = "ml", shelfLifeHours = 24 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await admin.PostAsync($"/api/v1/ingredients/{created.GetProperty("id").GetInt64()}/deactivate", null))
            .ShouldBeAsync(HttpStatusCode.OK);
        var (_, versionId) = await _factory.NewDraftAsync();
        _factory.DraftModel.Answer(ValidModelDraft());

        await (await GenerateAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        var call = Assert.Single(_factory.DraftModel.Calls);
        Assert.Equal(RecipeDraftSchema.Text, call.JsonSchema);
        Assert.Contains("\"additionalProperties\": false", call.JsonSchema);
        Assert.Contains("ING-MILKBASE", call.UserPrompt);
        Assert.DoesNotContain(retired, call.UserPrompt);
    }

    [Fact]
    public async Task Malformed_answer_twice_is_rejected_unseen_and_both_attempts_are_logged()
    {
        var (recipeId, versionId) = await _factory.NewDraftAsync(ValidContent());
        _factory.DraftModel.Answer(NotJson).Answer("{ \"drinkName\": \"x\", \"steps\": \"free text\" }");

        var response = await GenerateAsync(versionId);

        // BR-07: retried once, then rejected with MSG-E04...
        var body = await response.Content.ReadAsStringAsync();
        await response.ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "MSG-E04", "BR-07");
        Assert.Equal(2, _factory.DraftModel.Calls.Count);
        // ...without the answer being shown to the user...
        Assert.DoesNotContain("lovely recipe", body);
        Assert.DoesNotContain("free text", body);

        // ...and BR-06: both attempts are in ai_draft_log, marked as not conforming.
        var logs = await LogsAsync(recipeId);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.False(log.SchemaValid));
        Assert.Equal(NotJson, JsonSerializer.Deserialize<string>(logs[0].RawResponse!));
        Assert.Contains("free text", logs[1].RawResponse);

        // The draft the specialist already had is untouched.
        var version = await _factory.GetVersionAsync(versionId);
        Assert.Equal(3, version.GetProperty("steps").GetArrayLength());
        Assert.Equal("DRAFT", version.GetProperty("state").GetString());
    }

    [Fact]
    public async Task One_malformed_answer_is_retried_and_the_second_answer_is_used()
    {
        var (recipeId, versionId) = await _factory.NewDraftAsync();
        _factory.DraftModel.Answer(NotJson).Answer(ValidModelDraft());

        var result = await (await GenerateAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(2, result.GetProperty("version").GetProperty("steps").GetArrayLength());
        Assert.Equal([false, true], (await LogsAsync(recipeId)).Select(log => log.SchemaValid));
    }

    [Fact]
    public async Task Answer_that_invents_an_ingredient_code_does_not_conform()
    {
        var (recipeId, versionId) = await _factory.NewDraftAsync();
        var invented = ModelDraft(ModelStep(1, "Add unicorn tears", uses: [("ING-UNICORN", 5m, "ml")]));
        _factory.DraftModel.Answer(invented).Answer(invented);

        await (await GenerateAsync(versionId)).ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "MSG-E04", "BR-07");

        Assert.All(await LogsAsync(recipeId), log => Assert.False(log.SchemaValid));
        Assert.Empty((await _factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task Timeout_is_MSG_E05_is_not_retried_and_is_logged_without_a_response()
    {
        var (recipeId, versionId) = await _factory.NewDraftAsync();
        _factory.DraftModel.Fail(DraftModelFailure.TimedOut);

        await (await GenerateAsync(versionId)).ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "MSG-E05");

        Assert.Single(_factory.DraftModel.Calls);
        var log = Assert.Single(await LogsAsync(recipeId));
        Assert.Null(log.RawResponse);
        Assert.False(log.SchemaValid);
    }

    [Fact]
    public async Task Description_is_required_and_the_model_is_not_called_without_it()
    {
        var (_, versionId) = await _factory.NewDraftAsync();

        var envelope = await (await GenerateAsync(versionId, "  ")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("description", envelope.DetailFields());
        Assert.Empty(_factory.DraftModel.Calls);
    }

    // ---------------------------------------------------------------- UC-08

    [Fact]
    public async Task Ai_repair_sends_the_violations_back_and_revalidates_the_answer()
    {
        var (_, versionId) = await _factory.NewDraftAsync(OverdosedContent());
        _factory.DraftModel.Answer(ValidModelDraft());

        var result = await (await RepairAsync(versionId, new { mode = "AI" })).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal(1, result.GetProperty("aiRepairsUsed").GetInt32());
        Assert.Equal(2, result.GetProperty("aiRepairsLeft").GetInt32());
        Assert.Equal("DRAFT", result.GetProperty("version").GetProperty("state").GetString());

        var prompt = Assert.Single(_factory.DraftModel.Calls).UserPrompt;
        Assert.Contains("40.0 g is outside the 15.0-25.0 g range of TEA_BREWER", prompt); // the violation
        Assert.Contains("Brew far too much oolong", prompt);                              // the current draft
    }

    [Fact]
    public async Task Fourth_ai_repair_is_409_and_the_draft_stays_a_draft()
    {
        var (_, versionId) = await _factory.NewDraftAsync(OverdosedContent());
        var stillWrong = ModelDraft(ModelStep(1, "Still far too much", "TEA_BREWER", 480, [("ING-OOLONG", 41m, "g")]));
        _factory.DraftModel.Answer(stillWrong).Answer(stillWrong).Answer(stillWrong);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await (await RepairAsync(versionId, new { mode = "AI" })).ShouldBeAsync(HttpStatusCode.OK);
            Assert.Equal(attempt, result.GetProperty("aiRepairsUsed").GetInt32());
            Assert.Equal(3 - attempt, result.GetProperty("aiRepairsLeft").GetInt32());
        }

        var fourth = await RepairAsync(versionId, new { mode = "AI" });

        await fourth.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "AI_REPAIR_LIMIT");
        Assert.Equal(3, _factory.DraftModel.Calls.Count); // the fourth never reached the model
        var version = await _factory.GetVersionAsync(versionId);
        Assert.Equal("DRAFT", version.GetProperty("state").GetString());
        Assert.Equal("Still far too much", version.GetProperty("steps")[0].GetProperty("actionText").GetString());
    }

    [Fact]
    public async Task Manual_repair_applies_the_patch_revalidates_and_is_not_counted()
    {
        var (_, versionId) = await _factory.NewDraftAsync(OverdosedContent());

        var result = await (await RepairAsync(versionId, new { mode = "MANUAL", patch = ValidContent() }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal(0, result.GetProperty("aiRepairsUsed").GetInt32());
        Assert.Equal(3, result.GetProperty("version").GetProperty("steps").GetArrayLength());
        Assert.Empty(_factory.DraftModel.Calls);
    }

    [Fact]
    public async Task Repair_of_a_draft_that_already_passes_spends_no_attempt()
    {
        var (_, versionId) = await _factory.NewDraftAsync(ValidContent());

        var result = await (await RepairAsync(versionId, new { mode = "AI" })).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal(0, result.GetProperty("aiRepairsUsed").GetInt32());
        Assert.Empty(_factory.DraftModel.Calls);
    }

    [Fact]
    public async Task Malformed_repair_answer_does_not_count_as_a_repair()
    {
        var (_, versionId) = await _factory.NewDraftAsync(OverdosedContent());
        _factory.DraftModel.Answer(NotJson).Answer(NotJson);

        await (await RepairAsync(versionId, new { mode = "AI" }))
            .ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "MSG-E04");

        _factory.DraftModel.Answer(ValidModelDraft());
        var result = await (await RepairAsync(versionId, new { mode = "AI" })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, result.GetProperty("aiRepairsUsed").GetInt32());
    }

    [Theory]
    [InlineData("{}", "mode")]
    [InlineData("{\"mode\":\"MANUAL\"}", "patch")]
    [InlineData("{\"mode\":\"MAGIC\"}", "mode")]
    public async Task Repair_request_must_say_how(string json, string field)
    {
        var (_, versionId) = await _factory.NewDraftAsync(OverdosedContent());

        var envelope = await (await RepairAsync(versionId, JsonSerializer.Deserialize<JsonElement>(json)))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains(field, envelope.DetailFields());
    }
}
