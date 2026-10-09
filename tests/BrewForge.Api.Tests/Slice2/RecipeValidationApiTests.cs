using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Recipes;
using BrewForge.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>UC-07: the three checks, run through the API and stored for audit.</summary>
[Collection(ApiCollection.Name)]
public sealed class RecipeValidationApiTests(BrewForgeApiFactory factory)
{
    private async Task<JsonElement> ValidateAsync(long versionId, string user = TestUsers.RdSpecialist)
    {
        using var client = await factory.ClientForAsync(user);
        return await (await client.PostAsync($"{Versions}/{versionId}/validate", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sound_recipe_passes_all_three_checks()
    {
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());

        var result = await ValidateAsync(versionId);

        Assert.Equal(versionId, result.GetProperty("versionId").GetInt64());
        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.Equal(["EQUIPMENT", "ORDERING", "INGREDIENT"],
            result.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("checkType").GetString()));
        Assert.All(result.GetProperty("checks").EnumerateArray(), check =>
        {
            Assert.True(check.GetProperty("passed").GetBoolean());
            Assert.Empty(check.GetProperty("violations").EnumerateArray());
        });
    }

    [Fact]
    public async Task Failed_check_answers_200_with_the_violation_attached_to_its_step()
    {
        var (_, versionId) = await factory.NewDraftAsync(OverdosedContent());
        var brewStepId = (await factory.GetVersionAsync(versionId)).GetProperty("steps")[0].GetProperty("id").GetInt64();

        var result = await ValidateAsync(versionId, TestUsers.RdManager);

        // BR-08: two checks passed, one failed, so the candidate failed.
        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.True(result.Check("ORDERING").GetProperty("passed").GetBoolean());
        Assert.True(result.Check("INGREDIENT").GetProperty("passed").GetBoolean());

        var equipment = result.Check("EQUIPMENT");
        Assert.False(equipment.GetProperty("passed").GetBoolean());
        var violation = Assert.Single(equipment.GetProperty("violations").EnumerateArray());
        Assert.Equal(brewStepId, violation.GetProperty("stepId").GetInt64());
        Assert.Equal(1, violation.GetProperty("stepOrder").GetInt32());
        Assert.Equal("BR-10", violation.GetProperty("rule").GetString());
        Assert.Equal("MSG-E10", violation.GetProperty("code").GetString());
        Assert.Equal("40.0 g is outside the 15.0-25.0 g range of TEA_BREWER", violation.GetProperty("message").GetString());
        Assert.Equal("15.0 - 25.0", violation.GetProperty("expected").GetString());
        Assert.Equal("40.0", violation.GetProperty("actual").GetString());
    }

    [Fact]
    public async Task Cycle_is_reported_on_every_step_of_it()
    {
        var (_, versionId) = await factory.NewDraftAsync(new
        {
            steps = new[]
            {
                Step(1, "A", dependsOn: [3]),
                Step(2, "B", dependsOn: [1]),
                Step(3, "C", dependsOn: [2]),
            },
        });

        var ordering = (await ValidateAsync(versionId)).Check("ORDERING");

        Assert.False(ordering.GetProperty("passed").GetBoolean());
        var violations = ordering.GetProperty("violations").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], violations.Select(v => v.GetProperty("stepOrder").GetInt32()));
        Assert.All(violations, v =>
        {
            Assert.Equal("BR-09", v.GetProperty("rule").GetString());
            Assert.Equal("MSG-E07", v.GetProperty("code").GetString());
            Assert.Contains("steps 1, 2, 3", v.GetProperty("message").GetString());
        });
    }

    [Fact]
    public async Task Every_run_is_stored_in_validation_result_with_the_offending_step()
    {
        var (_, versionId) = await factory.NewDraftAsync(OverdosedContent());
        var brewStepId = (await factory.GetVersionAsync(versionId)).GetProperty("steps")[0].GetProperty("id").GetInt64();

        await ValidateAsync(versionId);

        var rows = await factory.WithDbAsync(db =>
            db.ValidationResults.Where(r => r.RecipeVersionId == versionId).ToListAsync());

        // One row per passed check, one row per violation of a failed check.
        Assert.Equal(3, rows.Count);
        Assert.Single(rows.Select(r => r.RunAt).Distinct());
        Assert.Contains(rows, r => r is { CheckType: CheckType.Ordering, Passed: true, StepId: null, ViolationDetail: null });
        Assert.Contains(rows, r => r is { CheckType: CheckType.Ingredient, Passed: true });

        var failed = rows.Single(r => r.CheckType == CheckType.Equipment);
        Assert.False(failed.Passed);
        Assert.Equal(brewStepId, failed.StepId);
        var detail = JsonSerializer.Deserialize<JsonElement>(failed.ViolationDetail!);
        Assert.Equal("BR-10", detail.GetProperty("rule").GetString());
        Assert.Equal("15.0 - 25.0", detail.GetProperty("expected").GetString());
        Assert.Equal("40.0", detail.GetProperty("actual").GetString());
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Validation_reads_the_catalogue_as_it_stands_at_that_moment()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = UniqueCode("ING-");
        var ingredient = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = code, name = "Seasonal syrup", unit = "ml", shelfLifeHours = 24 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var (_, versionId) = await factory.NewDraftAsync(new
        {
            steps = new[] { Step(1, "Add the seasonal syrup", seconds: 10, uses: [Use(code, 20m, "ml")]) },
        });
        Assert.True((await ValidateAsync(versionId)).GetProperty("passed").GetBoolean());

        // The ingredient is withdrawn from the catalogue; the draft has not changed.
        await (await admin.PostAsync($"/api/v1/ingredients/{ingredient.GetProperty("id").GetInt64()}/deactivate", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        var after = await ValidateAsync(versionId);
        Assert.False(after.GetProperty("passed").GetBoolean());
        var violation = Assert.Single(after.Check("INGREDIENT").GetProperty("violations").EnumerateArray());
        Assert.Equal("BR-11", violation.GetProperty("rule").GetString());
        Assert.Contains(code, violation.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------- submit

    [Fact]
    public async Task Submit_moves_a_passing_draft_to_validated()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (recipeId, versionId) = await factory.NewDraftAsync(ValidContent());

        var result = await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.Equal("VALIDATED", (await factory.GetVersionAsync(versionId)).GetProperty("state").GetString());

        var history = await (await specialist.GetAsync($"{Recipes}/{recipeId}/versions")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(history[0].GetProperty("lastValidationPassed").GetBoolean());
    }

    [Fact]
    public async Task Submit_of_a_partial_pass_is_422_and_the_draft_stays_a_draft()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync(OverdosedContent());

        var response = await specialist.PostAsync($"{Versions}/{versionId}/submit", null);

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "MSG-E03", "BR-08");
        Assert.Contains("steps[1]", envelope.DetailFields());
        Assert.Equal("DRAFT", (await factory.GetVersionAsync(versionId)).GetProperty("state").GetString());
        // The refused submission still left its violations on record.
        Assert.True(await factory.WithDbAsync(db => db.ValidationResults.AnyAsync(r => r.RecipeVersionId == versionId && !r.Passed)));
    }

    [Fact]
    public async Task Validated_version_cannot_be_edited_by_the_author()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);

        var response = await specialist.PutAsJsonAsync($"{Versions}/{versionId}", OverdosedContent());

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        Assert.Equal(3, (await factory.GetVersionAsync(versionId)).GetProperty("steps").GetArrayLength());
    }

    // ---------------------------------------------------------------- seed

    [Fact]
    public async Task Every_seeded_reference_recipe_passes_validation()
    {
        var versions = await factory.WithDbAsync(db => db.RecipeVersions
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, v.VersionNo, r.RecipeCode })
            .Where(x => x.VersionNo == 1)
            .ToListAsync());
        var seeded = versions.Where(v => SeedRecipes.All.Any(s => s.Code == v.RecipeCode && s.Code != SeedRecipes.BrokenDemoCode))
            .ToList();

        Assert.Equal(10, seeded.Count);
        foreach (var version in seeded)
        {
            var result = await ValidateAsync(version.Id);
            Assert.True(result.GetProperty("passed").GetBoolean(), $"{version.RecipeCode}: {result}");
        }
    }

    [Fact]
    public async Task Seeded_demonstration_draft_fails_all_three_checks()
    {
        var versionId = await factory.WithDbAsync(db => db.RecipeVersions
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, r.RecipeCode })
            .Where(x => x.RecipeCode == SeedRecipes.BrokenDemoCode)
            .Select(x => x.Id).SingleAsync());

        var result = await ValidateAsync(versionId);

        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.All(result.GetProperty("checks").EnumerateArray(),
            check => Assert.False(check.GetProperty("passed").GetBoolean()));
    }
}
