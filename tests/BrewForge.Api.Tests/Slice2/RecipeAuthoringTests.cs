using System.Net;
using System.Net.Http.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>UC-05: the structured recipe model (API contract section 4).</summary>
[Collection(ApiCollection.Name)]
public sealed class RecipeAuthoringTests(BrewForgeApiFactory factory)
{
    [Fact]
    public async Task Recipe_is_created_with_no_released_version()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var code = UniqueCode();

        var created = await (await specialist.PostAsJsonAsync(Recipes,
                new { recipeCode = code, name = "Trà sữa thử nghiệm", category = "TEA", origin = "NEW" }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(code, created.GetProperty("recipeCode").GetString());
        Assert.Equal("Trà sữa thử nghiệm", created.GetProperty("name").GetString());
        Assert.Equal("TEA", created.GetProperty("category").GetString());
        Assert.Equal("NEW", created.GetProperty("origin").GetString());
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("releasedVersionId").ValueKind);

        var duplicate = await specialist.PostAsJsonAsync(Recipes, new { recipeCode = code, name = "Again", category = "TEA" });
        await duplicate.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "UNIQUE");
    }

    [Fact]
    public async Task Category_outside_the_enumeration_is_400()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var response = await specialist.PostAsJsonAsync(Recipes,
            new { recipeCode = UniqueCode(), name = "Smoothie", category = "SMOOTHIE" });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("category", envelope.DetailFields());
    }

    [Fact]
    public async Task New_version_is_a_draft_and_version_numbers_only_go_up()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var recipeId = await factory.NewRecipeAsync();
        var authorId = await factory.UserIdAsync(TestUsers.RdSpecialist);

        var first = await (await specialist.PostAsync($"{Recipes}/{recipeId}/versions", null))
            .ShouldBeAsync(HttpStatusCode.Created);
        var second = await (await specialist.PostAsJsonAsync($"{Recipes}/{recipeId}/versions", new { }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(1, first.GetProperty("versionNo").GetInt32());
        Assert.Equal(2, second.GetProperty("versionNo").GetInt32());
        Assert.Equal("DRAFT", first.GetProperty("state").GetString()); // BR-05
        Assert.False(first.GetProperty("isImmutable").GetBoolean());
        Assert.Equal(authorId, first.GetProperty("createdBy").GetInt64());
        Assert.Empty(first.GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task Version_is_returned_as_the_full_tree_of_the_contract()
    {
        var (recipeId, versionId) = await factory.NewDraftAsync(ValidContent());

        var version = await factory.GetVersionAsync(versionId);

        Assert.Equal(recipeId, version.GetProperty("recipeId").GetInt64());
        Assert.Equal("DRAFT", version.GetProperty("state").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, version.GetProperty("approvedBy").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, version.GetProperty("releasedAt").ValueKind);

        var steps = version.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], steps.Select(s => s.GetProperty("stepOrder").GetInt32()));

        var brew = steps[0];
        Assert.True(brew.GetProperty("id").GetInt64() > 0);
        Assert.Equal("Brew the oolong", brew.GetProperty("actionText").GetString());
        Assert.Equal("TEA_BREWER", brew.GetProperty("equipmentClass").GetString());
        Assert.Equal("Water at 90 C", brew.GetProperty("techniqueGate").GetString());
        Assert.Equal(480, brew.GetProperty("durationSeconds").GetInt32());
        Assert.Empty(brew.GetProperty("dependsOn").EnumerateArray());

        var oolong = brew.GetProperty("ingredients").EnumerateArray()
            .Single(i => i.GetProperty("ingredientCode").GetString() == "ING-OOLONG");
        Assert.True(oolong.GetProperty("ingredientId").GetInt64() > 0);
        Assert.Equal(18m, oolong.GetProperty("quantity").GetDecimal());
        Assert.Equal("g", oolong.GetProperty("unit").GetString());

        // Step 2 depends on step 1, expressed by the id of step 1 as in the contract.
        var dependency = Assert.Single(steps[1].GetProperty("dependsOn").EnumerateArray());
        Assert.Equal(brew.GetProperty("id").GetInt64(), dependency.GetProperty("stepId").GetInt64());
        Assert.Equal("FINISH_TO_START", dependency.GetProperty("type").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, steps[1].GetProperty("equipmentClass").ValueKind);
    }

    [Fact]
    public async Task Saving_again_replaces_the_content_and_keeps_the_step_numbers()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());

        // The same step numbers 1 and 2 are reused by entirely new steps.
        var saved = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}", new
            {
                steps = new[]
                {
                    Step(1, "Steam the milk", "MILK_STEAMER", 40, [Use("ING-MILK", 200m, "ml")]),
                    Step(2, "Serve", seconds: 10, dependsOn: [1]),
                },
            }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(["Steam the milk", "Serve"],
            saved.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("actionText").GetString()));
        Assert.Equal("DRAFT", saved.GetProperty("state").GetString());

        var reloaded = await factory.GetVersionAsync(versionId);
        Assert.Equal(2, reloaded.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Draft_read_from_the_api_can_be_sent_back_unchanged()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());
        var read = await factory.GetVersionAsync(versionId);

        // Ingredients by ingredientId and dependencies by stepId, exactly as they were returned.
        var saved = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
                new { steps = read.GetProperty("steps") }))
            .ShouldBeAsync(HttpStatusCode.OK);

        var steps = saved.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal(steps[0].GetProperty("id").GetInt64(),
            steps[1].GetProperty("dependsOn")[0].GetProperty("stepId").GetInt64());
        Assert.Equal(2, steps[0].GetProperty("ingredients").GetArrayLength());
    }

    [Fact]
    public async Task New_version_can_start_as_a_copy_of_an_earlier_one()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (recipeId, versionId) = await factory.NewDraftAsync(ValidContent());

        var copy = await (await specialist.PostAsJsonAsync($"{Recipes}/{recipeId}/versions",
                new { copyFromVersionId = versionId }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(2, copy.GetProperty("versionNo").GetInt32());
        Assert.Equal("DRAFT", copy.GetProperty("state").GetString());
        Assert.Equal(3, copy.GetProperty("steps").GetArrayLength());
        Assert.NotEqual(versionId, copy.GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("PLASMA_BREWER", "ING-OOLONG", "steps[0].equipmentClass")]
    [InlineData("TEA_BREWER", "ING-UNOBTAINIUM", "steps[0].ingredients[0].ingredientId")]
    public async Task Reference_to_master_data_that_does_not_exist_is_400(string equipment, string ingredient,
        string field)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync();

        var response = await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
            new { steps = new[] { Step(1, "Brew", equipment, 60, [Use(ingredient, 18m, "g")]) } });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains(field, envelope.DetailFields());
    }

    [Fact]
    public async Task Step_depending_on_itself_is_409_BR_09()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync();

        var response = await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
            new { steps = new[] { Step(1, "Brew", dependsOn: [1]) } });

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E07", "BR-09");
    }

    [Fact]
    public async Task Step_numbers_with_a_gap_are_400()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync();

        var response = await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
            new { steps = new[] { Step(1, "First"), Step(3, "Third") } });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("steps", envelope.DetailFields());
    }

    [Fact]
    public async Task Recipe_list_filters_by_category_and_shows_the_version_count()
    {
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var (recipeId, _) = await factory.NewDraftAsync(ValidContent());

        var page = await (await auditor.GetAsync($"{Recipes}?category=COFFEE&size=100")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.NotEmpty(page.GetProperty("items").EnumerateArray());
        Assert.All(page.GetProperty("items").EnumerateArray(),
            recipe => Assert.Equal("COFFEE", recipe.GetProperty("category").GetString()));

        var recipe = await (await auditor.GetAsync($"{Recipes}/{recipeId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, recipe.GetProperty("versionCount").GetInt32());

        var history = await (await auditor.GetAsync($"{Recipes}/{recipeId}/versions")).ShouldBeAsync(HttpStatusCode.OK);
        var only = Assert.Single(history.EnumerateArray());
        Assert.Equal("DRAFT", only.GetProperty("state").GetString());
        Assert.Equal(3, only.GetProperty("stepCount").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, only.GetProperty("lastValidationPassed").ValueKind);
    }

    [Fact]
    public async Task Recipe_and_its_versions_cannot_be_deleted()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (recipeId, versionId) = await factory.NewDraftAsync(ValidContent());

        await (await specialist.DeleteAsync($"{Recipes}/{recipeId}")).ShouldBeErrorAsync(HttpStatusCode.MethodNotAllowed);
        await (await specialist.DeleteAsync($"{Versions}/{versionId}")).ShouldBeErrorAsync(HttpStatusCode.MethodNotAllowed);
        await factory.GetVersionAsync(versionId);
    }
}
