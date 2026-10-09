using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrewForge.Api.Errors;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using BrewForge.Infrastructure.Persistence;
using BrewForge.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice3;

/// <summary>UC-09, UC-10 and the rollback: BR-01 to BR-04, BR-12 and the audit trail.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReleaseTests(BrewForgeApiFactory factory)
{
    private const string OtherManager = "rdmanager2";

    private async Task<long> ValidatedVersionAsync(long? recipeId = null, object? content = null)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        long versionId;
        if (recipeId is null)
        {
            (_, versionId) = await factory.NewDraftAsync(content ?? ValidContent());
        }
        else
        {
            var created = await (await specialist.PostAsJsonAsync($"{Recipes}/{recipeId}/versions", new { }))
                .ShouldBeAsync(HttpStatusCode.Created);
            versionId = created.GetProperty("id").GetInt64();
            await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}", content ?? ValidContent()))
                .ShouldBeAsync(HttpStatusCode.OK);
        }
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        return versionId;
    }

    private async Task<HttpResponseMessage> ReleaseAsync(long versionId, string user = TestUsers.RdManager,
        string? idempotencyKey = null)
    {
        using var manager = await factory.ClientForAsync(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Versions}/{versionId}/release");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await manager.SendAsync(request);
    }

    private async Task<long> ReleasedVersionAsync(long? recipeId = null, object? content = null)
    {
        var versionId = await ValidatedVersionAsync(recipeId, content);
        await (await ReleaseAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);
        return versionId;
    }

    private async Task<long> RecipeOfAsync(long versionId) =>
        (await factory.GetVersionAsync(versionId)).GetProperty("recipeId").GetInt64();

    private Task<List<string>> AuditActionsAsync(long versionId) =>
        factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "RecipeVersion" && a.EntityId == versionId)
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());

    // ---------------------------------------------------------------- UC-10

    [Fact]
    public async Task Release_seals_the_version_and_makes_it_the_released_version_of_the_recipe()
    {
        var versionId = await ValidatedVersionAsync();
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);
        var authorId = await factory.UserIdAsync(TestUsers.RdSpecialist);

        var result = await (await ReleaseAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        var version = result.GetProperty("version");
        Assert.Equal("RELEASED", version.GetProperty("state").GetString());
        Assert.True(version.GetProperty("isImmutable").GetBoolean());
        Assert.Equal(managerId, version.GetProperty("approvedBy").GetInt64());
        Assert.Equal(authorId, version.GetProperty("createdBy").GetInt64());
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), version.GetProperty("contentHash").GetString());
        Assert.True(version.GetProperty("releasedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("supersededVersionId").ValueKind);

        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var recipe = await (await auditor.GetAsync($"{Recipes}/{await RecipeOfAsync(versionId)}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(versionId, recipe.GetProperty("releasedVersionId").GetInt64());
    }

    [Fact]
    public async Task Draft_cannot_be_released_without_passing_through_validation()
    {
        // BR-05: no path from DRAFT to RELEASED.
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());

        await (await ReleaseAsync(versionId)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        Assert.Equal("DRAFT", (await factory.GetVersionAsync(versionId)).GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- BR-01

    public static TheoryData<string, string, string, string?> ChangesToAReleasedVersion => new()
    {
        { "PUT", "", TestUsers.RdSpecialist, JsonSerializer.Serialize(new { steps = new[] { Step(1, "Tampered") } }) },
        { "POST", "/submit", TestUsers.RdSpecialist, null },
        { "POST", "/generate-draft", TestUsers.RdSpecialist, "{\"description\":\"something else entirely\"}" },
        { "POST", "/repair", TestUsers.RdSpecialist, "{\"mode\":\"AI\"}" },
        { "POST", "/repair", TestUsers.RdSpecialist, JsonSerializer.Serialize(new { mode = "MANUAL", patch = new { steps = new[] { Step(1, "Tampered") } } }) },
        { "POST", "/review", TestUsers.RdManager, "{\"decisions\":[]}" },
        { "POST", "/release", OtherManager, null },
    };

    [Theory]
    [MemberData(nameof(ChangesToAReleasedVersion))]
    public async Task BR_01_every_attempt_to_change_a_released_version_is_a_clean_409(string method, string path,
        string user, string? body)
    {
        factory.DraftModel.Reset();
        var versionId = await ReleasedVersionAsync();
        var before = await factory.GetVersionAsync(versionId);
        using var client = await factory.ClientForAsync(user);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"{Versions}/{versionId}{path}");
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E06", "BR-01");
        var after = await factory.GetVersionAsync(versionId);
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Empty(factory.DraftModel.Calls); // the model was never asked to redraft a released recipe
    }

    [Theory]
    [InlineData("UPDATE recipe_version SET content_hash = repeat('0', 64) WHERE id = {0}")]
    [InlineData("UPDATE recipe_version SET approved_by = NULL WHERE id = {0}")]
    [InlineData("UPDATE recipe_step SET action_text = 'Tampered' WHERE recipe_version_id = {0}")]
    [InlineData("DELETE FROM recipe_step WHERE recipe_version_id = {0}")]
    [InlineData("INSERT INTO recipe_step (recipe_version_id, step_order, action_text) VALUES ({0}, 99, 'Smuggled in')")]
    public async Task BR_01_the_database_rejects_a_change_that_bypasses_the_application(string sql)
    {
        var versionId = await ReleasedVersionAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, versionId)));

        Assert.Contains("BR-01", exception.MessageText);
        // ...and such a rejection reaches a client as 409 BR-01, never as a 500.
        var translated = PostgresErrorTranslator.Translate(exception);
        Assert.Equal("BR-01", translated!.Rule);
        Assert.Equal(ErrorKind.RuleViolation, translated.Kind);
    }

    [Fact]
    public async Task BR_01_code_that_goes_around_the_aggregate_is_stopped_before_the_database()
    {
        var versionId = await ReleasedVersionAsync();

        await factory.WithDbAsync(async db =>
        {
            var version = await db.RecipeVersions.Include(v => v.Steps).SingleAsync(v => v.Id == versionId);

            db.Entry(version.Steps[0]).Property(nameof(RecipeStep.ActionText)).CurrentValue = "Tampered";
            var onStep = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("BR-01", onStep.Rule);
            Assert.Equal("MSG-E06", onStep.Code);

            db.ChangeTracker.Clear();
            version = await db.RecipeVersions.SingleAsync(v => v.Id == versionId);
            db.Entry(version).Property(nameof(RecipeVersion.ContentHash)).CurrentValue = new string('0', 64);
            var onVersion = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("BR-01", onVersion.Rule);

            db.ChangeTracker.Clear();
            db.Remove(await db.RecipeVersions.SingleAsync(v => v.Id == versionId));
            var onDelete = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("BR-01", onDelete.Rule);
        });
    }

    // ---------------------------------------------------------------- BR-02

    [Fact]
    public async Task BR_02_releasing_a_second_version_supersedes_the_first()
    {
        var first = await ReleasedVersionAsync();
        var recipeId = await RecipeOfAsync(first);
        var firstHash = (await factory.GetVersionAsync(first)).GetProperty("contentHash").GetString();
        var second = await ValidatedVersionAsync(recipeId, OverdosedFixed());

        var result = await (await ReleaseAsync(second)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(first, result.GetProperty("supersededVersionId").GetInt64());
        var old = await factory.GetVersionAsync(first);
        Assert.Equal("SUPERSEDED", old.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, old.GetProperty("supersededAt").ValueKind);
        Assert.True(old.GetProperty("isImmutable").GetBoolean());
        Assert.Equal(firstHash, old.GetProperty("contentHash").GetString()); // retained untouched

        var states = await factory.WithDbAsync(db =>
            db.RecipeVersions.Where(v => v.RecipeId == recipeId).Select(v => v.State).ToListAsync());
        Assert.Single(states, state => state == VersionState.Released);
        Assert.Contains("SUPERSEDE", await AuditActionsAsync(first));
    }

    [Fact]
    public async Task BR_02_a_second_released_version_of_one_recipe_is_409()
    {
        var first = await ReleasedVersionAsync();
        var second = await ValidatedVersionAsync(await RecipeOfAsync(first));

        // What two releases racing each other would attempt: a second RELEASED row.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE recipe_version SET state = 'RELEASED' WHERE id = {0}", second)));
        Assert.Equal("ux_recipe_one_released", exception.ConstraintName);

        // The API answers that rejection with 409 naming BR-02.
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await new ErrorEnvelopeMiddleware(_ => throw exception, NullLogger<ErrorEnvelopeMiddleware>.Instance)
            .InvokeAsync(context);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var envelope = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        Assert.Equal("BR-02", envelope.GetProperty("rule").GetString());
    }

    // ---------------------------------------------------------------- BR-03

    [Fact]
    public async Task BR_03_version_numbers_only_go_up_even_when_drafts_are_released_out_of_order()
    {
        var first = await ReleasedVersionAsync();                       // 1
        var recipeId = await RecipeOfAsync(first);
        var second = await ValidatedVersionAsync(recipeId);             // 2, waits
        var third = await ValidatedVersionAsync(recipeId);              // 3

        var thirdReleased = await (await ReleaseAsync(third)).ShouldBeAsync(HttpStatusCode.OK);
        var secondReleased = await (await ReleaseAsync(second)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(3, thirdReleased.GetProperty("version").GetProperty("versionNo").GetInt32());
        // Released after 3, so it cannot be 2: it takes the next free number.
        Assert.Equal(4, secondReleased.GetProperty("version").GetProperty("versionNo").GetInt32());

        var numbers = await factory.WithDbAsync(db => db.RecipeVersions.Where(v => v.RecipeId == recipeId)
            .OrderBy(v => v.ReleasedAt).Select(v => v.VersionNo).ToListAsync());
        Assert.Equal([1, 3, 4], numbers);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    // ---------------------------------------------------------------- BR-04

    [Fact]
    public async Task BR_04_rollback_creates_a_new_version_and_leaves_the_old_one_as_it_was()
    {
        var first = await ReleasedVersionAsync();
        var recipeId = await RecipeOfAsync(first);
        await ReleasedVersionAsync(recipeId, OverdosedFixed());                 // version 2 replaces it
        var firstBefore = await factory.GetVersionAsync(first);
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        var rolledBack = await (await manager.PostAsync($"{Versions}/{first}/rollback", null))
            .ShouldBeAsync(HttpStatusCode.Created);

        // A new version with a new number...
        Assert.NotEqual(first, rolledBack.GetProperty("id").GetInt64());
        Assert.Equal(3, rolledBack.GetProperty("versionNo").GetInt32());
        Assert.Equal(managerId, rolledBack.GetProperty("createdBy").GetInt64());
        Assert.False(rolledBack.GetProperty("isImmutable").GetBoolean());
        Assert.Equal("VALIDATED", rolledBack.GetProperty("state").GetString());
        // ...whose content is a copy of the earlier one...
        Assert.Equal(
            firstBefore.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("actionText").GetString()),
            rolledBack.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("actionText").GetString()));
        // ...while the old version still exists, exactly as it was.
        Assert.Equal(firstBefore.GetRawText(), (await factory.GetVersionAsync(first)).GetRawText());
        Assert.Equal("SUPERSEDED", firstBefore.GetProperty("state").GetString());

        // The manager who rolled back is its author, so someone else releases it (BR-12).
        var newId = rolledBack.GetProperty("id").GetInt64();
        await (await ReleaseAsync(newId)).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E09", "BR-12");
        var released = await (await ReleaseAsync(newId, OtherManager)).ShouldBeAsync(HttpStatusCode.OK);
        // Same procedure, same hash, different version.
        Assert.Equal(firstBefore.GetProperty("contentHash").GetString(),
            released.GetProperty("version").GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task BR_04_only_a_version_that_was_released_can_be_rolled_back_to()
    {
        var (_, draft) = await factory.NewDraftAsync(ValidContent());
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        await (await manager.PostAsync($"{Versions}/{draft}/rollback", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-04");
    }

    // ---------------------------------------------------------------- BR-12

    [Fact]
    public async Task BR_12_the_manager_who_edited_the_draft_cannot_release_it()
    {
        var versionId = await ValidatedVersionAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("id").GetInt64()).ToList();

        // The manager edits one step in review, so the version is theirs now...
        await (await manager.PostAsJsonAsync($"{Versions}/{versionId}/review", new
        {
            decisions = steps.Select((id, i) => i == 0
                ? new { stepId = id, action = "EDIT", editedText = (string?)"Brew the oolong for eight minutes" }
                : new { stepId = id, action = "ACCEPT", editedText = (string?)null }),
        })).ShouldBeAsync(HttpStatusCode.OK);
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);

        // ...and they cannot be the one who releases it.
        var envelope = await (await ReleaseAsync(versionId))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E09", "BR-12");
        Assert.Contains("approvedBy", envelope.DetailFields());
        Assert.Equal("VALIDATED", (await factory.GetVersionAsync(versionId)).GetProperty("state").GetString());

        await (await ReleaseAsync(versionId, OtherManager)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BR_12_the_database_also_refuses_an_approver_who_is_the_author()
    {
        var versionId = await ValidatedVersionAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE recipe_version SET approved_by = created_by WHERE id = {0}", versionId)));

        Assert.Equal("BR-12", PostgresErrorTranslator.Translate(exception)!.Rule);
    }

    // ---------------------------------------------------------------- MSG-E08

    [Fact]
    public async Task Release_revalidates_and_refuses_when_master_data_changed_since_approval()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var code = UniqueCode("ING-");
        var ingredient = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = code, name = "Seasonal syrup", unit = "ml", shelfLifeHours = 24 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var versionId = await ValidatedVersionAsync(content: new
        {
            steps = new[] { Step(1, "Add the seasonal syrup", seconds: 10, uses: [Use(code, 20m, "ml")]) },
        });

        // Between approval and release, the catalogue changes.
        await (await admin.PostAsync($"/api/v1/ingredients/{ingredient.GetProperty("id").GetInt64()}/deactivate", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        var envelope = await (await ReleaseAsync(versionId))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E08", "BR-08");
        Assert.Contains("steps[1]", envelope.DetailFields());

        // Not released; handed back for repair, with the failed run on record.
        var version = await factory.GetVersionAsync(versionId);
        Assert.Equal("REJECTED", version.GetProperty("state").GetString());
        Assert.False(version.GetProperty("isImmutable").GetBoolean());
        Assert.True(await factory.WithDbAsync(db => db.ValidationResults.AnyAsync(r => r.RecipeVersionId == versionId && !r.Passed)));
        Assert.Contains("RELEASE_REFUSED", await AuditActionsAsync(versionId));

        // The specialist can reopen it.
        var reopened = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}", ValidContent()))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("DRAFT", reopened.GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Repeating_a_release_with_the_same_idempotency_key_returns_the_original_result()
    {
        var versionId = await ValidatedVersionAsync();
        var key = Guid.NewGuid().ToString();

        var first = await (await ReleaseAsync(versionId, idempotencyKey: key)).ShouldBeAsync(HttpStatusCode.OK);
        var second = await (await ReleaseAsync(versionId, idempotencyKey: key)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.True(second.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.GetProperty("version").GetRawText(), second.GetProperty("version").GetRawText());
        Assert.Single(await AuditActionsAsync(versionId), action => action == "RELEASE");

        // Without the key, or with another, it is an attempt to release a released version.
        await (await ReleaseAsync(versionId)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-01");
        await (await ReleaseAsync(versionId, idempotencyKey: "another-key"))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-01");
    }

    // ---------------------------------------------------------------- UC-09

    [Fact]
    public async Task Review_that_accepts_every_step_leaves_the_version_ready_for_release()
    {
        var versionId = await ValidatedVersionAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray();

        var result = await (await manager.PostAsJsonAsync($"{Versions}/{versionId}/review", new
            {
                decisions = steps.Select(s => new { stepId = s.GetProperty("id").GetInt64(), action = "ACCEPT" }),
            }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("ACCEPTED", result.GetProperty("outcome").GetString());
        Assert.Equal("VALIDATED", result.GetProperty("version").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Review_that_rejects_a_step_rejects_the_version()
    {
        var versionId = await ValidatedVersionAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("id").GetInt64()).ToList();

        var result = await (await manager.PostAsJsonAsync($"{Versions}/{versionId}/review", new
            {
                decisions = steps.Select((id, i) => new { stepId = id, action = i == 0 ? "REJECT" : "ACCEPT" }),
            }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal("REJECTED", result.GetProperty("version").GetProperty("state").GetString());
        await (await ReleaseAsync(versionId, OtherManager))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
    }

    [Fact]
    public async Task Review_must_decide_every_step()
    {
        var versionId = await ValidatedVersionAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var firstStep = (await factory.GetVersionAsync(versionId)).GetProperty("steps")[0].GetProperty("id").GetInt64();

        var response = await manager.PostAsJsonAsync($"{Versions}/{versionId}/review",
            new { decisions = new[] { new { stepId = firstStep, action = "ACCEPT" } } });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("decisions", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Every_state_changing_action_on_the_way_to_release_is_in_the_audit_log()
    {
        var versionId = await ValidatedVersionAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray();
        await (await manager.PostAsJsonAsync($"{Versions}/{versionId}/review", new
        {
            decisions = steps.Select(s => new { stepId = s.GetProperty("id").GetInt64(), action = "ACCEPT" }),
        })).ShouldBeAsync(HttpStatusCode.OK);
        await (await ReleaseAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);

        var entries = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "RecipeVersion" && a.EntityId == versionId)
            .OrderBy(a => a.Id).ToListAsync());

        Assert.Equal(["CREATE", "UPDATE", "VALIDATE", "SUBMIT", "REVIEW", "VALIDATE", "RELEASE"],
            entries.Select(e => e.Action));
        var specialistId = await factory.UserIdAsync(TestUsers.RdSpecialist);
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);
        Assert.Equal(specialistId, entries[0].UserId);
        Assert.Equal(managerId, entries[^1].UserId);

        var release = JsonSerializer.Deserialize<JsonElement>(entries[^1].PayloadJson!);
        Assert.Equal(specialistId, release.GetProperty("author").GetInt64());
        Assert.Equal(managerId, release.GetProperty("approver").GetInt64());
        Assert.Equal(64, release.GetProperty("contentHash").GetString()!.Length);
    }

    // ---------------------------------------------------------------- UC-26

    [Fact]
    public async Task Existing_recipe_is_imported_as_structured_data_with_origin_forced_to_existing()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var code = UniqueCode();

        var response = await specialist.PostAsJsonAsync($"{Recipes}/import-existing", new
        {
            recipeCode = code, name = "Trà đá truyền thống", category = "TEA", origin = "NEW",
            steps = new[]
            {
                Step(1, "Brew the black tea", "TEA_BREWER", 420, [Use("ING-BLACKTEA", 20m, "g")]),
                Step(2, "Pour over ice", seconds: 15, uses: [Use("ING-ICE", 200m, "g")], dependsOn: [1]),
            },
        });

        var imported = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("EXISTING", imported.GetProperty("recipe").GetProperty("origin").GetString());
        Assert.Equal("DRAFT", imported.GetProperty("version").GetProperty("state").GetString()); // BR-05
        Assert.Equal(1, imported.GetProperty("version").GetProperty("versionNo").GetInt32());
        Assert.Equal(2, imported.GetProperty("version").GetProperty("steps").GetArrayLength());
        Assert.True(imported.GetProperty("validation").GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Import_with_invalid_content_creates_nothing()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var code = UniqueCode();

        var response = await specialist.PostAsJsonAsync($"{Recipes}/import-existing", new
        {
            recipeCode = code, name = "Half a recipe", category = "TEA",
            steps = new[] { Step(1, "Brew", "NO_SUCH_MACHINE") },
        });

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.False(await factory.WithDbAsync(db => db.Recipes.AnyAsync(r => r.RecipeCode == code)));
    }

    // ---------------------------------------------------------------- seed

    [Fact]
    public async Task Seeded_reference_recipes_are_released_by_someone_other_than_their_author()
    {
        var seeded = await factory.WithDbAsync(db => db.RecipeVersions
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { r.RecipeCode, v.VersionNo, v.State, v.CreatedBy, v.ApprovedBy, v.ContentHash })
            .Where(x => x.VersionNo == 1)
            .ToListAsync());
        var byCode = seeded.Where(x => SeedRecipes.All.Any(s => s.Code == x.RecipeCode)).ToDictionary(x => x.RecipeCode);

        Assert.Equal(VersionState.Draft, byCode[SeedRecipes.BrokenDemoCode].State);
        Assert.Equal(VersionState.Validated, byCode[SeedRecipes.AwaitingReviewCode].State);
        var released = byCode.Values.Where(x => x.RecipeCode is not (SeedRecipes.BrokenDemoCode or SeedRecipes.AwaitingReviewCode)).ToList();
        Assert.Equal(9, released.Count);
        Assert.All(released, x =>
        {
            Assert.True(x.State is VersionState.Released or VersionState.Superseded);
            Assert.NotNull(x.ApprovedBy);
            Assert.NotEqual(x.CreatedBy, x.ApprovedBy);
            Assert.Equal(64, x.ContentHash!.Length);
        });
    }

    /// <summary>A second valid content, different from <see cref="RecipeScenario.ValidContent"/>.</summary>
    private static object OverdosedFixed() => new
    {
        steps = new[]
        {
            Step(1, "Brew a lighter oolong", "TEA_BREWER", 420, [Use("ING-OOLONG", 16m, "g")]),
            Step(2, "Add the milk base", seconds: 20, uses: [Use("ING-MILKBASE", 100m, "ml")], dependsOn: [1]),
        },
    };
}
