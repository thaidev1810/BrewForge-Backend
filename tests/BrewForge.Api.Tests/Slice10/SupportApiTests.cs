using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice10;

/// <summary>Products, question bank, direct assignment, practical schedule, skills and settings.</summary>
[Collection(ApiCollection.Name)]
public sealed class SupportApiTests(BrewForgeApiFactory factory)
{
    private static string Unique() => $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    // ---------------------------------------------------------------- products

    [Fact]
    public async Task Product_shows_the_released_version_of_its_recipe()
    {
        var recipeId = await factory.WithDbAsync(db => db.Recipes.Where(r => r.RecipeCode == "R05").Select(r => r.Id).SingleAsync());
        var drink = await factory.WithDbAsync(db => db.Categories.Where(c => c.Kind == BrewForge.Domain.MasterData.CategoryKind.Drink)
            .Select(c => c.Id).FirstAsync());
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var created = await (await specialist.PostAsJsonAsync("/api/v1/products", new
        {
            productCode = Unique(), name = "Espresso", categoryId = drink, recipeId,
        })).ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());
        Assert.True(created.GetProperty("currentVersionId").GetInt64() > 0);
        Assert.Equal("R05", created.GetProperty("recipeCode").GetString());
    }

    [Fact]
    public async Task Product_category_must_be_an_active_drink_category()
    {
        var ingredientCategory = await factory.WithDbAsync(db => db.Categories
            .Where(c => c.Kind == BrewForge.Domain.MasterData.CategoryKind.Ingredient).Select(c => c.Id).FirstAsync());
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var response = await specialist.PostAsJsonAsync("/api/v1/products", new
        {
            productCode = Unique(), name = "Wrong", categoryId = ingredientCategory,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- question bank

    [Fact]
    public async Task Question_bank_validates_types_and_the_answers()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var trueFalse = await (await trainer.PostAsJsonAsync("/api/v1/question-bank", new
        {
            questionType = "TRUE_FALSE", questionText = "Shake for 10 seconds?", correctOptions = new[] { "TRUE" },
        })).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(2, trueFalse.GetProperty("options").GetArrayLength());

        var twoAnswers = await trainer.PostAsJsonAsync("/api/v1/question-bank", new
        {
            questionType = "SINGLE_CHOICE", questionText = "Preset for 15ml?",
            options = new[] { new { key = "A", text = "S10" }, new { key = "B", text = "S15" } },
            correctOptions = new[] { "A", "B" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, twoAnswers.StatusCode);

        var multiple = await (await trainer.PostAsJsonAsync("/api/v1/question-bank", new
        {
            questionType = "MULTIPLE_CHOICE", questionText = "Which are sweet?",
            options = new[] { new { key = "A", text = "Syrup" }, new { key = "B", text = "Ice" }, new { key = "C", text = "Sugar" } },
            correctOptions = new[] { "A", "C" },
        })).ShouldBeAsync(HttpStatusCode.Created);

        var id = multiple.GetProperty("id").GetInt64();
        Assert.Equal(2, multiple.GetProperty("correctOptions").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await trainer.DeleteAsync($"/api/v1/question-bank/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await trainer.GetAsync($"/api/v1/question-bank/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_multiple_choice_question_cannot_be_copied_into_a_quiz()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var question = await (await trainer.PostAsJsonAsync("/api/v1/question-bank", new
        {
            questionType = "MULTIPLE_CHOICE", questionText = "Pick two", moduleType = "SOP",
            options = new[] { new { key = "A", text = "x" }, new { key = "B", text = "y" } }, correctOptions = new[] { "A", "B" },
        })).ShouldBeAsync(HttpStatusCode.Created);

        var response = await trainer.PostAsync($"/api/v1/question-bank/{question.GetProperty("id").GetInt64()}/add-to-course/1", null);

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- assignments, schedule, skills

    [Fact]
    public async Task Trainer_assigns_a_published_course_with_a_deadline_and_a_reason()
    {
        var course = await factory.NewPublishedCourseAsync();
        var branch = await factory.BranchIdAsync("B01");
        var trainee = await factory.UserIdAsync(TestUsers.Trainee);
        var deadline = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var result = await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
        {
            courseId = course.Id(), branchId = branch, employeeIds = new[] { trainee }, deadline, reason = "New SOP version",
        })).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Single(result.GetProperty("enrollmentIds").EnumerateArray());

        var list = await (await trainer.GetAsync($"/api/v1/assignments?courseId={course.Id()}")).ShouldBeAsync(HttpStatusCode.OK);
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("ASSIGNED", row.GetProperty("stage").GetString());
        Assert.Equal(deadline.ToString("yyyy-MM-dd"), row.GetProperty("dueDate").GetString());

        var again = await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
        {
            courseId = course.Id(), branchId = branch, employeeIds = new[] { trainee },
        })).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Empty(again.GetProperty("enrollmentIds").EnumerateArray());
        Assert.Single(again.GetProperty("alreadyEnrolled").EnumerateArray());
    }

    [Fact]
    public async Task Assignment_deadline_cannot_be_in_the_past_and_staff_must_belong_to_the_branch()
    {
        var course = await factory.NewPublishedCourseAsync();
        var b01 = await factory.BranchIdAsync("B01");
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var past = await trainer.PostAsJsonAsync("/api/v1/assignments", new
        {
            courseId = course.Id(), branchId = b01, allTrainees = true, deadline = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);

        var foreign = await trainer.PostAsJsonAsync("/api/v1/assignments", new
        {
            courseId = course.Id(), branchId = b01, employeeIds = new[] { await factory.UserIdAsync("trainee3") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Fact]
    public async Task Assignments_filtered_by_status_still_honour_the_requested_sort()
    {
        var course = await factory.NewPublishedCourseAsync();
        var b01 = await factory.BranchIdAsync("B01");
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        // Two fresh B01 trainees, assigned the same course with different deadlines, both ASSIGNED.
        var near = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5);
        var far = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(15);
        var t1 = await NewTraineeAsync(admin, b01);
        var t2 = await NewTraineeAsync(admin, b01);
        await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
            { courseId = course.Id(), branchId = b01, employeeIds = new[] { t1 }, deadline = near }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
            { courseId = course.Id(), branchId = b01, employeeIds = new[] { t2 }, deadline = far }))
            .ShouldBeAsync(HttpStatusCode.Created);

        var baseUrl = $"/api/v1/assignments?courseId={course.Id()}&stage=ASSIGNED";
        var asc = await DueDatesAsync(trainer, $"{baseUrl}&sort=dueDate,asc");
        var desc = await DueDatesAsync(trainer, $"{baseUrl}&sort=dueDate,desc");

        Assert.Equal(new[] { near.ToString("yyyy-MM-dd"), far.ToString("yyyy-MM-dd") }, asc);
        Assert.Equal(new[] { far.ToString("yyyy-MM-dd"), near.ToString("yyyy-MM-dd") }, desc);
    }

    private async Task<long> NewTraineeAsync(HttpClient admin, long branchId)
    {
        var name = $"t{Guid.NewGuid():N}"[..12];
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = name, email = $"{name}@brewforge.local", password = "Another-Test-Passw0rd",
            fullName = "List Trainee", role = "TRAINEE", branchId,
        })).ShouldBeAsync(HttpStatusCode.Created);
        return created.GetProperty("id").GetInt64();
    }

    private static async Task<string[]> DueDatesAsync(HttpClient client, string url)
    {
        var list = await (await client.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        return [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("dueDate").GetString()!)];
    }

    [Fact]
    public async Task Assignment_stage_follows_the_cycle_and_overdue_is_a_separate_flag()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var url = $"/api/v1/assignments?courseId={learner.Setup.CourseId}&size=100";

        async Task<JsonElement> RowAsync()
        {
            var list = await (await trainer.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
            return list.GetProperty("items").EnumerateArray()
                .Single(a => a.GetProperty("enrollmentId").GetInt64() == learner.EnrollmentId);
        }

        // Eligible, but the quiz is not passed yet.
        Assert.Equal("THEORY_COMPLETED", (await RowAsync()).GetProperty("stage").GetString());

        // The quiz is passed: the stage is QUIZ_PASSED while the enrolment stays ELIGIBLE underneath.
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var passed = await RowAsync();
        Assert.Equal("QUIZ_PASSED", passed.GetProperty("stage").GetString());
        Assert.Equal("ELIGIBLE", passed.GetProperty("enrollmentState").GetString());

        // A practical is booked: PRACTICAL_PENDING.
        await (await trainer.PostAsJsonAsync($"/api/v1/enrollments/{learner.EnrollmentId}/practical-schedules",
                new { scheduledAt = DateTimeOffset.UtcNow.AddDays(2), location = "B01 counter" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("PRACTICAL_PENDING", (await RowAsync()).GetProperty("stage").GetString());

        // Past the deadline: isOverdue is its own flag and does not hide the real stage.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE enrollment SET due_date = {0} WHERE id = {1}",
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), learner.EnrollmentId));
        var overdue = await RowAsync();
        Assert.True(overdue.GetProperty("isOverdue").GetBoolean());
        Assert.Equal("PRACTICAL_PENDING", overdue.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Practical_is_scheduled_only_after_the_quiz_and_the_trainee_can_read_it()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var at = DateTimeOffset.UtcNow.AddDays(2);
        var url = $"/api/v1/enrollments/{learner.EnrollmentId}/practical-schedules";

        await (await trainer.PostAsJsonAsync(url, new { scheduledAt = at, location = "B01 counter" }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-39");

        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var created = await (await trainer.PostAsJsonAsync(url, new { scheduledAt = at, location = "B01 counter" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("SCHEDULED", created.GetProperty("status").GetString());

        await (await trainer.PostAsJsonAsync(url, new { scheduledAt = at, location = "B01 counter" }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-39");

        using var trainee = await factory.ClientForAsync(learner.Username);
        var seen = await (await trainee.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("B01 counter", Assert.Single(seen.EnumerateArray()).GetProperty("location").GetString());

        var id = created.GetProperty("id").GetInt64();
        await (await trainer.PostAsync($"/api/v1/practical-schedules/{id}/cancel", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await trainer.PostAsync($"/api/v1/practical-schedules/{id}/cancel", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-39");
    }

    [Fact]
    public async Task Closing_the_enrolment_cancels_its_practical_and_tells_the_learner()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var id = await ScheduleAsync(learner);

        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        await (await manager.PostAsync($"/api/v1/enrollments/{learner.EnrollmentId}/close", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        await AssertCancelledAndToldAsync(learner, id);
    }

    [Fact]
    public async Task Cancelling_the_class_cancels_the_practicals_of_its_learners()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var id = await ScheduleAsync(learner);

        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        await (await trainer.PostAsync($"/api/v1/training-classes/{learner.Setup.ClassId}/cancel", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        await AssertCancelledAndToldAsync(learner, id);
        // The trainer's agenda no longer holds it as open.
        var agenda = await (await trainer.GetAsync("/api/v1/practical-schedules?status=SCHEDULED&size=100"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(agenda.GetProperty("items").EnumerateArray(), s => s.GetProperty("id").GetInt64() == id);
    }

    private async Task<long> ScheduleAsync(EligibleLearner learner)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var created = await (await trainer.PostAsJsonAsync($"/api/v1/enrollments/{learner.EnrollmentId}/practical-schedules",
            new { scheduledAt = DateTimeOffset.UtcNow.AddDays(2), location = "B01 counter" })).ShouldBeAsync(HttpStatusCode.Created);
        return created.GetProperty("id").GetInt64();
    }

    private async Task AssertCancelledAndToldAsync(EligibleLearner learner, long scheduleId)
    {
        var status = await factory.WithDbAsync(db =>
            db.PracticalSchedules.Where(s => s.Id == scheduleId).Select(s => s.Status).SingleAsync());
        Assert.Equal(BrewForge.Domain.Training.ScheduleStatus.Cancelled, status);

        using var trainee = await factory.ClientForAsync(learner.Username);
        var inbox = await (await trainee.GetAsync("/api/v1/me/notifications?size=50")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(),
            n => n.GetProperty("subject").GetString() == "Practical cancelled");
    }

    [Fact]
    public async Task Schedule_must_be_in_the_future()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var response = await trainer.PostAsJsonAsync($"/api/v1/enrollments/{learner.EnrollmentId}/practical-schedules",
            new { scheduledAt = DateTimeOffset.UtcNow.AddHours(-1), location = "B01" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_trainer_cannot_touch_another_trainers_appointment()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);

        using var owner = await factory.ClientForAsync(TestUsers.Trainer);
        var url = $"/api/v1/enrollments/{learner.EnrollmentId}/practical-schedules";
        var at = DateTimeOffset.UtcNow.AddDays(2);
        var created = await (await owner.PostAsJsonAsync(url, new { scheduledAt = at, location = "B01 counter" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();

        // A second trainer who does not hold this appointment.
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var otherName = $"t{Guid.NewGuid():N}"[..12];
        const string password = "Another-Test-Passw0rd";
        await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = otherName, email = $"{otherName}@brewforge.local", password, fullName = "Other Trainer",
            role = "TRAINER", branchId = (long?)null,
        })).ShouldBeAsync(HttpStatusCode.Created);
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Authorization =
            new("Bearer", (await factory.LoginAsync(otherName, password)).AccessToken);

        // The other trainer may call the endpoints (same role) but cannot reach this appointment.
        await (await other.PostAsync($"/api/v1/practical-schedules/{id}/cancel", null))
            .ShouldBeAsync(HttpStatusCode.NotFound);
        await (await other.PutAsJsonAsync($"/api/v1/practical-schedules/{id}", new { scheduledAt = at, location = "elsewhere" }))
            .ShouldBeAsync(HttpStatusCode.NotFound);
        await (await other.PostAsync($"/api/v1/practical-schedules/{id}/complete", null))
            .ShouldBeAsync(HttpStatusCode.NotFound);

        // The appointment is untouched, and its owner still holds it as SCHEDULED.
        var status = await factory.WithDbAsync(db =>
            db.PracticalSchedules.Where(s => s.Id == id).Select(s => s.Status).SingleAsync());
        Assert.Equal(BrewForge.Domain.Training.ScheduleStatus.Scheduled, status);
    }

    [Fact]
    public async Task Skill_passport_lists_every_published_product_course()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var skills = await (await trainee.GetAsync("/api/v1/me/skills")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.All(skills.EnumerateArray(), s => Assert.Contains(s.GetProperty("status").GetString(),
            new[] { "CERTIFIED", "NEEDS_RECERT", "TRAINING", "NOT_CERTIFIED" }));
    }

    [Fact]
    public async Task Branch_manager_sees_employees_of_their_own_branch_only()
    {
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var own = await factory.BranchIdAsync("B01");

        var rows = await (await manager.GetAsync("/api/v1/dashboards/employees")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.NotEmpty(rows.EnumerateArray());
        Assert.All(rows.EnumerateArray(), r => Assert.Equal(own, r.GetProperty("branchId").GetInt64()));
    }

    // ---------------------------------------------------------------- settings and evidence

    [Fact]
    public async Task Audit_pass_mark_is_a_setting_the_administrator_changes_and_validates()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/v1/settings/audit.pass_mark", new { value = "150" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/v1/settings/no.such.key", new { value = "1" })).StatusCode);

        var changed = await (await admin.PutAsJsonAsync("/api/v1/settings/audit.pass_mark", new { value = "50" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("50", changed.GetProperty("value").GetString());
        try
        {
            var version = await factory.WithDbAsync(db => (from v in db.RecipeVersions
                                                           join r in db.Recipes on v.RecipeId equals r.Id
                                                           where r.RecipeCode == "R05" && v.State == BrewForge.Domain.Recipes.VersionState.Released
                                                           select v.Id).SingleAsync());
            using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
            var audit = await (await auditor.PostAsJsonAsync("/api/v1/branch-audits", new
            {
                branchId = await factory.BranchIdAsync("B01"), recipeVersionId = version,
                items = new[] { new { criterion = "a", passed = true }, new { criterion = "b", passed = false } },
            })).ShouldBeAsync(HttpStatusCode.Created);
            Assert.Equal(50, audit.GetProperty("score").GetInt32());
            Assert.Equal("PASSED", audit.GetProperty("result").GetString());
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/settings/audit.pass_mark", new { value = "90" });
        }
    }

    [Fact]
    public async Task Evidence_photo_is_uploaded_and_read_back_and_other_files_are_refused()
    {
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

        using var good = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "sugar.png" } };
        var stored = await (await auditor.PostAsync("/api/v1/branch-audits/evidence", good)).ShouldBeAsync(HttpStatusCode.Created);

        var back = await auditor.GetAsync(stored.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(png, await back.Content.ReadAsByteArrayAsync());

        using var bad = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "script.exe" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await auditor.PostAsync("/api/v1/branch-audits/evidence", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await auditor.GetAsync("/api/v1/branch-audits/evidence/../../etc/passwd")).StatusCode);
    }
}
