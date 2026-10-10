using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Scheduling;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice5;

/// <summary>
/// The learning path of a new member of staff: put on what the regulation
/// makes mandatory for their role when they are created, held back from a
/// course whose prerequisite they have not passed, moved on to the next
/// stage when they pass it, and reminded when a due date comes close.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LearningPathApiTests(BrewForgeApiFactory factory)
{
    private const string Paths = "/api/v1/learning-paths";

    private sealed record NewStaff(string Username, long Id);

    [Fact]
    public async Task New_member_of_staff_is_put_on_the_induction_course_and_held_back_from_the_product_courses()
    {
        var drink = await TaughtDrinkAtB03Async();
        var induction = await InductionCourseIdAsync();

        var staff = await NewTraineeAsync("B03");

        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var path = await (await trainer.GetAsync($"{Paths}/{staff.Id}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((staff.Id, staff.Username, "TRAINEE"), (path.GetProperty("userId").GetInt64(),
            path.GetProperty("username").GetString(), path.GetProperty("role").GetString()));

        // Induction first, with nothing before it: assigned by itself, due by the regulation.
        var stages = path.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["INDUCTION", "PRODUCT"], stages.Select(stage => stage.GetProperty("courseType").GetString()));
        Assert.Equal((false, JsonValueKind.Null), (stages[0].GetProperty("blocked").GetBoolean(), stages[0].GetProperty("prerequisiteType").ValueKind));
        var welcome = CourseOf(stages[0], induction);
        Assert.Equal(("ENROLLED", "ASSIGNED", Today.AddDays(stages[0].GetProperty("dueDays").GetInt32()).ToString("yyyy-MM-dd")),
            (welcome.GetProperty("status").GetString(), welcome.GetProperty("enrollmentState").GetString(), welcome.GetProperty("dueDate").GetString()));

        // The drinks of their branch come after induction: shown, and not open yet.
        Assert.Equal((true, "INDUCTION"), (stages[1].GetProperty("blocked").GetBoolean(), stages[1].GetProperty("prerequisiteType").GetString()));
        var product = CourseOf(stages[1], drink.CourseId);
        Assert.Equal(("BLOCKED", JsonValueKind.Null), (product.GetProperty("status").GetString(), product.GetProperty("enrollmentId").ValueKind));

        // They were told, and the assignment is on record as the path's doing.
        var enrollmentId = welcome.GetProperty("enrollmentId").GetInt64();
        Assert.True(await factory.WithDbAsync(db => db.Notifications.AnyAsync(n => n.UserId == staff.Id && n.Subject == "Course assigned")));
        var assigned = await factory.WithDbAsync(db => db.AuditLogs.SingleAsync(a =>
            a.EntityType == "Enrollment" && a.EntityId == enrollmentId && a.Action == "ASSIGN"));
        Assert.Contains("LEARNING_PATH", assigned.PayloadJson);

        // The same path, as they see it themselves.
        using var self = await factory.ClientForAsync(staff.Username);
        var mine = await (await self.GetAsync("/api/v1/me/learning-path")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(path.GetRawText(), mine.GetRawText());
        Assert.Contains(enrollmentId, (await (await self.GetAsync("/api/v1/me/enrollments")).ShouldBeAsync(HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.Id()));
    }

    [Fact]
    public async Task Course_is_not_opened_to_somebody_who_has_not_passed_its_prerequisite()
    {
        var staff = await NewTraineeAsync("B01");
        var seeded = await factory.UserIdAsync(TestUsers.Trainee);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        // Named for a PRODUCT class before induction: refused by name, and nobody is enrolled.
        var named = await factory.NewClassAsync();
        var refusal = await (await trainer.PostAsJsonAsync($"{Classes}/{named.ClassId}/open", new { traineeIds = new[] { seeded, staff.Id } }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "PREREQUISITE");
        Assert.Contains("traineeIds", refusal.DetailFields());
        Assert.Contains($"user {staff.Id} has not passed a INDUCTION course",
            refusal.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("issue").GetString()));
        Assert.False(await factory.WithDbAsync(db => db.Enrollments.AnyAsync(e => e.TrainingClassId == named.ClassId)));

        // A class for the whole branch takes those who have, and leaves the newcomer out.
        var branch = await factory.NewClassAsync();
        await factory.OpenClassAsync(branch.ClassId);
        var enrolled = await factory.WithDbAsync(db => db.Enrollments.Where(e => e.TrainingClassId == branch.ClassId)
            .Select(e => e.UserId).ToListAsync());
        Assert.Contains(seeded, enrolled);
        Assert.DoesNotContain(staff.Id, enrolled);
    }

    [Fact]
    public async Task Passing_induction_opens_the_product_courses_of_the_branch()
    {
        var drink = await TaughtDrinkAtB03Async();
        var staff = await NewTraineeAsync("B03");
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        await PassInductionAsync(staff);

        var stages = (await (await trainer.GetAsync($"{Paths}/{staff.Id}")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal("CERTIFIED", CourseOf(stages[0], await InductionCourseIdAsync()).GetProperty("status").GetString());

        // The next stage opened by itself: the course of the drink their branch sells is theirs to take, self-paced.
        Assert.False(stages[1].GetProperty("blocked").GetBoolean());
        var product = CourseOf(stages[1], drink.CourseId);
        Assert.Equal(("ENROLLED", Today.AddDays(stages[1].GetProperty("dueDays").GetInt32()).ToString("yyyy-MM-dd")),
            (product.GetProperty("status").GetString(), product.GetProperty("dueDate").GetString()));
        Assert.Null(await factory.WithDbAsync(db => db.Enrollments.Where(e => e.Id == product.GetProperty("enrollmentId").GetInt64())
            .Select(e => e.TrainingClassId).SingleAsync()));

        // And a class of another PRODUCT course takes them now.
        var other = await factory.NewClassAsync(branchCode: "B03");
        await (await trainer.PostAsJsonAsync($"{Classes}/{other.ClassId}/open", new { traineeIds = new[] { staff.Id } }))
            .ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Course_published_after_somebody_joined_is_assigned_on_request()
    {
        var staff = await NewTraineeAsync("B03");
        var later = await NewPublishedInductionCourseAsync();
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);

        // Publishing a course assigns it to nobody: it is on the path, open and not taken.
        var before = (await (await manager.GetAsync($"{Paths}/{staff.Id}")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("stages").EnumerateArray().First();
        Assert.Equal("AVAILABLE", CourseOf(before, later).GetProperty("status").GetString());

        var assigned = await (await manager.PostAsync($"{Paths}/{staff.Id}/assign", null)).ShouldBeAsync(HttpStatusCode.OK);

        var enrollmentId = Assert.Single(assigned.GetProperty("enrollmentIds").EnumerateArray()).GetInt64();
        var after = assigned.GetProperty("path").GetProperty("stages").EnumerateArray().First();
        Assert.Equal(("ENROLLED", enrollmentId), (CourseOf(after, later).GetProperty("status").GetString(),
            CourseOf(after, later).GetProperty("enrollmentId").GetInt64()));

        // Asking again assigns nothing twice.
        var again = await (await manager.PostAsync($"{Paths}/{staff.Id}/assign", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(again.GetProperty("enrollmentIds").EnumerateArray());
    }

    [Fact]
    public async Task Path_of_a_role_with_nothing_mandatory_is_empty_and_an_unknown_user_has_none()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var own = await (await trainer.GetAsync("/api/v1/me/learning-path")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(own.GetProperty("stages").EnumerateArray());
        await (await trainer.GetAsync($"{Paths}/999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- helpers

    private static JsonElement CourseOf(JsonElement stage, long courseId) =>
        stage.GetProperty("courses").EnumerateArray().Single(course => course.GetProperty("courseId").GetInt64() == courseId);

    private Task<long> InductionCourseIdAsync() =>
        factory.WithDbAsync(db => db.Courses.Where(c => c.Title == DataSeeder.InductionTitle).Select(c => c.Id).SingleAsync());

    /// <summary>A drink with a published course that is on sale at B03, where the new staff of these tests work.</summary>
    private async Task<TaughtDrink> TaughtDrinkAtB03Async()
    {
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 0);
        await factory.GoLiveAsync("B03", drink.RecipeId, drink.VersionId);
        return drink;
    }

    /// <summary>A trainee who has just joined a branch, created by the admin as any user is.</summary>
    private async Task<NewStaff> NewTraineeAsync(string branchCode)
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = $"n{Guid.NewGuid():N}"[..12];
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username,
            email = $"{username}@brewforge.local",
            password = BrewForgeApiFactory.SeedPassword,
            fullName = "New Barista",
            role = "TRAINEE",
            branchId = await factory.BranchIdAsync(branchCode),
        })).ShouldBeAsync(HttpStatusCode.Created);
        return new NewStaff(username, created.Id());
    }

    private Task DueInAsync(long enrollmentId, int days)
    {
        var dueDate = Today.AddDays(days);
        return factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE enrollment SET due_date = {dueDate} WHERE id = {enrollmentId}"));
    }

    /// <summary>The new member of staff takes the seeded induction course, self-paced, and is certified on it.</summary>
    private async Task PassInductionAsync(NewStaff staff)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.GetCourseAsync(await InductionCourseIdAsync());
        var enrollmentId = await factory.WithDbAsync(db => db.Enrollments
            .Where(e => e.UserId == staff.Id && e.CourseId == course.Id()).Select(e => e.Id).SingleAsync());
        var moduleIds = course.GetProperty("modules").EnumerateArray().Select(m => m.Id()).ToList();
        var learner = new EligibleLearner(new ClassSetup(course.Id(), RecipeVersionId: 0, ClassId: 0, [], moduleIds),
            staff.Username, staff.Id, enrollmentId);

        var studied = await factory.CompleteModulesAsync(staff.Username, enrollmentId, moduleIds);
        Assert.Equal("ELIGIBLE", studied.GetProperty("state").GetString());
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        // No class, so no trainer of a class: any trainer runs the practical of a self-paced enrolment.
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();
        var evaluation = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new
        {
            practicalVideoId,
            items = course.GetProperty("practicalChecklist").EnumerateArray()
                .Select(item => new { lessonId = item.GetProperty("lessonId").GetInt64(), passed = true }),
        })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PASSED", evaluation.GetProperty("enrollmentState").GetString());
    }

    /// <summary>Another induction course, written and published the way a trainer would.</summary>
    private async Task<long> NewPublishedInductionCourseAsync()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var course = await (await trainer.PostAsJsonAsync(Courses,
                new { courseType = "INDUCTION", title = $"Induction {Guid.NewGuid():N}"[..20] }))
            .ShouldBeAsync(HttpStatusCode.Created);
        foreach (var module in course.GetProperty("modules").EnumerateArray())
        {
            await (await trainer.PostAsJsonAsync($"{Modules}/{module.Id()}/lessons",
                new { title = "What a new colleague needs to know", content = "Shown by the trainer on the first day." }))
                .ShouldBeAsync(HttpStatusCode.Created);
            await (await trainer.PutAsJsonAsync($"{Modules}/{module.Id()}", new { durationMinutes = 10 })).ShouldBeAsync(HttpStatusCode.OK);
            await factory.AddQuestionAsync(course.Id(), module.Id());
        }
        await (await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await manager.PostAsync($"{Courses}/{course.Id()}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);
        return course.Id();
    }
}
