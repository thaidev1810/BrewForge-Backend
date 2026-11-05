using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Recipes;
using BrewForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task Retraining_reports_each_employee_and_skips_an_inactive_one()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);

        async Task<long> NewB01TraineeAsync()
        {
            var name = $"t{Guid.NewGuid():N}"[..12];
            var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
            {
                username = name, email = $"{name}@brewforge.local", password = "Another-Test-Passw0rd",
                fullName = "Audit Trainee", role = "TRAINEE", branchId = branch,
            })).ShouldBeAsync(HttpStatusCode.Created);
            return created.GetProperty("id").GetInt64();
        }

        var keep = await NewB01TraineeAsync();
        var gone = await NewB01TraineeAsync();
        var sop = await factory.NewPublishedCourseAsync();
        var sopCourse = sop.Id();

        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sop.GetProperty("recipeVersionId").GetInt64(),
            retrainingRequired = true, employeeIds = new[] { keep, gone }, items = new[] { Item("Shaking", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        // One named employee is deactivated before retraining is assigned: it is reported, not dropped.
        await (await admin.PostAsync($"/api/v1/users/{gone}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);

        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = sopCourse }))
            .ShouldBeAsync(HttpStatusCode.OK);

        var results = result.GetProperty("results").EnumerateArray().ToList();
        var keepItem = results.Single(r => r.GetProperty("employeeId").GetInt64() == keep);
        var goneItem = results.Single(r => r.GetProperty("employeeId").GetInt64() == gone);
        Assert.Equal("ASSIGNED", keepItem.GetProperty("outcome").GetString());
        Assert.True(keepItem.GetProperty("enrollmentId").GetInt64() > 0);
        Assert.Equal("SKIPPED", goneItem.GetProperty("outcome").GetString());
        Assert.Equal("INACTIVE", goneItem.GetProperty("reasonCode").GetString());
        Assert.Single(result.GetProperty("enrollmentIds").EnumerateArray());   // only the active one
    }

    [Fact]
    public async Task G_19_an_action_requiring_retraining_closes_only_after_every_named_employee_has_passed()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();

        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sop.GetProperty("recipeVersionId").GetInt64(),
            retrainingRequired = true, employeeIds = new[] { trainee }, items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        // Retraining is required and nobody has passed it: the action cannot be closed.
        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");

        // It is assigned, and still cannot close while the enrolment is only in progress.
        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = sop.Id() }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var enrollmentId = result.GetProperty("enrollmentIds").EnumerateArray().Single().GetInt64();
        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");

        // Once the retraining is passed, the action closes.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE enrollment SET state = 'PASSED', completed_at = now() WHERE id = {enrollmentId}"));
        var closed = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { note = "Retrained" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task G_19_a_pass_from_before_the_failed_audit_is_not_retraining()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();

        // The trainee was trained and passed on this SOP last week.
        await factory.PassInductionAsync(trainee);
        var assigned = await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
            {
                courseId = sop.Id(), branchId = branch, employeeIds = new[] { trainee }, reason = "Onboarding",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var earlier = assigned.GetProperty("enrollmentIds").EnumerateArray().Single().GetInt64();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE enrollment SET state = 'PASSED', completed_at = now() - interval '7 days' WHERE id = {earlier}"));

        // Then an audit finds them preparing it wrong.
        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sop.GetProperty("recipeVersionId").GetInt64(),
            retrainingRequired = true, employeeIds = new[] { trainee }, items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();

        // The old pass is the training that did not hold: it does not close the action.
        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");

        // This trainee passed the induction, so they would join every whole-branch class of B01 opened
        // by later tests; they leave the branch's active staff here.
        await (await admin.PostAsync($"/api/v1/users/{trainee}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task G_19_a_retraining_passed_on_a_course_that_later_went_out_of_date_still_counts()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();

        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sop.GetProperty("recipeVersionId").GetInt64(),
            retrainingRequired = true, employeeIds = new[] { trainee }, items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        var actionId = audit.GetProperty("correctiveActionId").GetInt64();
        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = sop.Id() }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var enrollmentId = result.GetProperty("enrollmentIds").EnumerateArray().Single().GetInt64();

        // Retrained and passed; then a new release of the drink makes the course OUT_OF_DATE.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE enrollment SET state = 'PASSED', completed_at = now() WHERE id = {enrollmentId}"));
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE course SET state = 'OUT_OF_DATE' WHERE id = {sop.Id()}"));

        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { note = "Retrained" }))
            .ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task G_19_I_a_named_employee_who_left_holds_the_action_until_exempted_with_a_reason()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();
        var actionId = await NewRetrainingActionAsync(auditor, branch, sop, trainee);

        // The only named employee leaves. Leaving is not retraining: the action stays open, and says who holds it.
        await (await admin.PostAsync($"/api/v1/users/{trainee}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);
        var refused = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");
        Assert.Contains(Issues(refused), issue => issue == $"employee {trainee}: NOT_ASSIGNED");

        // An exemption needs a reason; with one, it is recorded and the duty no longer holds the close.
        Assert.Contains("reason", (await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/obligations/{trainee}/exempt", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        var exempted = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/obligations/{trainee}/exempt",
                new { reason = "Left the chain before retraining" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var duty = Assert.Single(exempted.GetProperty("obligations").EnumerateArray());
        Assert.Equal(("EXEMPTED", "Left the chain before retraining", true),
            (duty.GetProperty("status").GetString(), duty.GetProperty("exemptReason").GetString(), duty.GetProperty("completed").GetBoolean()));
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "CorrectiveAction" && a.EntityId == actionId && a.Action == "EXEMPT")));

        await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { })).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task G_19_A_to_D_someone_who_passed_before_gets_a_new_cycle_and_only_that_cycle_closes_the_action()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();
        var (courseId, versionId) = (sop.Id(), sop.GetProperty("recipeVersionId").GetInt64());

        // Trained, passed and certified on this SOP a week ago.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO enrollment (course_id, user_id, state, enrolled_at, completed_at) VALUES ({courseId}, {trainee}, 'PASSED', now() - interval '10 days', now() - interval '7 days')"));
        var earlier = await factory.WithDbAsync(db => db.Enrollments.IgnoreQueryFilters()
            .Where(e => e.CourseId == courseId && e.UserId == trainee).Select(e => e.Id).SingleAsync());
        await factory.CertifyAsync(trainee, courseId, versionId);
        var actionId = await NewRetrainingActionAsync(auditor, branch, sop, trainee);

        // A and C: the old pass and its valid certificate do not meet a duty raised after them.
        var refused = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");
        Assert.Contains(Issues(refused), issue => issue == $"employee {trainee}: NOT_ASSIGNED");

        // Assigning starts a new cycle on the same course; the passed one stays as history.
        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var item = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal("ASSIGNED", item.GetProperty("outcome").GetString());
        Assert.True(item.GetProperty("obligationId").GetInt64() > 0);
        var cycle = item.GetProperty("enrollmentId").GetInt64();
        Assert.NotEqual(earlier, cycle);
        // G: the new cycle starts from nothing - none of the old cycle's progress carries over.
        var fresh = await factory.WithDbAsync(db => db.Enrollments.AsNoTracking().Include(e => e.QuizAttempts)
            .SingleAsync(e => e.Id == cycle));
        Assert.Equal((Domain.Training.EnrollmentState.Assigned, 0, 0), (fresh.State, fresh.ProgressPercent, fresh.QuizAttempts.Count));
        Assert.Equal(Domain.Training.EnrollmentState.Passed, await factory.WithDbAsync(db =>
            db.Enrollments.IgnoreQueryFilters().Where(e => e.Id == earlier).Select(e => e.State).SingleAsync()));

        // B: assigned but not passed yet.
        refused = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");
        Assert.Contains(Issues(refused), issue => issue == $"employee {trainee}: NOT_PASSED");

        // D: the new cycle is passed - the action closes, and the duty says by which cycle.
        await PassAsync(cycle);
        var closed = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { note = "Retrained" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var duty = Assert.Single(closed.GetProperty("obligations").EnumerateArray());
        Assert.Equal(("ASSIGNED", cycle, true, "PASSED"), (duty.GetProperty("status").GetString(),
            duty.GetProperty("enrollmentId").GetInt64(), duty.GetProperty("completed").GetBoolean(),
            duty.GetProperty("enrollmentState").GetString()));
    }

    [Fact]
    public async Task G_19_E_one_of_two_employees_not_retrained_keeps_the_action_open()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var (first, second) = (await NewB01TraineeAsync(admin, branch), await NewB01TraineeAsync(admin, branch));
        var sop = await factory.NewPublishedCourseAsync();
        var actionId = await NewRetrainingActionAsync(auditor, branch, sop, first, second);

        var result = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/assign-retraining", new { courseId = sop.Id() }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var cycles = result.GetProperty("results").EnumerateArray()
            .ToDictionary(r => r.GetProperty("employeeId").GetInt64(), r => r.GetProperty("enrollmentId").GetInt64());
        await PassAsync(cycles[first]);

        var refused = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");
        Assert.Equal([$"employee {second}: NOT_PASSED"], Issues(refused));
    }

    [Fact]
    public async Task G_19_H_assigning_again_or_twice_at_once_makes_no_second_duty_or_cycle()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var (trainee, other) = (await NewB01TraineeAsync(admin, branch), await NewB01TraineeAsync(admin, branch));
        var sop = await factory.NewPublishedCourseAsync();
        var courseId = sop.Id();
        var actionId = await NewRetrainingActionAsync(auditor, branch, sop, trainee, other);
        var url = $"{Actions}/{actionId}/assign-retraining";

        // Twice at the same moment, then once more: one duty and one cycle per employee, the same each time.
        var responses = await Task.WhenAll(auditor.PostAsJsonAsync(url, new { courseId }), auditor.PostAsJsonAsync(url, new { courseId }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var again = await (await auditor.PostAsJsonAsync(url, new { courseId })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.All(again.GetProperty("results").EnumerateArray(),
            r => Assert.Equal("ALREADY_ENROLLED", r.GetProperty("outcome").GetString()));

        var (duties, active) = await factory.WithDbAsync(async db => (
            await db.RetrainingObligations.CountAsync(o => o.CorrectiveActionId == actionId),
            await db.Enrollments.IgnoreQueryFilters().CountAsync(e => e.CourseId == courseId
                && (e.UserId == trainee || e.UserId == other) && e.State != Domain.Training.EnrollmentState.Closed)));
        Assert.Equal((2, 2), (duties, active));
    }

    [Fact]
    public async Task G_19_an_action_from_before_duties_were_recorded_waits_for_reconciliation()
    {
        var branch = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var trainee = await NewB01TraineeAsync(admin, branch);
        var sop = await factory.NewPublishedCourseAsync();
        var actionId = await NewRetrainingActionAsync(auditor, branch, sop, trainee);

        // As if raised before migration 020: no duty recorded. Re-applying the schema backfills one, marked legacy.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"DELETE FROM retraining_obligation WHERE corrective_action_id = {actionId}"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().ApplySchemaAsync();
        }

        var action = await (await auditor.GetAsync($"{Actions}/{actionId}")).ShouldBeAsync(HttpStatusCode.OK);
        var duty = Assert.Single(action.GetProperty("obligations").EnumerateArray());
        Assert.Equal(("PENDING", true, false), (duty.GetProperty("status").GetString(), duty.GetProperty("legacy").GetBoolean(),
            duty.GetProperty("completed").GetBoolean()));
        var refused = await (await auditor.PostAsJsonAsync($"{Actions}/{actionId}/close", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "RETRAINING_INCOMPLETE");
        Assert.Contains(Issues(refused), issue => issue == $"employee {trainee}: LEGACY_PENDING");
    }

    private async Task<long> NewRetrainingActionAsync(HttpClient auditor, long branch, JsonElement sop, params long[] employees)
    {
        var audit = await (await auditor.PostAsJsonAsync(Audits, new
        {
            branchId = branch, recipeVersionId = sop.GetProperty("recipeVersionId").GetInt64(),
            retrainingRequired = true, employeeIds = employees, items = new[] { Item("Sugar quantity", false) },
        })).ShouldBeAsync(HttpStatusCode.Created);
        return audit.GetProperty("correctiveActionId").GetInt64();
    }

    /// <summary>The learner passes the cycle now, as the assessment would record it.</summary>
    private Task PassAsync(long enrollmentId) => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
        $"UPDATE enrollment SET state = 'PASSED', completed_at = now() WHERE id = {enrollmentId}"));

    private static List<string> Issues(JsonElement envelope) =>
        [.. envelope.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("issue").GetString()!)];

    private static async Task<long> NewB01TraineeAsync(HttpClient admin, long branch)
    {
        var name = $"t{Guid.NewGuid():N}"[..12];
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = name, email = $"{name}@brewforge.local", password = "Another-Test-Passw0rd",
            fullName = "Audit Trainee", role = "TRAINEE", branchId = branch,
        })).ShouldBeAsync(HttpStatusCode.Created);
        return created.GetProperty("id").GetInt64();
    }
}
