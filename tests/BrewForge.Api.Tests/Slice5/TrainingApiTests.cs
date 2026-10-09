using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice5;

/// <summary>
/// UC-27, UC-29, UC-30 and UC-14 through the API: the training regulation,
/// classes and sessions (BR-34), attendance, and the eligibility gate (BR-32).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TrainingApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- UC-29

    [Fact]
    public async Task Class_is_scheduled_opened_and_closed()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync(sessions: 1);

        var planned = await (await trainer.GetAsync($"{Classes}/{setup.ClassId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PLANNED", planned.GetProperty("state").GetString());
        Assert.Equal(0, planned.GetProperty("enrolledCount").GetInt32());

        var opened = await factory.OpenClassAsync(setup.ClassId);
        Assert.Equal("RUNNING", opened.GetProperty("class").GetProperty("state").GetString());
        // Every active trainee of branch B01.
        Assert.Equal(2, opened.GetProperty("enrollmentIds").GetArrayLength());
        Assert.Equal(2, opened.GetProperty("class").GetProperty("enrolledCount").GetInt32());

        var enrollment = await factory.GetEnrollmentAsync(await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee));
        Assert.Equal("ASSIGNED", enrollment.GetProperty("state").GetString());
        Assert.Equal(setup.CourseId, enrollment.GetProperty("courseId").GetInt64());
        Assert.Equal(setup.ClassId, enrollment.GetProperty("trainingClassId").GetInt64());
        // The deadline comes from the regulation: 14 days for a PRODUCT course.
        Assert.Equal(Today.AddDays(14).ToString("yyyy-MM-dd"), enrollment.GetProperty("dueDate").GetString());
        Assert.Equal(0, enrollment.GetProperty("progressPercent").GetInt32());

        // A class is opened once.
        await (await trainer.PostAsJsonAsync($"{Classes}/{setup.ClassId}/open", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        var closed = await (await trainer.PostAsync($"{Classes}/{setup.ClassId}/close", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CLOSED", closed.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Opening_a_class_notifies_each_trainee_it_enrols()
    {
        var setup = await factory.NewClassAsync();
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);

        await factory.OpenClassAsync(setup.ClassId);

        var notices = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "AppUser" && a.EntityId == traineeId && a.Action == "NOTIFY")
            .Select(a => a.PayloadJson).ToListAsync());
        Assert.Contains(notices, payload => payload!.Contains("Course assigned"));
    }

    [Fact]
    public async Task Session_has_the_shape_of_the_contract()
    {
        var setup = await factory.NewClassAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        await factory.CertifyAsync(trainerId, setup.CourseId, setup.RecipeVersionId);

        var session = await factory.AddSessionAsync(setup.ClassId, dayOffset: 6,
            moduleIds: [setup.ModuleIds[0], setup.ModuleIds[1]]);

        Assert.Equal(setup.ClassId, session.GetProperty("trainingClassId").GetInt64());
        Assert.Equal(1, session.GetProperty("sessionNo").GetInt32());
        Assert.Equal(Today.AddDays(6).ToString("yyyy-MM-dd"), session.GetProperty("scheduledDate").GetString());
        Assert.Equal("09:00", session.GetProperty("startTime").GetString());
        Assert.Equal(120, session.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Branch 1", session.GetProperty("location").GetString());
        Assert.Equal(trainerId, session.GetProperty("trainerId").GetInt64());
        Assert.Equal([setup.ModuleIds[0], setup.ModuleIds[1]],
            session.GetProperty("moduleIds").EnumerateArray().Select(m => m.GetInt64()));
    }

    [Fact]
    public async Task Session_is_rescheduled_and_removed_while_the_class_is_planned()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync(sessions: 2);
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);

        var moved = await (await trainer.PutAsJsonAsync($"{Sessions}/{setup.SessionIds[0]}", new
            {
                scheduledDate = Today.AddDays(9), startTime = "14:30", durationMinutes = 90, trainerId,
            }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("14:30", moved.GetProperty("startTime").GetString());
        Assert.Equal(90, moved.GetProperty("durationMinutes").GetInt32());

        await (await trainer.DeleteAsync($"{Sessions}/{setup.SessionIds[1]}")).ShouldBeAsync(HttpStatusCode.NoContent);
        var after = await (await trainer.GetAsync($"{Classes}/{setup.ClassId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Single(after.GetProperty("sessions").EnumerateArray());
    }

    // ---------------------------------------------------------------- BR-34

    [Fact]
    public async Task BR_34_a_trainer_who_is_not_certified_on_the_bound_version_cannot_be_scheduled()
    {
        var setup = await factory.NewClassAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        object Body() => new { scheduledDate = Today, startTime = "09:00", durationMinutes = 120, trainerId };

        // Not certified at all.
        var envelope = await (await trainer.PostAsJsonAsync($"{Classes}/{setup.ClassId}/sessions", Body()))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-34");
        Assert.Contains("trainerId", envelope.DetailFields());

        // Certified, but on the version of a different recipe.
        var other = await factory.NewClassAsync();
        await factory.CertifyAsync(trainerId, other.CourseId, other.RecipeVersionId);
        await (await trainer.PostAsJsonAsync($"{Classes}/{setup.ClassId}/sessions", Body()))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-34");

        // Certified on the bound version.
        await factory.CertifyAsync(trainerId, setup.CourseId, setup.RecipeVersionId);
        await (await trainer.PostAsJsonAsync($"{Classes}/{setup.ClassId}/sessions", Body()))
            .ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task BR_34_a_certificate_that_needs_recertification_no_longer_qualifies()
    {
        var setup = await factory.NewClassAsync(sessions: 1); // the trainer is certified here
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE certificate SET status = 'NEEDS_RECERT' WHERE user_id = {trainerId} AND recipe_version_id = {setup.RecipeVersionId}"));

        await factory.AddSessionAsync(setup.ClassId, dayOffset: 2, expected: HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Session_must_be_taught_by_an_active_trainer()
    {
        var setup = await factory.NewClassAsync();
        var adminId = await factory.UserIdAsync(TestUsers.Admin);

        var envelope = await factory.AddSessionAsync(setup.ClassId, expected: HttpStatusCode.BadRequest, trainerId: adminId);

        Assert.Contains("trainerId", envelope.DetailFields());
    }

    [Fact]
    public async Task Class_cannot_be_scheduled_for_a_course_that_is_not_published()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var draftCourse = (await factory.NewCourseAsync()).Id();

        var response = await trainer.PostAsJsonAsync(Classes, new
        {
            courseId = draftCourse, branchId = await factory.BranchIdAsync("B01"), name = "Too early",
            startDate = Today, endDate = Today.AddDays(7),
        });

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "COURSE_NOT_PUBLISHED");
    }

    [Fact]
    public async Task Roster_can_be_named_and_a_trainee_of_another_branch_is_refused()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync();   // at B01
        var ownTrainee = await factory.UserIdAsync(TestUsers.Trainee);
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        var otherBranchTrainee = await factory.UserIdAsync("trainee3");

        var envelope = await (await trainer.PostAsJsonAsync($"{Classes}/{setup.ClassId}/open",
                new { traineeIds = new[] { ownTrainee, otherBranchTrainee } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("traineeIds", envelope.DetailFields());

        // A trainer may be on the roster: that is how a trainer becomes certified to teach.
        var opened = await factory.OpenClassAsync(setup.ClassId, [ownTrainee, trainerId]);
        Assert.Equal(2, opened.GetProperty("enrollmentIds").GetArrayLength());
    }

    // ---------------------------------------------------------------- UC-30

    [Fact]
    public async Task Attendance_is_recorded_only_while_the_class_is_running()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync(sessions: 1);
        var sessionId = setup.SessionIds[0];

        // PLANNED: there is nobody enrolled and nothing to record yet.
        await (await trainer.PutAsJsonAsync($"{Sessions}/{sessionId}/attendance", new { entries = Array.Empty<object>() }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        await factory.OpenClassAsync(setup.ClassId);
        var trainee = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var trainee2 = await factory.EnrollmentIdAsync(setup.ClassId, "trainee2");

        var sheet = await (await factory.RecordAttendanceAsync(sessionId, (trainee, "PRESENT"), (trainee2, "ABSENT")))
            .ShouldBeAsync(HttpStatusCode.OK);
        var entries = sheet.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("enrollmentId").GetInt64());
        Assert.Equal("PRESENT", entries[trainee].GetProperty("status").GetString());
        Assert.Equal("ABSENT", entries[trainee2].GetProperty("status").GetString());
        Assert.Equal("RUNNING", sheet.GetProperty("classState").GetString());

        // CLOSED: attendance is frozen.
        await (await trainer.PostAsync($"{Classes}/{setup.ClassId}/close", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await factory.RecordAttendanceAsync(sessionId, (trainee2, "PRESENT")))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        var frozen = await (await trainer.GetAsync($"{Sessions}/{sessionId}/attendance")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(frozen.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("enrollmentId").GetInt64() == trainee2 && e.GetProperty("status").GetString() == "ABSENT");
    }

    [Fact]
    public async Task Every_attendance_correction_is_written_to_the_audit_log()
    {
        var setup = await factory.NewClassAsync(sessions: 1);
        await factory.OpenClassAsync(setup.ClassId);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        await (await factory.RecordAttendanceAsync(setup.SessionIds[0], (enrollmentId, "ABSENT"))).ShouldBeAsync(HttpStatusCode.OK);

        await (await factory.RecordAttendanceAsync(setup.SessionIds[0], (enrollmentId, "EXCUSED"))).ShouldBeAsync(HttpStatusCode.OK);
        await (await factory.RecordAttendanceAsync(setup.SessionIds[0], (enrollmentId, "EXCUSED"))).ShouldBeAsync(HttpStatusCode.OK); // no change

        var corrections = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "Attendance" && a.Action == "CORRECT_ATTENDANCE" && a.UserId == trainerId)
            .OrderByDescending(a => a.Id).Select(a => a.PayloadJson).ToListAsync());
        var matching = corrections.Select(p => JsonSerializer.Deserialize<JsonElement>(p!))
            .Where(p => p.GetProperty("enrollmentId").GetInt64() == enrollmentId).ToList();
        var correction = Assert.Single(matching);
        Assert.Equal("ABSENT", correction.GetProperty("from").GetString());
        Assert.Equal("EXCUSED", correction.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Attendance_entry_for_someone_outside_the_class_is_400()
    {
        var setup = await factory.NewClassAsync(sessions: 1);
        var other = await factory.NewClassAsync(sessions: 1);
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        await factory.OpenClassAsync(other.ClassId, [await factory.UserIdAsync("trainee2")]);
        var outsider = await factory.EnrollmentIdAsync(other.ClassId, "trainee2");

        var envelope = await (await factory.RecordAttendanceAsync(setup.SessionIds[0], (outsider, "PRESENT")))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("entries[0].enrollmentId", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- BR-32

    [Fact]
    public async Task BR_32_all_modules_but_too_little_attendance_is_not_eligible_until_attendance_is_corrected()
    {
        var setup = await factory.NewClassAsync(sessions: 5);
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);

        // Present at three of five sessions: 60 percent, the minimum is 80.
        for (var i = 0; i < 5; i++)
        {
            await (await factory.RecordAttendanceAsync(setup.SessionIds[i], (enrollmentId, i < 3 ? "PRESENT" : "ABSENT")))
                .ShouldBeAsync(HttpStatusCode.OK);
        }
        var afterModules = await factory.CompleteModulesAsync(TestUsers.Trainee, enrollmentId, setup.ModuleIds);
        Assert.Equal(100, afterModules.GetProperty("progressPercent").GetInt32());
        Assert.Equal("IN_PROGRESS", afterModules.GetProperty("state").GetString());

        var notYet = await factory.EligibilityAsync(enrollmentId, TestUsers.Trainee);
        Assert.False(notYet.GetProperty("eligible").GetBoolean());
        Assert.Empty(notYet.GetProperty("missingModules").EnumerateArray());
        Assert.Equal(60, notYet.GetProperty("attendancePct").GetInt32());
        Assert.Equal(80, notYet.GetProperty("minAttendancePct").GetInt32());
        Assert.False(notYet.GetProperty("attendanceStillReachable").GetBoolean());

        // The trainer corrects one absence: four of five is 80 percent, and the checker runs again.
        await (await factory.RecordAttendanceAsync(setup.SessionIds[3], (enrollmentId, "PRESENT"))).ShouldBeAsync(HttpStatusCode.OK);

        var now = await factory.EligibilityAsync(enrollmentId, TestUsers.Trainee);
        Assert.True(now.GetProperty("eligible").GetBoolean());
        Assert.Equal(80, now.GetProperty("attendancePct").GetInt32());
        Assert.Equal("ELIGIBLE", now.GetProperty("state").GetString());
        Assert.Equal(2, now.GetProperty("retakesLeft").GetInt32());
    }

    [Fact]
    public async Task BR_32_full_attendance_but_six_of_seven_modules_is_not_eligible()
    {
        var setup = await factory.NewClassAsync(sessions: 2);
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        foreach (var sessionId in setup.SessionIds)
        {
            await (await factory.RecordAttendanceAsync(sessionId, (enrollmentId, "PRESENT"))).ShouldBeAsync(HttpStatusCode.OK);
        }

        await factory.CompleteModulesAsync(TestUsers.Trainee, enrollmentId, setup.ModuleIds.Take(6));

        var result = await factory.EligibilityAsync(enrollmentId);
        Assert.False(result.GetProperty("eligible").GetBoolean());
        var missing = Assert.Single(result.GetProperty("missingModules").EnumerateArray());
        Assert.Equal(setup.ModuleIds[6], missing.GetProperty("courseModuleId").GetInt64());
        Assert.Equal("EXCEPTION_HANDLING", missing.GetProperty("moduleType").GetString());
        Assert.Equal(100, result.GetProperty("attendancePct").GetInt32());
        Assert.Equal("IN_PROGRESS", result.GetProperty("state").GetString());

        // The seventh module is what makes the difference.
        var done = await factory.CompleteModulesAsync(TestUsers.Trainee, enrollmentId, [setup.ModuleIds[6]]);
        Assert.Equal("ELIGIBLE", done.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Trainee_is_flagged_on_the_attendance_sheet_once_the_minimum_is_out_of_reach()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync(sessions: 5);
        await factory.OpenClassAsync(setup.ClassId);
        var missing = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var attending = await factory.EnrollmentIdAsync(setup.ClassId, "trainee2");

        // Two of five sessions missed: even perfect attendance from here gives 60 percent.
        for (var i = 0; i < 2; i++)
        {
            await (await factory.RecordAttendanceAsync(setup.SessionIds[i], (missing, "ABSENT"), (attending, "PRESENT")))
                .ShouldBeAsync(HttpStatusCode.OK);
        }

        var sheet = await (await trainer.GetAsync($"{Sessions}/{setup.SessionIds[2]}/attendance")).ShouldBeAsync(HttpStatusCode.OK);
        var entries = sheet.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("enrollmentId").GetInt64());
        Assert.False(entries[missing].GetProperty("attendanceStillReachable").GetBoolean());
        Assert.True(entries[attending].GetProperty("attendanceStillReachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entries[missing].GetProperty("status").ValueKind); // session 3 not recorded yet
    }

    // ---------------------------------------------------------------- UC-14

    [Fact]
    public async Task Learner_sees_own_courses_with_progress_and_the_lessons_rendered_from_the_recipe()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        await factory.CompleteModulesAsync(TestUsers.Trainee, enrollmentId, [setup.ModuleIds[0]]);

        var mine = await (await trainee.GetAsync("/api/v1/me/enrollments")).ShouldBeAsync(HttpStatusCode.OK);
        var entry = mine.EnumerateArray().Single(e => e.Id() == enrollmentId);
        Assert.Equal("IN_PROGRESS", entry.GetProperty("state").GetString());
        Assert.Equal(14, entry.GetProperty("progressPercent").GetInt32());
        Assert.Equal("PRODUCT", entry.GetProperty("courseType").GetString());
        Assert.False(entry.GetProperty("overdue").GetBoolean());

        var modules = await (await trainee.GetAsync($"{Enrollments}/{enrollmentId}/modules")).ShouldBeAsync(HttpStatusCode.OK);
        var list = modules.EnumerateArray().ToList();
        Assert.Equal(7, list.Count);
        Assert.True(list[0].GetProperty("completed").GetBoolean());
        Assert.False(list[1].GetProperty("completed").GetBoolean());
        // The lesson viewer shows the reference values of the bound version (BR-22)...
        var sop = list.Single(m => m.GetProperty("module").GetProperty("moduleType").GetString() == "SOP").GetProperty("module");
        Assert.Equal("Brew the oolong", sop.GetProperty("lessons")[0].GetProperty("reference").GetProperty("actionText").GetString());
        // ...and never the answers of the quiz.
        Assert.DoesNotContain("correctOption", modules.GetRawText());
    }

    [Fact]
    public async Task Trainee_can_only_see_and_act_on_their_own_enrolment()
    {
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId);
        var own = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var someoneElses = await factory.EnrollmentIdAsync(setup.ClassId, "trainee2");
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        await (await trainee.GetAsync($"{Enrollments}/{own}")).ShouldBeAsync(HttpStatusCode.OK);
        // Another trainee's enrolment does not exist as far as this trainee is concerned.
        await (await trainee.GetAsync($"{Enrollments}/{someoneElses}")).ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await trainee.GetAsync($"{Enrollments}/{someoneElses}/modules")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await trainee.GetAsync($"{Enrollments}/{someoneElses}/eligibility")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await trainee.PostAsync($"{Enrollments}/{someoneElses}/modules/{setup.ModuleIds[0]}/complete", null))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // A trainer may look at any enrolment, but completing modules is the learner's own act.
        await (await trainer.GetAsync($"{Enrollments}/{someoneElses}")).ShouldBeAsync(HttpStatusCode.OK);
        await (await trainer.PostAsync($"{Enrollments}/{someoneElses}/modules/{setup.ModuleIds[0]}/complete", null))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, (await factory.GetEnrollmentAsync(someoneElses)).GetProperty("progressPercent").GetInt32());
    }

    [Fact]
    public async Task Store_level_caller_sees_only_the_classes_and_enrolments_of_its_own_branch()
    {
        var atB01 = await factory.NewClassAsync(branchCode: "B01");
        var atB02 = await factory.NewClassAsync(branchCode: "B02");
        await factory.OpenClassAsync(atB01.ClassId);
        await factory.OpenClassAsync(atB02.ClassId);
        var b02 = await factory.BranchIdAsync("B02");

        await using var db = factory.CreateDbContextAs(new FakeCurrentUser(1, RoleName.BranchManager, b02));
        var classes = await db.TrainingClasses.Select(c => c.Id).ToListAsync();
        var learners = await db.Enrollments.Select(e => e.User.Username).Distinct().ToListAsync();

        Assert.Contains(atB02.ClassId, classes);
        Assert.DoesNotContain(atB01.ClassId, classes);
        Assert.Contains("trainee3", learners);
        Assert.DoesNotContain(TestUsers.Trainee, learners);
    }

    // ---------------------------------------------------------------- the enrolment state model

    [Fact]
    public async Task Closed_enrolment_accepts_nothing_more_and_only_a_locked_one_can_be_reset()
    {
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);

        // ASSIGNED -> ASSIGNED is not a transition: only a LOCKED enrolment is reset.
        await (await manager.PostAsync($"{Enrollments}/{enrollmentId}/reset", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        var closed = await (await manager.PostAsync($"{Enrollments}/{enrollmentId}/close", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CLOSED", closed.GetProperty("state").GetString());

        await (await trainee.PostAsync($"{Enrollments}/{enrollmentId}/modules/{setup.ModuleIds[0]}/complete", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await manager.PostAsync($"{Enrollments}/{enrollmentId}/close", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
    }

    // ---------------------------------------------------------------- UC-27

    [Fact]
    public async Task Regulation_change_leaves_enrolments_in_flight_on_the_rule_they_were_created_under()
    {
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var before = await factory.EligibilityAsync(enrollmentId);
        var seeded = (await (await manager.GetAsync(Regulations)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()
            .Single(r => r.GetProperty("courseType").GetString() == "PRODUCT" && r.GetProperty("effectiveFrom").GetString() == "2026-01-01");
        object Stricter(DateOnly from) => new
        {
            courseType = "PRODUCT", mandatoryForRole = "TRAINEE", dueDays = 3, maxRetakes = 0, minAttendancePct = 100,
            effectiveFrom = from,
        };

        // A rule that takes effect today, or earlier, would reach back to the enrolment just created.
        var retroactive = await (await manager.PostAsJsonAsync(Regulations, Stricter(Today)))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "REGULATION_RETROACTIVE");
        Assert.Contains("effectiveFrom", retroactive.DetailFields());
        // So would editing the regulation the enrolment was created under.
        await (await manager.PutAsJsonAsync($"{Regulations}/{seeded.Id()}", Stricter(new DateOnly(2026, 1, 1))))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "REGULATION_RETROACTIVE");

        // The change is made by adding a regulation that takes effect later.
        var created = await (await manager.PostAsJsonAsync(Regulations, Stricter(Today.AddDays(1)))).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(0, created.GetProperty("maxRetakes").GetInt32());
        Assert.Equal(Today.AddDays(1).ToString("yyyy-MM-dd"), created.GetProperty("effectiveFrom").GetString());

        // The enrolment in flight is still on two retakes and 80 percent.
        var after = await factory.EligibilityAsync(enrollmentId);
        Assert.Equal(seeded.Id(), after.GetProperty("regulationId").GetInt64());
        Assert.Equal(before.GetProperty("retakesLeft").GetInt32(), after.GetProperty("retakesLeft").GetInt32());
        Assert.Equal(2, after.GetProperty("retakesLeft").GetInt32());
        Assert.Equal(80, after.GetProperty("minAttendancePct").GetInt32());
        Assert.Equal(Today.AddDays(14).ToString("yyyy-MM-dd"),
            (await factory.GetEnrollmentAsync(enrollmentId)).GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Regulation_has_the_shape_of_the_contract_and_is_validated()
    {
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);

        var created = await (await manager.PostAsJsonAsync(Regulations, new
            {
                courseType = "EQUIPMENT", mandatoryForRole = "TRAINEE", prerequisiteType = "INDUCTION", dueDays = 14,
                maxRetakes = 2, minAttendancePct = 80, effectiveFrom = "2027-01-01",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("EQUIPMENT", created.GetProperty("courseType").GetString());
        Assert.Equal("TRAINEE", created.GetProperty("mandatoryForRole").GetString());
        Assert.Equal("INDUCTION", created.GetProperty("prerequisiteType").GetString());
        Assert.Equal(14, created.GetProperty("dueDays").GetInt32());
        Assert.Equal("2027-01-01", created.GetProperty("effectiveFrom").GetString());

        // Nobody is enrolled under it, so it can still be changed.
        var updated = await (await manager.PutAsJsonAsync($"{Regulations}/{created.Id()}", new
            {
                courseType = "EQUIPMENT", dueDays = 21, maxRetakes = 1, minAttendancePct = 90, effectiveFrom = "2027-02-01",
            }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(21, updated.GetProperty("dueDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("mandatoryForRole").ValueKind);

        var envelope = await (await manager.PostAsJsonAsync(Regulations,
                new { courseType = "EQUIPMENT", dueDays = 0, minAttendancePct = 150, effectiveFrom = "2027-03-01" }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("dueDays", envelope.DetailFields());
        Assert.Contains("minAttendancePct", envelope.DetailFields());
    }

    [Fact]
    public async Task Training_needs_list_the_trainees_who_are_not_yet_certified_on_a_mandatory_course()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var setup = await factory.NewClassAsync();   // a published PRODUCT course, mandatory for trainees
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);
        var trainee2Id = await factory.UserIdAsync("trainee2");
        await factory.CertifyAsync(trainee2Id, setup.CourseId, setup.RecipeVersionId);

        var needs = await (await trainer.GetAsync($"/api/v1/training-needs?courseId={setup.CourseId}")).ShouldBeAsync(HttpStatusCode.OK);

        var users = needs.EnumerateArray().Select(n => n.GetProperty("userId").GetInt64()).ToList();
        Assert.Contains(traineeId, users);
        Assert.DoesNotContain(trainee2Id, users);   // already certified
        var need = needs.EnumerateArray().First(n => n.GetProperty("userId").GetInt64() == traineeId);
        Assert.Equal("REGULATION", need.GetProperty("reason").GetString());
        Assert.Equal("INDUCTION", need.GetProperty("prerequisiteType").GetString());
        Assert.Equal(14, need.GetProperty("dueDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, need.GetProperty("enrollmentState").ValueKind);
    }
}
