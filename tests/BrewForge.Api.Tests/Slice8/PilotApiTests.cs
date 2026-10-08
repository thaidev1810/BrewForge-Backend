using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Launch;
using BrewForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.PilotScenario;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice8;

/// <summary>
/// UC-21, UC-24 and UC-25 through the API: pilot setup (BR-26, BR-28), the
/// launch gate (BR-23), drinks that go live without one (BR-36), the
/// evaluation and the decision (BR-27).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PilotApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- UC-21

    [Fact]
    public async Task Pilot_is_created_with_its_version_branches_period_and_criteria()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync();
        var (control, _) = await factory.NewReleasedRecipeAsync();
        var (b01, b02) = (await factory.BranchIdAsync("B01"), await factory.BranchIdAsync("B02"));

        var created = await (await manager.PostAsJsonAsync(Pilots, Body(versionId, [b01, b02],
                [Absolute(40), Relative(control, 60), Retention(25)], name: "Oolong pilot")))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal((versionId, recipeId, 1), (created.GetProperty("recipeVersionId").GetInt64(),
            created.GetProperty("recipeId").GetInt64(), created.GetProperty("versionNo").GetInt32()));
        Assert.Equal(("Oolong pilot", "DRAFT", 2), (created.GetProperty("name").GetString(),
            created.GetProperty("state").GetString(), created.GetProperty("minCertifiedStaff").GetInt32()));
        Assert.Equal((Today.ToString("yyyy-MM-dd"), Today.AddDays(13).ToString("yyyy-MM-dd")),
            (created.GetProperty("startDate").GetString(), created.GetProperty("endDate").GetString()));
        Assert.Equal(managerId, created.GetProperty("createdBy").GetInt64());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("decision").ValueKind);
        // The criteria come back as the contract writes them.
        Assert.Equal(
            $$"""[{"type":"ABSOLUTE","cupsPerDayPerBranch":40},{"type":"RELATIVE","controlRecipeId":{{control}},"minPercentOfControl":60},{"type":"RETENTION","maxDropSecondHalfPct":25}]""",
            JsonSerializer.Serialize(created.GetProperty("criteria").EnumerateArray().Select(c => c.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value))));
        Assert.Equal([("B01", "PREPARING"), ("B02", "PREPARING")], created.GetProperty("branches").EnumerateArray()
            .Select(b => (b.GetProperty("branchCode").GetString()!, b.GetProperty("readinessState").GetString()!)));
        // And they are stored in the column in the shape of the contract.
        var stored = await factory.WithDbAsync(db => db.PilotPrograms.Where(p => p.Id == created.Id()).Select(p => p.CriteriaJson).SingleAsync());
        Assert.Equal("ABSOLUTE", JsonSerializer.Deserialize<JsonElement>(stored).GetProperty("criteria")[0].GetProperty("type").GetString());

        // Nothing is planned at the branches until the pilot starts.
        Assert.Empty(await factory.LaunchStatusesAsync(recipeId));

        var listed = await (await manager.GetAsync($"{Pilots}?state=DRAFT&recipeVersionId={versionId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal([created.Id()], listed.GetProperty("items").EnumerateArray().Select(p => p.Id()));
        Assert.Equal(created.Id(), (await factory.GetPilotAsync(created.Id())).Id());
    }

    [Fact]
    public async Task Pilot_is_validated()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync();
        var (_, draftVersion) = await factory.NewDraftAsync(RecipeScenario.ValidContent());
        var b01 = await factory.BranchIdAsync("B01");
        async Task<JsonElement> Refused(object body, HttpStatusCode status = HttpStatusCode.BadRequest) =>
            await (await manager.PostAsJsonAsync(Pilots, body)).ShouldBeErrorAsync(status);

        Assert.Equal(["branchIds", "criteria", "endDate", "recipeVersionId", "startDate"], (await Refused(new { })).DetailFields().Order());
        Assert.Contains("endDate", (await Refused(Body(versionId, [b01], startDate: Today, endDate: Today.AddDays(-1)))).DetailFields());
        Assert.Contains("minCertifiedStaff", (await Refused(Body(versionId, [b01], minCertifiedStaff: 0))).DetailFields());
        Assert.Contains("branchIds", (await Refused(Body(versionId, [999999999]))).DetailFields());
        Assert.Contains("criteria[0].cupsPerDayPerBranch", (await Refused(Body(versionId, [b01], [Absolute(0)]))).DetailFields());
        Assert.Contains("criteria", (await Refused(Body(versionId, [b01], [Absolute(40), Absolute(50)]))).DetailFields());
        Assert.Contains("criteria", (await Refused(Body(versionId, [b01], [Relative(999999999, 60)]))).DetailFields());
        Assert.Contains("criteria", (await Refused(Body(versionId, [b01], [Relative(recipeId, 60)]))).DetailFields()); // against itself
        Assert.Contains((await Refused(Body(versionId, [b01], [new { type = "POPULARITY" }]))).DetailFields(), field => field.Contains("criteria"));

        await Refused(Body(999999999, [b01]), HttpStatusCode.NotFound);
        var notReleased = await Refused(Body(draftVersion, [b01]), HttpStatusCode.Conflict);
        Assert.Equal("PILOT_VERSION_NOT_RELEASED", notReleased.GetProperty("rule").GetString());
    }

    // ---------------------------------------------------------------- BR-28

    [Fact]
    public async Task BR_28_a_version_may_have_only_one_draft_or_running_pilot()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewPilotAsync();
        var body = Body(pilot.VersionId, pilot.BranchIds);

        // While it is a DRAFT, and while it is RUNNING.
        var refusal = await (await manager.PostAsJsonAsync(Pilots, body)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-28");
        Assert.Contains("recipeVersionId", refusal.DetailFields());
        await factory.StartPilotAsync(pilot.PilotId);
        await (await manager.PostAsJsonAsync(Pilots, body)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-28");

        // Cancelled, its version is free for another.
        var cancelled = await (await manager.PostAsync($"{pilot.Url}/cancel", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CANCELLED", cancelled.GetProperty("state").GetString());
        await (await manager.PostAsJsonAsync(Pilots, body)).ShouldBeAsync(HttpStatusCode.Created);
        // A cancelled pilot is over: it is not started, cancelled again or changed.
        await (await manager.PostAsync($"{pilot.Url}/start", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await manager.PostAsync($"{pilot.Url}/cancel", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
    }

    [Fact]
    public async Task BR_28_the_index_of_the_database_answers_a_race_with_the_same_409()
    {
        var pilot = await factory.NewPilotAsync();
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);

        // A second request that passed the check before the first one was stored.
        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var version = await db.RecipeVersions.SingleAsync(v => v.Id == pilot.VersionId);
            var recipe = await db.Recipes.SingleAsync(r => r.Id == pilot.RecipeId);
            db.PilotPrograms.Add(PilotProgram.Create(recipe, version, pilotsOfVersion: [], "Racing pilot", Today, Today.AddDays(6),
                PilotCriteria.Create([new PilotCriterion(CriterionType.Absolute, 40)]), 2, pilot.BranchIds.ToList(), managerId));
            await db.SaveChangesAsync();
        }));

        var refusal = PostgresErrorTranslator.Translate(exception)!;
        Assert.Equal(("BR-28", BrewForge.Domain.Common.ErrorKind.RuleViolation), (refusal.Rule, refusal.Kind));
    }

    [Fact]
    public async Task BR_28_a_version_with_an_active_pilot_cannot_be_superseded()
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewPilotAsync();
        // The next version of the drink, validated and ready to be released.
        var next = (await (await specialist.PostAsJsonAsync($"{RecipeScenario.Recipes}/{pilot.RecipeId}/versions", new { }))
            .ShouldBeAsync(HttpStatusCode.Created)).Id();
        await (await specialist.PutAsJsonAsync($"{RecipeScenario.Versions}/{next}", RecipeScenario.LighterContent())).ShouldBeAsync(HttpStatusCode.OK);
        await (await specialist.PostAsync($"{RecipeScenario.Versions}/{next}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);

        // Releasing it would supersede the version under test.
        await (await manager.PostAsync($"{RecipeScenario.Versions}/{next}/release", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-28");
        await factory.StartPilotAsync(pilot.PilotId);
        await (await manager.PostAsync($"{RecipeScenario.Versions}/{next}/release", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-28");

        Assert.Equal("RELEASED", (await factory.GetVersionAsync(pilot.VersionId)).GetProperty("state").GetString());
        Assert.Equal("VALIDATED", (await factory.GetVersionAsync(next)).GetProperty("state").GetString());

        // The pilot abandoned, the release goes through.
        await (await manager.PostAsync($"{pilot.Url}/cancel", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await manager.PostAsync($"{RecipeScenario.Versions}/{next}/release", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("SUPERSEDED", (await factory.GetVersionAsync(pilot.VersionId)).GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- BR-26

    [Fact]
    public async Task BR_26_the_criteria_are_read_only_once_the_pilot_is_running()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewPilotAsync(["B01", "B02"]);
        var b01 = pilot.BranchIds[0];

        // A DRAFT is still open: the period, the branches, the threshold and the criteria.
        var changed = await (await manager.PutAsJsonAsync(pilot.Url, Body(pilot.VersionId, [b01], [Absolute(55), Retention(20)],
                startDate: Today.AddDays(1), minCertifiedStaff: 1, name: "Changed")))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(("Changed", 1, Today.AddDays(1).ToString("yyyy-MM-dd")), (changed.GetProperty("name").GetString(),
            changed.GetProperty("minCertifiedStaff").GetInt32(), changed.GetProperty("startDate").GetString()));
        Assert.Equal(["ABSOLUTE", "RETENTION"], changed.GetProperty("criteria").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
        Assert.Equal(55, changed.GetProperty("criteria")[0].GetProperty("cupsPerDayPerBranch").GetDecimal());
        Assert.Equal(["B01"], changed.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchCode").GetString()));
        // The version it tests is not one of the things that can change.
        var (_, otherVersion) = await factory.NewReleasedRecipeAsync();
        Assert.Contains("recipeVersionId", (await (await manager.PutAsJsonAsync(pilot.Url, Body(otherVersion, [b01])))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());

        await factory.StartPilotAsync(pilot.PilotId);

        // RUNNING: an easier target is refused, and so is every other edit, valid or not.
        await (await manager.PutAsJsonAsync(pilot.Url, Body(pilot.VersionId, [b01], [Absolute(5)])))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-26");
        await (await manager.PutAsJsonAsync(pilot.Url, new { })).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-26");
        var after = await factory.GetPilotAsync(pilot.PilotId);
        Assert.Equal(55, after.GetProperty("criteria")[0].GetProperty("cupsPerDayPerBranch").GetDecimal());
        Assert.Equal("Changed", after.GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------- BR-23: the gate

    [Fact]
    public async Task BR_23_a_branch_is_opened_for_sale_only_through_the_gate()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewPilotAsync(["B01", "B02"]);
        var b01 = pilot.BranchIds[0];

        // Before the pilot runs there is no gate to pass.
        await (await factory.GoLiveAsync(pilot.PilotId, b01)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        var started = await factory.StartPilotAsync(pilot.PilotId);
        Assert.Equal("RUNNING", started.GetProperty("state").GetString());
        // Starting planned the drink at both branches: PREPARING, nobody certified.
        Assert.All(await factory.LaunchStatusesAsync(pilot.RecipeId), row =>
            Assert.Equal(("PREPARING", 0, 2, false, 2), (row.GetProperty("status").GetString(), row.GetProperty("certifiedCount").GetInt32(),
                row.GetProperty("minCertifiedStaff").GetInt32(), row.GetProperty("coverageMet").GetBoolean(), row.GetProperty("shortfall").GetInt32())));

        // PREPARING may not go straight to LIVE: with nobody certified, and with one of the two needed.
        var refusal = await (await factory.GoLiveAsync(pilot.PilotId, b01)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-23");
        Assert.Contains("certifiedCount", refusal.DetailFields());
        await factory.CertifyStaffAsync(pilot, "B01", count: 1);
        await (await factory.GoLiveAsync(pilot.PilotId, b01)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-23");
        Assert.Equal("PREPARING", (await factory.LaunchStatusAsync(pilot.RecipeId, "B01")).GetProperty("status").GetString());
        // And while it is not live, nothing can be sold there (BR-24).
        await (await factory.PostSaleAsync(pilot.DrinkAt("B01"), Today, 10)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-24");

        await factory.CertifyStaffAsync(pilot, "B01", count: 2);
        var readiness = await (await manager.GetAsync($"{pilot.Url}/readiness")).ShouldBeAsync(HttpStatusCode.OK);
        var rows = readiness.GetProperty("branches").EnumerateArray().ToDictionary(b => b.GetProperty("branchCode").GetString()!);
        Assert.Equal((2, 2, true), (rows["B01"].GetProperty("certifiedCount").GetInt32(), rows["B01"].GetProperty("minCertifiedStaff").GetInt32(),
            rows["B01"].GetProperty("coverageMet").GetBoolean()));
        Assert.Equal((0, false), (rows["B02"].GetProperty("certifiedCount").GetInt32(), rows["B02"].GetProperty("coverageMet").GetBoolean()));

        // Two certified: the gate opens.
        var live = await (await factory.GoLiveAsync(pilot.PilotId, b01)).ShouldBeAsync(HttpStatusCode.OK);
        var branches = live.GetProperty("branches").EnumerateArray().ToDictionary(b => b.GetProperty("branchCode").GetString()!);
        Assert.Equal("LIVE", branches["B01"].GetProperty("readinessState").GetString());
        Assert.NotEqual(JsonValueKind.Null, branches["B01"].GetProperty("wentLiveAt").ValueKind);
        Assert.Equal("PREPARING", branches["B02"].GetProperty("readinessState").GetString());
        var status = await factory.LaunchStatusAsync(pilot.RecipeId, "B01");
        Assert.Equal(("LIVE", 2, true, pilot.VersionId), (status.GetProperty("status").GetString(), status.GetProperty("certifiedCount").GetInt32(),
            status.GetProperty("coverageMet").GetBoolean(), status.GetProperty("recipeVersionId").GetInt64()));
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("liveSince").ValueKind);

        // From this point sales can be recorded at B01, on the pilot's version, and still not at B02.
        var sale = await factory.RecordSaleAsync(pilot.DrinkAt("B01"), Today, 42);
        Assert.Equal(pilot.VersionId, sale.GetProperty("recipeVersionId").GetInt64());
        await (await factory.PostSaleAsync(pilot.DrinkAt("B02"), Today, 10)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-24");

        // A branch is opened once, and only a branch of the pilot.
        await (await factory.GoLiveAsync(pilot.PilotId, b01)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await factory.GoLiveAsync(pilot.PilotId, await factory.BranchIdAsync("B03"))).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "BranchLaunchStatus" && a.EntityId == status.Id() && a.Action == "GO_LIVE")));
    }

    [Fact]
    public async Task Readiness_checker_makes_a_branch_ready_when_its_second_member_of_staff_is_certified()
    {
        // The whole chain: a pilot, the course of its version, a class, two trainees certified through it.
        var pilot = await factory.NewPilotAsync(withCourse: true);
        await factory.StartPilotAsync(pilot.PilotId);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.GetCourseAsync(pilot.CourseId);
        var classId = (await (await trainer.PostAsJsonAsync(TrainingScenario.Classes, new
            {
                courseId = pilot.CourseId, branchId = pilot.BranchIds[0], name = "Pilot class",
                startDate = TrainingScenario.Today, endDate = TrainingScenario.Today.AddDays(14),
            }))
            .ShouldBeAsync(HttpStatusCode.Created)).Id();
        var setup = new ClassSetup(pilot.CourseId, pilot.VersionId, classId, [],
            [.. course.GetProperty("modules").EnumerateArray().Select(m => m.Id())]);
        await factory.OpenClassAsync(classId);
        async Task<(string Launch, string Pilot, int Certified)> StateAsync()
        {
            var launch = await factory.LaunchStatusAsync(pilot.RecipeId, "B01");
            var branch = (await factory.GetPilotAsync(pilot.PilotId)).GetProperty("branches")[0];
            return (launch.GetProperty("status").GetString()!, branch.GetProperty("readinessState").GetString()!,
                launch.GetProperty("certifiedCount").GetInt32());
        }

        await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, TestUsers.Trainee));
        Assert.Equal(("PREPARING", "PREPARING", 1), await StateAsync());

        await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, "trainee2"));
        Assert.Equal(("READY", "READY", 2), await StateAsync());

        await (await factory.GoLiveAsync(pilot.PilotId, pilot.BranchIds[0])).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(("LIVE", "LIVE", 2), await StateAsync());
    }

    [Fact]
    public async Task Training_needs_list_the_staff_of_a_branch_that_is_short_of_certified_people()
    {
        var pilot = await factory.NewPilotAsync(withCourse: true);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var url = $"/api/v1/training-needs?courseId={pilot.CourseId}";
        async Task<Dictionary<string, string>> ReasonsAsync() =>
            (await (await trainer.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()
            .ToDictionary(n => n.GetProperty("username").GetString()!, n => n.GetProperty("reason").GetString()!);

        // The regulation alone: a product course is mandatory for every trainee of the chain.
        Assert.All((await ReasonsAsync()).Values, reason => Assert.Equal("REGULATION", reason));

        // The pilot starts: B01 prepares for the drink and has nobody certified on it.
        await factory.StartPilotAsync(pilot.PilotId);
        var needs = await ReasonsAsync();
        Assert.Equal(("BRANCH_SHORTFALL", "BRANCH_SHORTFALL"), (needs[TestUsers.Trainee], needs["trainee2"]));
        Assert.Equal("REGULATION", needs["trainee3"]); // B02 is not preparing for it

        // One certified: the other is still needed, the certified one no longer listed at all.
        await factory.CertifyStaffAsync(pilot, "B01", count: 1);
        needs = await ReasonsAsync();
        Assert.DoesNotContain(TestUsers.Trainee, needs.Keys);
        Assert.Equal("BRANCH_SHORTFALL", needs["trainee2"]);
    }

    // ---------------------------------------------------------------- BR-36

    [Fact]
    public async Task BR_36_an_existing_drink_is_live_at_every_branch_on_release_with_the_coverage_warning()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync(origin: "EXISTING");

        // No pilot, no gate: LIVE at once, coverage not met, and shown as such.
        var statuses = (await factory.LaunchStatusesAsync(recipeId)).ToDictionary(row => row.GetProperty("branchCode").GetString()!);
        Assert.Subset(statuses.Keys.ToHashSet(), new HashSet<string> { "B01", "B02", "B03" });
        Assert.All(statuses.Values, row =>
        {
            Assert.Equal(("LIVE", "EXISTING", versionId), (row.GetProperty("status").GetString(), row.GetProperty("origin").GetString(),
                row.GetProperty("recipeVersionId").GetInt64()));
            Assert.Equal((false, 0, 2, 2), (row.GetProperty("coverageMet").GetBoolean(), row.GetProperty("certifiedCount").GetInt32(),
                row.GetProperty("minCertifiedStaff").GetInt32(), row.GetProperty("shortfall").GetInt32()));
            Assert.NotEqual(JsonValueKind.Null, row.GetProperty("liveSince").ValueKind);
        });

        // Not blocked: it is sold today although nobody is certified on it.
        var code = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId).Select(r => r.RecipeCode).SingleAsync());
        var b01 = await factory.BranchIdAsync("B01");
        await factory.RecordSaleAsync(new LiveDrink(b01, "B01", recipeId, code, versionId, 0), Today, 75);

        // And it has no pilot to go through.
        await (await manager.PostAsJsonAsync(Pilots, Body(versionId, [b01]))).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-36");
    }

    [Fact]
    public async Task BR_36_a_new_drink_is_live_nowhere_on_release_and_needs_the_gate()
    {
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync(origin: "NEW");
        var code = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId).Select(r => r.RecipeCode).SingleAsync());
        var b01 = await factory.BranchIdAsync("B01");

        Assert.Empty(await factory.LaunchStatusesAsync(recipeId));
        await (await factory.PostSaleAsync(new LiveDrink(b01, "B01", recipeId, code, versionId, 0), Today, 75))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-24");
    }

    [Fact]
    public async Task BR_36_a_new_version_of_an_existing_drink_replaces_the_old_one_at_every_branch_it_is_sold_at()
    {
        var (recipeId, firstVersion) = await factory.NewReleasedRecipeAsync(origin: "EXISTING");
        await factory.BackdateLaunchAsync(recipeId, days: 10);
        var code = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId).Select(r => r.RecipeCode).SingleAsync());
        var drink = new LiveDrink(await factory.BranchIdAsync("B01"), "B01", recipeId, code, firstVersion, 0);

        var secondVersion = await factory.ReleaseNewVersionAsync(recipeId, RecipeScenario.LighterContent());

        // LIVE stays LIVE; the bound version changes and coverage is counted for the new one.
        Assert.All(await factory.LaunchStatusesAsync(recipeId), row =>
            Assert.Equal(("LIVE", secondVersion, false), (row.GetProperty("status").GetString(),
                row.GetProperty("recipeVersionId").GetInt64(), row.GetProperty("coverageMet").GetBoolean())));
        // Sales follow: today on the new version, a day entered late on the one sold that day (BR-24).
        Assert.Equal(secondVersion, (await factory.RecordSaleAsync(drink, Today, 60)).GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(firstVersion, (await factory.RecordSaleAsync(drink, Today.AddDays(-3), 55)).GetProperty("recipeVersionId").GetInt64());
    }

    // ---------------------------------------------------------------- UC-24

    [Fact]
    public async Task Pilot_ends_by_the_calendar_and_is_evaluated_per_criterion_per_branch_and_per_week()
    {
        var control = await factory.NewLiveDrinkAsync(branchCode: "B01", liveDaysAgo: 30);
        var controlAtB02 = await factory.GoLiveAsync("B02", control.RecipeId, control.VersionId);
        var pilot = await factory.NewFinishedPilotAsync([Absolute(40), Relative(control.RecipeId, 60), Retention(25)]);
        await factory.SellAsync(control, Today.AddDays(-14), Today.AddDays(-1), 70);
        await factory.SellAsync(controlAtB02, Today.AddDays(-14), Today.AddDays(-1), 70);

        var evaluation = await factory.EvaluationAsync(pilot.PilotId);

        // Nobody ended it: its last day passed.
        Assert.Equal((pilot.PilotId, "ENDED"), (evaluation.GetProperty("pilotId").GetInt64(), evaluation.GetProperty("state").GetString()));
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "PilotProgram" && a.EntityId == pilot.PilotId && a.Action == "END")));
        Assert.Equal((true, "PASS"), (evaluation.GetProperty("coverageComplete").GetBoolean(), evaluation.GetProperty("overall").GetString()));

        var criteria = evaluation.GetProperty("criteria").EnumerateArray().ToDictionary(c => c.GetProperty("type").GetString()!);
        Assert.Equal((40m, 47.5m, "PASS"), (criteria["ABSOLUTE"].GetProperty("target").GetDecimal(),
            criteria["ABSOLUTE"].GetProperty("actual").GetDecimal(), criteria["ABSOLUTE"].GetProperty("verdict").GetString()));
        Assert.Equal((60m, 67.9m, "PASS"), (criteria["RELATIVE"].GetProperty("target").GetDecimal(),
            criteria["RELATIVE"].GetProperty("actual").GetDecimal(), criteria["RELATIVE"].GetProperty("verdict").GetString()));
        Assert.Equal((25m, 0m, "PASS"), (criteria["RETENTION"].GetProperty("target").GetDecimal(),
            criteria["RETENTION"].GetProperty("actual").GetDecimal(), criteria["RETENTION"].GetProperty("verdict").GetString()));

        var branches = evaluation.GetProperty("byBranch").EnumerateArray().ToDictionary(b => b.GetProperty("branchId").GetInt64());
        var b01 = branches[pilot.BranchIds[0]];
        Assert.Equal((14, 14, 700, 50.0m, "PASS"), (b01.GetProperty("expectedDays").GetInt32(), b01.GetProperty("tradingDays").GetInt32(),
            b01.GetProperty("cupsTotal").GetInt32(), b01.GetProperty("cupsPerDay").GetDecimal(), b01.GetProperty("verdict").GetString()));
        Assert.Equal(630, branches[pilot.BranchIds[1]].GetProperty("cupsTotal").GetInt32());
        Assert.Equal([(1, 665), (2, 665)], evaluation.GetProperty("byWeek").EnumerateArray()
            .Select(w => (w.GetProperty("week").GetInt32(), w.GetProperty("cups").GetInt32())));
    }

    [Fact]
    public async Task Branch_with_missing_trading_days_is_reported_as_incomplete_coverage_and_not_scored()
    {
        // B02 sold poorly, and its last two days were never recorded.
        var pilot = await factory.NewRunningPilotAsync(["B01", "B02"], [Absolute(40)]);
        await factory.TimePassesAsync(pilot);
        await factory.SellAsync(pilot.DrinkAt("B01"), Today.AddDays(-14), Today.AddDays(-1), 50);
        await factory.SellAsync(pilot.DrinkAt("B02"), Today.AddDays(-14), Today.AddDays(-3), 10);

        var evaluation = await factory.EvaluationAsync(pilot.PilotId);

        Assert.False(evaluation.GetProperty("coverageComplete").GetBoolean());
        var incomplete = evaluation.GetProperty("byBranch").EnumerateArray().Single(b => b.GetProperty("branchId").GetInt64() == pilot.BranchIds[1]);
        Assert.Equal((14, 12, false, "INCOMPLETE"), (incomplete.GetProperty("expectedDays").GetInt32(), incomplete.GetProperty("tradingDays").GetInt32(),
            incomplete.GetProperty("coverageComplete").GetBoolean(), incomplete.GetProperty("verdict").GetString()));
        // Scored on the branch whose figures are whole: 50 a day, not 31.5.
        var absolute = Assert.Single(evaluation.GetProperty("criteria").EnumerateArray());
        Assert.Equal((50.0m, "PASS"), (absolute.GetProperty("actual").GetDecimal(), absolute.GetProperty("verdict").GetString()));
        Assert.Equal("PASS", evaluation.GetProperty("overall").GetString());
    }

    [Fact]
    public async Task Pilot_that_misses_its_target_fails()
    {
        var pilot = await factory.NewFinishedPilotAsync([Absolute(40)], cupsAtB01: 30, cupsAtB02: 20);

        var evaluation = await factory.EvaluationAsync(pilot.PilotId);

        Assert.Equal((true, "FAIL"), (evaluation.GetProperty("coverageComplete").GetBoolean(), evaluation.GetProperty("overall").GetString()));
        Assert.Equal(25.0m, evaluation.GetProperty("criteria")[0].GetProperty("actual").GetDecimal());
    }

    // ---------------------------------------------------------------- BR-27

    [Fact]
    public async Task BR_27_a_decision_is_refused_unless_the_pilot_has_ended()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var draft = await factory.NewPilotAsync();
        var running = await factory.NewRunningPilotAsync();
        var cancelled = await factory.NewPilotAsync();
        await (await manager.PostAsync($"{cancelled.Url}/cancel", null)).ShouldBeAsync(HttpStatusCode.OK);

        foreach (var pilot in new[] { draft, running, cancelled })
        {
            var refusal = await (await factory.DecideAsync(pilot.PilotId, "ROLLOUT")).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-27");
            Assert.Contains("state", refusal.DetailFields());
            Assert.Equal(JsonValueKind.Null, (await factory.GetPilotAsync(pilot.PilotId)).GetProperty("decision").ValueKind);
        }
        Assert.Equal(0, await factory.WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM launch_decision WHERE pilot_program_id IN ({draft.PilotId}, {running.PilotId}, {cancelled.PilotId})")
            .SingleAsync()));
    }

    [Fact]
    public async Task BR_27_the_decision_keeps_the_figures_as_they_were_evaluated_when_it_was_taken()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        using var branchManager = await factory.ClientForAsync(TestUsers.BranchManager);
        var managerId = await factory.UserIdAsync(TestUsers.RdManager);
        var pilot = await factory.NewFinishedPilotAsync([Absolute(40)]);
        Assert.Contains("decision", (await (await manager.PostAsJsonAsync($"{pilot.Url}/decision", new { })).ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());

        var decided = await (await factory.DecideAsync(pilot.PilotId, "REVISE", "Sells, but the second week was flat"))
            .ShouldBeAsync(HttpStatusCode.OK);

        var decision = decided.GetProperty("decision");
        Assert.Equal(("REVISE", managerId), (decision.GetProperty("decision").GetString(), decision.GetProperty("decidedBy").GetInt64()));
        var evaluated = decision.GetProperty("evaluated");
        Assert.Equal(("PASS", true, 47.5m), (evaluated.GetProperty("overall").GetString(), evaluated.GetProperty("coverageComplete").GetBoolean(),
            evaluated.GetProperty("criteria")[0].GetProperty("actual").GetDecimal()));
        Assert.Equal("ABSOLUTE", evaluated.GetProperty("criteria")[0].GetProperty("type").GetString());
        Assert.Equal(2, evaluated.GetProperty("byBranch").GetArrayLength());

        // A sales figure corrected afterwards changes what the evaluator says now, and not what was decided on.
        var sale = (await (await branchManager.GetAsync($"{Sales}?recipeId={pilot.RecipeId}&size=1")).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("items")[0];
        await (await branchManager.PutAsJsonAsync($"{Sales}/{sale.Id()}", new { cupsSold = 1 })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.NotEqual(47.5m, (await factory.EvaluationAsync(pilot.PilotId)).GetProperty("criteria")[0].GetProperty("actual").GetDecimal());
        Assert.Equal(47.5m, (await factory.GetPilotAsync(pilot.PilotId)).GetProperty("decision").GetProperty("evaluated")
            .GetProperty("criteria")[0].GetProperty("actual").GetDecimal());

        // It is taken once.
        await (await factory.DecideAsync(pilot.PilotId, "ROLLOUT")).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-27");
        Assert.Equal("REVISE", (await factory.GetPilotAsync(pilot.PilotId)).GetProperty("decision").GetProperty("decision").GetString());
        // REVISE leaves the branches selling what they sell.
        Assert.All(await factory.LaunchStatusesAsync(pilot.RecipeId), row => Assert.Equal("LIVE", row.GetProperty("status").GetString()));
        var entry = JsonSerializer.Deserialize<JsonElement>((await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "PilotProgram" && a.EntityId == pilot.PilotId && a.Action == "DECIDE").Select(a => a.PayloadJson).SingleAsync()))!);
        Assert.Equal(("REVISE", "Sells, but the second week was flat"), (entry.GetProperty("decision").GetString(), entry.GetProperty("note").GetString()));
    }

    // ---------------------------------------------------------------- UC-25

    [Fact]
    public async Task Rollout_plans_the_drink_at_the_other_branches_which_then_go_live_through_the_same_gate()
    {
        var pilot = await factory.NewRunningPilotAsync(["B01"], [Absolute(40)]);
        await factory.TimePassesAsync(pilot);
        await factory.SellAsync(pilot.DrinkAt("B01"), Today.AddDays(-14), Today.AddDays(-1), 60);
        var b02 = await factory.BranchIdAsync("B02");
        Assert.Single(await factory.LaunchStatusesAsync(pilot.RecipeId)); // only the pilot branch so far

        await (await factory.DecideAsync(pilot.PilotId, "ROLLOUT")).ShouldBeAsync(HttpStatusCode.OK);

        // Every other active branch now prepares for it; the pilot branch stays live.
        var statuses = (await factory.LaunchStatusesAsync(pilot.RecipeId)).ToDictionary(row => row.GetProperty("branchCode").GetString()!);
        Assert.Equal("LIVE", statuses["B01"].GetProperty("status").GetString());
        Assert.Equal(("PREPARING", pilot.VersionId, 2), (statuses["B02"].GetProperty("status").GetString(),
            statuses["B02"].GetProperty("recipeVersionId").GetInt64(), statuses["B02"].GetProperty("minCertifiedStaff").GetInt32()));
        Assert.Equal("PREPARING", statuses["B03"].GetProperty("status").GetString());

        // Rolled out is not live: the gate still stands in front of each branch (BR-23).
        await (await factory.GoLiveAsync(pilot.PilotId, b02)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-23");
        await factory.CertifyStaffAsync(pilot, "B02");
        await (await factory.GoLiveAsync(pilot.PilotId, b02)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(("LIVE", true), ((await factory.LaunchStatusAsync(pilot.RecipeId, "B02")).GetProperty("status").GetString(),
            (await factory.LaunchStatusAsync(pilot.RecipeId, "B02")).GetProperty("coverageMet").GetBoolean()));
        await factory.RecordSaleAsync(pilot.DrinkAt("B01") with { BranchId = b02, BranchCode = "B02" }, Today, 33);
    }

    [Fact]
    public async Task Discontinue_withdraws_the_drink_from_the_pilot_branches()
    {
        var pilot = await factory.NewFinishedPilotAsync([Absolute(40)], cupsAtB01: 12, cupsAtB02: 9);

        await (await factory.DecideAsync(pilot.PilotId, "DISCONTINUE")).ShouldBeAsync(HttpStatusCode.OK);

        var statuses = await factory.LaunchStatusesAsync(pilot.RecipeId);
        Assert.Equal(2, statuses.Count); // no other branch was ever given it
        Assert.All(statuses, row => Assert.Equal("WITHDRAWN", row.GetProperty("status").GetString()));
        // The days of the pilot stay recorded, and the withdrawal is on the trail for BR-24.
        Assert.Equal(28, await factory.WithDbAsync(db => db.SalesRecords.CountAsync(s => s.RecipeId == pilot.RecipeId)));
        var launchIds = statuses.Select(row => row.Id()).ToList();
        Assert.Equal(2, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a =>
            a.EntityType == "BranchLaunchStatus" && a.Action == "WITHDRAW" && launchIds.Contains(a.EntityId))));
    }

    [Fact]
    public async Task Live_drink_is_withdrawn_at_a_branch_by_the_rd_manager()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewPilotAsync(["B01", "B02"]);
        await factory.CertifyStaffAsync(pilot, "B01");
        await factory.StartPilotAsync(pilot.PilotId);
        await (await factory.GoLiveAsync(pilot.PilotId, pilot.BranchIds[0])).ShouldBeAsync(HttpStatusCode.OK);
        var live = await factory.LaunchStatusAsync(pilot.RecipeId, "B01");
        var preparing = await factory.LaunchStatusAsync(pilot.RecipeId, "B02");

        var withdrawn = await (await manager.PostAsync($"{LaunchStatuses}/{live.Id()}/withdraw", null)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("WITHDRAWN", withdrawn.GetProperty("status").GetString());
        // Only a LIVE drink can be withdrawn.
        await (await manager.PostAsync($"{LaunchStatuses}/{live.Id()}/withdraw", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await manager.PostAsync($"{LaunchStatuses}/{preparing.Id()}/withdraw", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await manager.PostAsync($"{LaunchStatuses}/999999999/withdraw", null)).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        Assert.Equal(["WITHDRAWN"], (await (await manager.GetAsync($"{LaunchStatuses}?recipeId={pilot.RecipeId}&status=WITHDRAWN"))
            .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray().Select(row => row.GetProperty("status").GetString()));
    }

    // ---------------------------------------------------------------- branch scope

    [Fact]
    public async Task Branch_manager_sees_the_pilots_and_launch_statuses_of_their_own_branch()
    {
        var both = await factory.NewPilotAsync(["B01", "B02"]);
        var onlyB02 = await factory.NewPilotAsync(["B02"]);
        await factory.StartPilotAsync(both.PilotId);
        using var managerB01 = await factory.ClientForAsync(TestUsers.BranchManager);

        // Of a pilot their branch takes part in, they see their own branch.
        var seen = await (await managerB01.GetAsync(both.Url)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(["B01"], seen.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchCode").GetString()));
        // A pilot elsewhere does not exist for them.
        await (await managerB01.GetAsync(onlyB02.Url)).ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        var statuses = await factory.LaunchStatusesAsync(both.RecipeId, TestUsers.BranchManager);
        Assert.Equal(["B01"], statuses.Select(row => row.GetProperty("branchCode").GetString()));
        Assert.Equal(["B01", "B02"], (await factory.LaunchStatusesAsync(both.RecipeId)).Select(row => row.GetProperty("branchCode").GetString()));
        // Even when asking for another branch by id.
        Assert.Empty((await (await managerB01.GetAsync($"{LaunchStatuses}?branchId={both.BranchIds[1]}")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
    }
}
