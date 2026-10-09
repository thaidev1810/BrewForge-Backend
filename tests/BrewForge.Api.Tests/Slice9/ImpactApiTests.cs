using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.ImpactScenario;

namespace BrewForge.Api.Tests.Slice9;

/// <summary>
/// UC-19 through the API: the what-if that writes nothing but its own rows,
/// the commit that flags and deletes nothing (BR-15), and the propagation a
/// release makes by itself.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ImpactApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- the what-if

    [Fact]
    public async Task What_if_leaves_the_database_unchanged_apart_from_uncommitted_change_impact_rows()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var before = await factory.RowCountsAsync();

        var result = await factory.AnalyzeAsync("Ingredient", drink.IngredientId);

        var after = await factory.RowCountsAsync();
        // One row for the course, two for the certificates, one for the live branch: and nothing else, anywhere.
        Assert.Equal(before["change_impact"] + 4, after["change_impact"]);
        Assert.Equal(before.Where(table => table.Key != "change_impact"), after.Where(table => table.Key != "change_impact"));
        Assert.Equal(35, after.Count);

        var runId = Guid.Parse(result.GetProperty("runId").GetString()!);
        var rows = await factory.WithDbAsync(db => db.ChangeImpacts.AsNoTracking().Where(r => r.AnalysisRunId == runId).ToListAsync());
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal((false, "Ingredient", drink.IngredientId), (row.Committed, row.TriggerEntity, row.TriggerId)));

        // What it describes has not happened.
        Assert.Equal("PUBLISHED", (await factory.GetCourseAsync(drink.CourseId)).StateOf());
        Assert.All(await factory.WithDbAsync(db => db.Certificates.Where(c => drink.CertificateIds.Contains(c.Id))
            .Select(c => c.Status).ToListAsync()), status => Assert.Equal(Domain.Training.CertificateStatus.Valid, status));
        Assert.False(result.GetProperty("committed").GetBoolean());
    }

    [Fact]
    public async Task What_if_has_the_shape_of_the_contract_and_says_what_committing_would_cost()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        var result = await factory.AnalyzeAsync("Ingredient", drink.IngredientId);

        Assert.Equal(32, result.GetProperty("runId").GetString()!.Length);
        Assert.Equal(("Ingredient", drink.IngredientId), (result.GetProperty("trigger").GetProperty("entityType").GetString(),
            result.GetProperty("trigger").GetProperty("entityId").GetInt64()));
        var affected = result.GetProperty("affected");
        Assert.Equal([drink.VersionId], affected.GetProperty("recipeVersions").EnumerateArray().Select(v => v.GetInt64()));
        var course = Assert.Single(affected.GetProperty("courses").EnumerateArray());
        Assert.Equal((drink.CourseId, "OUT_OF_DATE"), (course.Id(), course.GetProperty("impact").GetString()));
        Assert.Equal(drink.CertificateIds.Zip(drink.StaffIds).Select(pair => (pair.First, "NEEDS_RECERT", pair.Second)),
            affected.GetProperty("certificates").EnumerateArray()
                .Select(c => (c.Id(), c.GetProperty("impact").GetString()!, c.GetProperty("userId").GetInt64())));
        var branch = Assert.Single(affected.GetProperty("liveBranches").EnumerateArray());
        Assert.Equal((drink.BranchId, drink.RecipeId, "B01"), (branch.GetProperty("branchId").GetInt64(),
            branch.GetProperty("recipeId").GetInt64(), branch.GetProperty("branchCode").GetString()));
        Assert.False(result.GetProperty("committed").GetBoolean());
        Assert.Equal(("MSG-W03", "This change will affect 1 courses and 2 certificates. Review the impact analysis before committing."),
            (result.GetProperty("warning").GetProperty("code").GetString(), result.GetProperty("warning").GetProperty("message").GetString()));

        // The run can be read again as it was computed.
        var stored = await (await manager.GetAsync($"{Impact}/{result.GetProperty("runId").GetString()}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(result.GetRawText(), stored.GetRawText());
    }

    [Fact]
    public async Task Analysis_starts_from_an_ingredient_an_equipment_class_or_a_recipe_version()
    {
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 1);
        async Task<long[]> CoursesOf(string entityType, long entityId) =>
            [.. (await factory.AnalyzeAsync(entityType, entityId)).GetProperty("affected").GetProperty("courses").EnumerateArray().Select(c => c.Id())];

        Assert.Equal([drink.CourseId], await CoursesOf("Ingredient", drink.IngredientId));
        Assert.Equal([drink.CourseId], await CoursesOf("StandardEquipment", drink.EquipmentId));
        Assert.Equal([drink.CourseId], await CoursesOf("RecipeVersion", drink.VersionId));

        // A version nobody built on, and a draft: nothing is affected, and there is nothing to warn about.
        var (_, plain) = await factory.NewReleasedRecipeAsync();
        var (_, draft) = await factory.NewDraftAsync(RecipeScenario.ValidContent());
        foreach (var versionId in new[] { plain, draft })
        {
            var nothing = await factory.AnalyzeAsync("RecipeVersion", versionId);
            Assert.Empty(nothing.GetProperty("affected").GetProperty("courses").EnumerateArray());
            Assert.Empty(nothing.GetProperty("affected").GetProperty("certificates").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, nothing.GetProperty("warning").ValueKind);
        }
    }

    [Fact]
    public async Task Analysis_request_is_validated()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        Assert.Equal(["entityId", "entityType"], (await (await manager.PostAsJsonAsync(Impact, new { }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields().Order());
        Assert.Contains("entityType", (await (await manager.PostAsJsonAsync(Impact, new { entityType = "Course", entityId = 1 }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        await (await manager.PostAsJsonAsync(Impact, new { entityType = "Ingredient", entityId = 999999999 }))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.GetAsync($"{Impact}/{Guid.NewGuid():N}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await manager.GetAsync($"{Impact}/not-a-run")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await manager.PostAsync($"{Impact}/{Guid.NewGuid():N}/commit", null)).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- BR-15: the commit

    [Fact]
    public async Task BR_15_commit_flags_the_course_and_the_certificates_and_deletes_nothing()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var lessonsBefore = (await factory.GetCourseAsync(drink.CourseId)).GetProperty("modules").EnumerateArray()
            .Sum(m => m.GetProperty("lessons").GetArrayLength());
        var runId = (await factory.AnalyzeAsync("Ingredient", drink.IngredientId)).GetProperty("runId").GetString()!;
        var before = await factory.RowCountsAsync();

        var committed = await (await factory.CommitAsync(runId)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(committed.GetProperty("committed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, committed.GetProperty("warning").ValueKind);
        // Nothing was deleted: every table has at least the rows it had, and these four have exactly as many.
        var after = await factory.RowCountsAsync();
        Assert.All(before, table => Assert.True(after[table.Key] >= table.Value, table.Key));
        Assert.All(new[] { "course", "course_module", "lesson", "certificate", "quiz_question", "change_impact" },
            table => Assert.Equal(before[table], after[table]));

        // The old course still exists, whole, and is out of date.
        var course = await factory.GetCourseAsync(drink.CourseId);
        Assert.Equal(("OUT_OF_DATE", drink.VersionId), (course.StateOf(), course.GetProperty("recipeVersionId").GetInt64()));
        Assert.Equal(lessonsBefore, course.GetProperty("modules").EnumerateArray().Sum(m => m.GetProperty("lessons").GetArrayLength()));
        // The old certificates still exist, on the version they were earned on, and need re-certification.
        var certificates = await factory.WithDbAsync(db => db.Certificates.AsNoTracking().Where(c => drink.CertificateIds.Contains(c.Id)).ToListAsync());
        Assert.Equal(2, certificates.Count);
        Assert.All(certificates, certificate => Assert.Equal((Domain.Training.CertificateStatus.NeedsRecert, drink.VersionId, drink.CourseId),
            (certificate.Status, certificate.RecipeVersionId!.Value, certificate.CourseId)));
        // The rows of the run are committed.
        Assert.All(await factory.WithDbAsync(db => db.ChangeImpacts.AsNoTracking().Where(r => r.AnalysisRunId == Guid.Parse(runId)).ToListAsync()),
            row => Assert.True(row.Committed));

        // The branch still sells the drink, with nobody certified on it any more: shown, not blocked.
        var launch = await factory.WithDbAsync(db => db.BranchLaunchStatuses.AsNoTracking().SingleAsync(l => l.Id == drink.LaunchStatusId));
        Assert.Equal((Domain.Launch.LaunchStatus.Live, 0, false), (launch.Status, launch.CertifiedCount, launch.CoverageMet));

        // The trainees and the trainer who built the course were told, and the trail says what was done.
        var notices = await factory.WithDbAsync(db => db.AuditLogs.Where(a => a.EntityType == "AppUser" && a.Action == "NOTIFY")
            .OrderByDescending(a => a.Id).Take(40).Select(a => new { a.EntityId, a.PayloadJson }).ToListAsync());
        Assert.All(drink.StaffIds, staff => Assert.Contains(notices, n => n.EntityId == staff && n.PayloadJson!.Contains("Re-training required")));
        Assert.Contains(notices.Where(n => n.EntityId == trainerId).Select(n => JsonSerializer.Deserialize<JsonElement>(n.PayloadJson!)),
            notice => notice.GetProperty("subject").GetString() == "Course out of date"
                      && notice.GetProperty("courseId").GetInt64() == drink.CourseId);
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "Course" && a.EntityId == drink.CourseId && a.Action == "MARK_OUT_OF_DATE")));
        Assert.Equal(2, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityType == "Certificate" && drink.CertificateIds.Contains(a.EntityId) && a.Action == "FLAG_RECERT")));
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "Ingredient" && a.EntityId == drink.IngredientId && a.Action == "COMMIT_IMPACT")));

        // A run is committed once.
        await (await factory.CommitAsync(runId)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "IMPACT_ALREADY_COMMITTED");
        Assert.True((await (await manager.GetAsync($"{Impact}/{runId}")).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("committed").GetBoolean());
    }

    [Fact]
    public async Task Commit_takes_in_a_certificate_issued_after_the_what_if()
    {
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 1);
        var run = await factory.AnalyzeAsync("RecipeVersion", drink.VersionId);
        Assert.Single(run.GetProperty("affected").GetProperty("certificates").EnumerateArray());
        // The second trainee is certified while the manager is still looking at the what-if.
        await factory.CertifyAsync(await factory.UserIdAsync("trainee2"), drink.CourseId, drink.VersionId);

        var committed = await (await factory.CommitAsync(run.GetProperty("runId").GetString()!)).ShouldBeAsync(HttpStatusCode.OK);

        // Nobody is left certified on a procedure that no longer stands.
        Assert.Equal(2, committed.GetProperty("affected").GetProperty("certificates").GetArrayLength());
        Assert.All(await factory.WithDbAsync(db => db.Certificates.Where(c => c.RecipeVersionId == drink.VersionId).Select(c => c.Status).ToListAsync()),
            status => Assert.Equal(Domain.Training.CertificateStatus.NeedsRecert, status));
    }

    // ---------------------------------------------------------------- on release

    [Fact]
    public async Task Releasing_a_new_version_puts_the_course_out_of_date_and_flags_the_certificates_by_itself()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var certificatesBefore = await factory.WithDbAsync(db => db.Certificates.CountAsync());

        var secondVersion = await factory.ReleaseNewVersionAsync(drink.RecipeId, new
        {
            steps = new[] { RecipeScenario.Step(1, "Brew the oolong differently", "TEA_BREWER", 400, [RecipeScenario.Use("ING-OOLONG", 17m, "g")], gate: "Water at 88 C") },
        });

        // Nobody ran an impact analysis: superseding the version did it (BR-15).
        Assert.Equal("OUT_OF_DATE", (await factory.GetCourseAsync(drink.CourseId)).StateOf());
        var passport = await (await trainee.GetAsync("/api/v1/me/certificates")).ShouldBeAsync(HttpStatusCode.OK);
        var held = passport.EnumerateArray().Single(c => c.GetProperty("courseId").GetInt64() == drink.CourseId);
        Assert.Equal(("NEEDS_RECERT", drink.VersionId), (held.GetProperty("status").GetString(), held.GetProperty("recipeVersionId").GetInt64()));
        Assert.Equal(certificatesBefore, await factory.WithDbAsync(db => db.Certificates.CountAsync()));
        var rows = await factory.WithDbAsync(db => db.ChangeImpacts.AsNoTracking()
            .Where(r => r.TriggerEntity == "RecipeVersion" && r.TriggerId == drink.VersionId).ToListAsync());
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.True(row.Committed));

        // The trainer rebuilds the same course on the new version; what was authored is kept for review (BR-30).
        var rebuilt = await (await trainer.PostAsync($"{Courses}/{drink.CourseId}/rebuild", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(("DRAFT", secondVersion, drink.CourseId), (rebuilt.StateOf(), rebuilt.GetProperty("recipeVersionId").GetInt64(), rebuilt.Id()));
        // A course that is not out of date is not rebuilt.
        await (await trainer.PostAsync($"{Courses}/{drink.CourseId}/rebuild", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-18");
    }
}
