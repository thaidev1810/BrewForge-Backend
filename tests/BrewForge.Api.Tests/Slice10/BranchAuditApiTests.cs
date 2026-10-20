using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice10;

/// <summary>The Quality Auditor's branch audits and the corrective actions they raise.</summary>
[Collection(ApiCollection.Name)]
public sealed class BranchAuditApiTests(BrewForgeApiFactory factory)
{
    private const string Audits = "/api/v1/branch-audits";
    private const string Actions = "/api/v1/corrective-actions";

    private Task<long> ReleasedVersionAsync() =>
        factory.WithDbAsync(db => (from v in db.RecipeVersions
                                   join r in db.Recipes on v.RecipeId equals r.Id
                                   where r.RecipeCode == "R05" && v.State == VersionState.Released
                                   select v.Id).SingleAsync());

    private static object Item(string criterion, bool passed, bool critical = false) =>
        new { criterion, expected = "15ml", actual = passed ? "15ml" : "20ml", passed, critical };

    private async Task<(long Branch, long Version)> ArrangeAsync() =>
        (await factory.BranchIdAsync("B01"), await ReleasedVersionAsync());

    [Fact]
    public async Task Audit_with_every_item_passed_is_PASSED_at_100_and_raises_no_action()
    {
        var (branch, version) = await ArrangeAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        var response = await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = version,
            items = new[] { Item("Tea quantity", true), Item("Sugar quantity", true) },
        });

        var audit = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(100, audit.GetProperty("score").GetInt32());
        Assert.Equal("PASSED", audit.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("correctiveActionId").ValueKind);
        Assert.Equal(2, audit.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Failed_audit_scores_the_checklist_and_raises_one_corrective_action()
    {
        var (branch, version) = await ArrangeAsync();
        var trainee = await factory.UserIdAsync(TestUsers.Trainee);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        var response = await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = version, retrainingRequired = true, employeeIds = new[] { trainee },
            items = new[]
            {
                Item("Ingredient expiry", true), Item("Tea quantity", true), Item("Sugar quantity", false),
                Item("Shaking", false), Item("Presentation", true),
            },
        });

        var audit = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(60, audit.GetProperty("score").GetInt32());
        Assert.Equal("FAILED", audit.GetProperty("result").GetString());
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        var action = await (await auditor.GetAsync($"{Actions}/{actionId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("OPEN", action.GetProperty("status").GetString());
        Assert.True(action.GetProperty("retrainingRequired").GetBoolean());
        Assert.Equal(trainee, action.GetProperty("employeeIds")[0].GetInt64());
        Assert.Contains("Sugar quantity", action.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_failed_critical_item_fails_the_audit_whatever_the_score()
    {
        var (branch, version) = await ArrangeAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var items = Enumerable.Range(1, 19).Select(i => Item($"Item {i}", true)).Append(Item("Expiry", false, critical: true));

        var audit = await (await auditor.PostAsJsonAsync(Audits, new { branchId = branch, recipeVersionId = version, items }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(95, audit.GetProperty("score").GetInt32());
        Assert.Equal("FAILED", audit.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Corrective_action_goes_open_to_in_progress_to_closed_and_closed_is_final()
    {
        var (branch, version) = await ArrangeAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = version, items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var id = audit.GetProperty("correctiveActionId").GetInt64();

        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var started = await (await manager.PostAsync($"{Actions}/{id}/start", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("IN_PROGRESS", started.GetProperty("status").GetString());
        await (await manager.PostAsync($"{Actions}/{id}/start", null)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-37");

        var closed = await (await auditor.PostAsJsonAsync($"{Actions}/{id}/close", new { note = "Retrained" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        await (await auditor.PostAsJsonAsync($"{Actions}/{id}/close", new { })).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-37");
    }

    [Fact]
    public async Task Audit_needs_items_and_a_released_version()
    {
        var (branch, version) = await ArrangeAsync();
        var draft = await factory.WithDbAsync(db => db.RecipeVersions.Where(v => v.State != VersionState.Released)
            .Select(v => v.Id).FirstAsync());
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        var empty = await auditor.PostAsJsonAsync(Audits, new { branchId = branch, recipeVersionId = version, items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var notReleased = await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = draft, items = new[] { Item("Tea quantity", true) },
        });
        await notReleased.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-38");
    }

    [Fact]
    public async Task Retraining_names_only_active_staff_of_the_audited_branch()
    {
        var (branch, version) = await ArrangeAsync();
        var otherBranchTrainee = await factory.UserIdAsync("trainee3");
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        var response = await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = version, retrainingRequired = true,
            employeeIds = new[] { otherBranchTrainee }, items = new[] { Item("Sugar quantity", false) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Branch_manager_sees_only_audits_of_their_own_branch()
    {
        var (b01, version) = await ArrangeAsync();
        var b02 = await factory.BranchIdAsync("B02");
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var own = await (await auditor.PostAsJsonAsync(Audits, new { branchId = b01, recipeVersionId = version, items = new[] { Item("Tea", true) } }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var other = await (await auditor.PostAsJsonAsync(Audits, new { branchId = b02, recipeVersionId = version, items = new[] { Item("Tea", true) } }))
            .ShouldBeAsync(HttpStatusCode.Created);

        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var page = await (await manager.GetAsync($"{Audits}?size=100")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.All(page.GetProperty("items").EnumerateArray(), a => Assert.Equal(b01, a.GetProperty("branchId").GetInt64()));
        Assert.Contains(page.GetProperty("items").EnumerateArray(), a => a.GetProperty("id").GetInt64() == own.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{Audits}/{other.GetProperty("id").GetInt64()}")).StatusCode);
    }

    [Fact]
    public async Task Summary_counts_the_month_and_names_the_lowest_branch()
    {
        var (branch, version) = await ArrangeAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        await (await auditor.PostAsJsonAsync(Audits, new { branchId = branch, recipeVersionId = version, items = new[] { Item("Tea", false) } }))
            .ShouldBeAsync(HttpStatusCode.Created);

        var summary = await (await auditor.GetAsync($"{Audits}/summary")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(summary.GetProperty("audits").GetInt32() >= 1);
        Assert.True(summary.GetProperty("failed").GetInt32() >= 1);
        Assert.True(summary.GetProperty("openCorrectiveActions").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Object, summary.GetProperty("lowestCompliance").ValueKind);
    }

    [Fact]
    public async Task Evidence_is_readable_only_within_the_branch_of_its_audit()
    {
        var version = await ReleasedVersionAsync();
        var b02 = await factory.BranchIdAsync("B02");
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        // Upload a photo and attach it to an audit of B02.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "sugar.png" } };
        var stored = await (await auditor.PostAsync($"{Audits}/evidence", form)).ShouldBeAsync(HttpStatusCode.Created);
        var url = stored.GetProperty("url").GetString()!;
        await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = b02, recipeVersionId = version,
            items = new[] { new { criterion = "Sugar quantity", passed = false, evidenceUrl = url } },
        })).ShouldBeAsync(HttpStatusCode.Created);

        // Head office and the B02 manager may read it; the B01 manager may not.
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(url)).StatusCode);
        using var b02Manager = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        Assert.Equal(HttpStatusCode.OK, (await b02Manager.GetAsync(url)).StatusCode);
        using var b01Manager = await factory.ClientForAsync(TestUsers.BranchManager);
        Assert.Equal(HttpStatusCode.NotFound, (await b01Manager.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Retraining_options_list_the_courses_of_the_audited_sop_and_the_named_staff()
    {
        var (branch, version) = await ArrangeAsync();
        var trainee = await factory.UserIdAsync(TestUsers.Trainee);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        // A failed audit raises a corrective action that requires retraining a named trainee.
        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = version, retrainingRequired = true, employeeIds = new[] { trainee },
            items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        // A published course bound to the audited SOP version is an option; the trainee is named.
        var course = await factory.NewPublishedCourseAsync(version);
        var options = await (await auditor.GetAsync($"{Actions}/{actionId}/retraining-options"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(version, options.GetProperty("auditedRecipeVersionId").GetInt64());
        Assert.Contains(trainee, options.GetProperty("employeeIds").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Contains(options.GetProperty("courses").EnumerateArray(),
            c => c.GetProperty("courseId").GetInt64() == course.Id());
    }

    [Fact]
    public async Task Retraining_can_only_be_assigned_on_a_course_tied_to_the_audited_sop()
    {
        var branch = await factory.BranchIdAsync("B01");
        var trainee = await factory.UserIdAsync(TestUsers.Trainee);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        // A fresh released SOP with its published course, and an unrelated published course on another recipe.
        var sop = await factory.NewPublishedCourseAsync();
        var sopVersion = sop.GetProperty("recipeVersionId").GetInt64();
        var sopCourse = sop.Id();
        var otherCourse = (await factory.NewPublishedCourseAsync()).Id();

        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sopVersion, retrainingRequired = true, employeeIds = new[] { trainee },
            items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        // A course on a different recipe is refused server-side, even when sent directly.
        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = otherCourse }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_COURSE_NOT_ALLOWED");

        // The course tied to the audited SOP is accepted.
        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = sopCourse }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Single(result.GetProperty("enrollmentIds").EnumerateArray());
    }
}
