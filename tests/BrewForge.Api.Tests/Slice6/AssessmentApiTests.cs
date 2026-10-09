using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Launch;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BrewForge.Api.Tests.Infrastructure.AssessmentScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice6;

/// <summary>
/// UC-15, UC-16 and UC-17 through the API: the quiz and its retake limit
/// (BR-32, BR-33), the practical evaluation (BR-14) and the certificate that
/// follows from passing both (BR-13, BR-21).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AssessmentApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- BR-32

    [Fact]
    public async Task BR_32_the_quiz_is_closed_until_the_enrolment_is_eligible()
    {
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        await factory.CompleteModulesAsync(TestUsers.Trainee, enrollmentId, setup.ModuleIds.Take(6));
        var answers = await factory.AnswersAsync(setup.CourseId);

        // Neither the questions nor an attempt, with six of seven modules done.
        await (await trainee.GetAsync($"{Enrollments}/{enrollmentId}/quiz"))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");
        var refusal = await (await trainee.PostAsJsonAsync($"{Enrollments}/{enrollmentId}/quiz-attempts", answers))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");
        Assert.Contains("modules", refusal.DetailFields());
        Assert.Contains("state", refusal.DetailFields());

        // Nor the practical.
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var checklist = await factory.ChecklistAsync(setup.CourseId);
        await (await trainer.PostAsJsonAsync($"{Enrollments}/{enrollmentId}/practical-evaluation",
                new { items = checklist.Select(stepId => new { recipeStepId = stepId, passed = true }) }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");

        Assert.Empty((await (await trainee.GetAsync($"{Enrollments}/{enrollmentId}/quiz-attempts"))
            .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Learner_is_served_the_questions_without_the_answers()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var quiz = await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(80, quiz.GetProperty("passScore").GetInt32());
        Assert.Equal(2, quiz.GetProperty("retakesLeft").GetInt32());
        var questions = quiz.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(7, questions.Count);
        Assert.Equal(learner.Setup.ModuleIds.Order(), questions.Select(q => q.GetProperty("courseModuleId").GetInt64()).Order());
        Assert.All(questions, question => Assert.Equal(4, question.GetProperty("options").GetArrayLength()));
        Assert.DoesNotContain("correctOption", quiz.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- UC-15

    [Fact]
    public async Task Failed_attempt_has_the_shape_of_the_contract_and_sends_the_trainee_back_to_the_failed_modules()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var result = await (await factory.AttemptQuizAsync(learner, "SOP", "TECHNIQUE")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, result.GetProperty("attemptNo").GetInt32());
        Assert.Equal(71, result.GetProperty("score").GetInt32()); // five of seven
        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.Equal(80, result.GetProperty("passScore").GetInt32());
        Assert.Equal(2, result.GetProperty("retakesLeft").GetInt32());
        Assert.Equal("ELIGIBLE", result.GetProperty("enrollmentState").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("certificateId").ValueKind);

        // The breakdown says which modules were failed.
        var perModule = result.GetProperty("perModule").EnumerateArray().ToDictionary(
            m => m.GetProperty("moduleType").GetString()!,
            m => (Correct: m.GetProperty("correct").GetInt32(), Total: m.GetProperty("total").GetInt32()));
        Assert.Equal(7, perModule.Count);
        Assert.Equal((0, 1), perModule["SOP"]);
        Assert.Equal((0, 1), perModule["TECHNIQUE"]);
        Assert.Equal(5, perModule.Values.Count(m => m == (1, 1)));

        // Exactly those two have to be studied again before the next attempt.
        var eligibility = await factory.EligibilityAsync(learner.EnrollmentId, TestUsers.Trainee);
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(["SOP", "TECHNIQUE"], eligibility.GetProperty("missingModules").EnumerateArray()
            .Select(m => m.GetProperty("moduleType").GetString()).Order());
        Assert.Equal(71, (await factory.GetEnrollmentAsync(learner.EnrollmentId)).GetProperty("progressPercent").GetInt32());
        await (await factory.AttemptQuizAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");

        await factory.CompleteModulesAsync(TestUsers.Trainee, learner.EnrollmentId, learner.Setup.ModuleIds);
        var second = await (await factory.AttemptQuizAsync(learner, "SOP")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, second.GetProperty("attemptNo").GetInt32());
        Assert.Equal(85, second.GetProperty("score").GetInt32()); // six of seven
        Assert.True(second.GetProperty("passed").GetBoolean());

        // Both attempts are kept, each with its breakdown.
        var attempts = (await (await trainee.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeAsync(HttpStatusCode.OK))
            .EnumerateArray().ToList();
        Assert.Equal([1, 2], attempts.Select(a => a.GetProperty("attemptNo").GetInt32()));
        Assert.Equal([false, true], attempts.Select(a => a.GetProperty("passed").GetBoolean()));
        Assert.All(attempts, attempt => Assert.Equal(7, attempt.GetProperty("perModule").GetArrayLength()));

        // A quiz that was passed is not taken again.
        await (await factory.AttemptQuizAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "QUIZ_ALREADY_PASSED");
    }

    [Fact]
    public async Task Answer_sheet_is_validated()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var unknown = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts",
                new { answers = new[] { new { questionId = 999999999L, selectedOption = "A" } } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var twice = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts",
                new { answers = new[] { new { questionId = 1L, selectedOption = "A" }, new { questionId = 1L, selectedOption = "B" } } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("answers", unknown.DetailFields());
        Assert.Contains("answers", twice.DetailFields());
        // A refused sheet is not an attempt.
        Assert.Equal(2, (await factory.EligibilityAsync(learner.EnrollmentId)).GetProperty("retakesLeft").GetInt32());
        Assert.Empty((await (await trainee.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Quiz_belongs_to_the_learner_alone()
    {
        var learner = await factory.NewEligibleLearnerAsync("trainee2");
        using var otherTrainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var answers = await factory.AnswersAsync(learner.Setup.CourseId);

        // To another trainee the enrolment does not exist.
        await (await otherTrainee.GetAsync($"{learner.Url}/quiz")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await otherTrainee.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await otherTrainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts", answers)).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // A trainer may read the attempts, but cannot sit the quiz for a trainee.
        await (await trainer.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeAsync(HttpStatusCode.OK);
        await (await trainer.PostAsJsonAsync($"{learner.Url}/quiz-attempts", answers)).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await trainer.GetAsync($"{learner.Url}/quiz")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- BR-33

    [Fact]
    public async Task BR_33_exhausting_the_retakes_locks_the_enrolment_and_tells_the_training_manager()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var managerId = await factory.UserIdAsync(TestUsers.TrainingManager);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        // The regulation allows two retakes: three attempts in all.
        JsonElement result = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = await (await factory.AttemptQuizAsync(learner, "SOP", "TECHNIQUE")).ShouldBeAsync(HttpStatusCode.OK);
            Assert.Equal(attempt, result.GetProperty("attemptNo").GetInt32());
            Assert.Equal(3 - attempt, result.GetProperty("retakesLeft").GetInt32());
            if (attempt < 3)
            {
                Assert.Equal("ELIGIBLE", result.GetProperty("enrollmentState").GetString());
                await factory.CompleteModulesAsync(TestUsers.Trainee, learner.EnrollmentId, learner.Setup.ModuleIds);
            }
        }
        Assert.Equal("LOCKED", result.GetProperty("enrollmentState").GetString());
        Assert.Equal("LOCKED", (await factory.GetEnrollmentAsync(learner.EnrollmentId)).GetProperty("state").GetString());

        // A fourth attempt is refused, and nothing is recorded for it.
        var refusal = await (await factory.AttemptQuizAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-33");
        Assert.Contains("attempts", refusal.DetailFields());
        Assert.Equal(3, (await (await trainee.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeAsync(HttpStatusCode.OK)).GetArrayLength());
        // Studying again does not reopen it either.
        await (await trainee.PostAsync($"{learner.Url}/modules/{learner.Setup.ModuleIds[0]}/complete", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        var notices = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "AppUser" && a.EntityId == managerId && a.Action == "NOTIFY")
            .Select(a => a.PayloadJson).ToListAsync());
        var notice = Assert.Single(notices.Select(p => JsonSerializer.Deserialize<JsonElement>(p!)),
            p => p.TryGetProperty("enrollmentId", out var id) && id.GetInt64() == learner.EnrollmentId);
        Assert.Equal("Enrolment locked", notice.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Reset_by_the_training_manager_reopens_a_locked_enrolment_from_the_start()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await (await factory.AttemptQuizAsync(learner, "SOP", "TECHNIQUE")).ShouldBeAsync(HttpStatusCode.OK);
            if (attempt < 3) await factory.CompleteModulesAsync(TestUsers.Trainee, learner.EnrollmentId, learner.Setup.ModuleIds);
        }

        var reset = await (await manager.PostAsync($"{learner.Url}/reset", null)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("ASSIGNED", reset.GetProperty("state").GetString());
        Assert.Equal(0, reset.GetProperty("progressPercent").GetInt32());
        Assert.Equal(Today.AddDays(14).ToString("yyyy-MM-dd"), reset.GetProperty("dueDate").GetString());

        // The whole course again, and the retake count starts over; the history stays.
        await (await factory.AttemptQuizAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");
        await factory.StudyAsync(learner.Setup, TestUsers.Trainee);
        Assert.Equal(2, (await factory.EligibilityAsync(learner.EnrollmentId)).GetProperty("retakesLeft").GetInt32());

        var passed = await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(4, passed.GetProperty("attemptNo").GetInt32());
        Assert.True(passed.GetProperty("passed").GetBoolean());
        Assert.Equal(100, passed.GetProperty("score").GetInt32());
    }

    // ---------------------------------------------------------------- UC-16: BR-14

    [Fact]
    public async Task BR_14_the_evaluator_can_never_be_the_trainee()
    {
        // A trainer takes the course to become certified on the version (BR-34)...
        var learner = await factory.NewEligibleLearnerAsync(TestUsers.Trainer);
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);

        // ...and holds the role that evaluates practicals, but not their own.
        var refusal = await (await factory.EvaluatePracticalAsync(learner, evaluator: TestUsers.Trainer))
            .ShouldBeErrorAsync(HttpStatusCode.Forbidden, rule: "BR-14");

        Assert.Equal("MSG-E01", refusal.GetProperty("code").GetString());
        Assert.Equal(0, await factory.WithDbAsync(db => db.Enrollments.Where(e => e.Id == learner.EnrollmentId)
            .SelectMany(e => e.PracticalEvaluations).CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Certificates.CountAsync(c => c.CourseId == learner.Setup.CourseId)));
        Assert.Equal("ELIGIBLE", (await factory.GetEnrollmentAsync(learner.EnrollmentId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Practical_is_passed_only_when_every_item_of_the_checklist_passed()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);
        Assert.Equal(2, checklist.Count); // the two technique gates of the recipe

        var failed = await (await factory.EvaluatePracticalAsync(learner, allPassed: false)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.False(failed.GetProperty("passed").GetBoolean());
        Assert.Equal(learner.EnrollmentId, failed.GetProperty("enrollmentId").GetInt64());
        Assert.Equal(trainerId, failed.GetProperty("evaluatedBy").GetInt64());
        var items = failed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(checklist, items.Select(i => i.GetProperty("recipeStepId").GetInt64()));
        Assert.Equal([false, true], items.Select(i => i.GetProperty("passed").GetBoolean()));
        Assert.Equal("Poured too fast", items[0].GetProperty("note").GetString());

        var passed = await (await factory.EvaluatePracticalAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(passed.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Practical_evaluation_must_mark_exactly_the_checklist()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);
        Task<HttpResponseMessage> Evaluate(object body) => trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", body);
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();

        var missingItem = await (await Evaluate(new { practicalVideoId, items = new[] { new { recipeStepId = checklist[0], passed = true } } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var foreignStep = await (await Evaluate(new
            {
                practicalVideoId,
                items = checklist.Append(999999999).Select(stepId => new { recipeStepId = stepId, passed = true }),
            }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noVerdict = await (await Evaluate(new { items = new[] { new { recipeStepId = checklist[0] } } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noItems = await (await Evaluate(new { })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("items", missingItem.DetailFields());
        Assert.Contains("items", foreignStep.DetailFields());
        Assert.Contains("items[0].passed", noVerdict.DetailFields());
        Assert.Contains("items", noItems.DetailFields());
    }

    // ---------------------------------------------------------------- UC-17: BR-13, BR-21

    [Fact]
    public async Task BR_21_passing_the_practical_alone_issues_no_certificate_and_the_quiz_then_completes_it()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var practical = await (await factory.EvaluatePracticalAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(practical.GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, practical.GetProperty("certificateId").ValueKind);
        Assert.Equal("ELIGIBLE", practical.GetProperty("enrollmentState").GetString());
        Assert.DoesNotContain((await (await trainee.GetAsync("/api/v1/me/certificates")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray(),
            c => c.GetProperty("courseId").GetInt64() == learner.Setup.CourseId);

        // A failed quiz changes nothing about that.
        var failedQuiz = await (await factory.AttemptQuizAsync(learner, "SOP", "TECHNIQUE")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, failedQuiz.GetProperty("certificateId").ValueKind);
        await factory.CompleteModulesAsync(TestUsers.Trainee, learner.EnrollmentId, learner.Setup.ModuleIds);

        // The passed quiz is the last of the three conditions.
        var quiz = await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PASSED", quiz.GetProperty("enrollmentState").GetString());
        var certificateId = quiz.GetProperty("certificateId").GetInt64();

        var enrollment = await factory.GetEnrollmentAsync(learner.EnrollmentId);
        Assert.Equal("PASSED", enrollment.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, enrollment.GetProperty("completedAt").ValueKind);
        Assert.Equal(certificateId, Assert.Single(await factory.WithDbAsync(db => db.Certificates
            .Where(c => c.UserId == learner.UserId && c.CourseId == learner.Setup.CourseId).Select(c => c.Id).ToListAsync())));
    }

    [Fact]
    public async Task BR_21_a_failed_practical_item_holds_the_certificate_back_until_it_is_observed_again()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);

        var failed = await (await factory.EvaluatePracticalAsync(learner, allPassed: false)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("certificateId").ValueKind);
        Assert.Equal("ELIGIBLE", failed.GetProperty("enrollmentState").GetString());

        var passed = await (await factory.EvaluatePracticalAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PASSED", passed.GetProperty("enrollmentState").GetString());
        Assert.NotEqual(JsonValueKind.Null, passed.GetProperty("certificateId").ValueKind);

        // PASSED is final: nothing more is assessed on this enrolment.
        await (await factory.EvaluatePracticalAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");
    }

    [Fact]
    public async Task BR_13_the_certificate_is_bound_to_the_recipe_version_of_the_course()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var certificate = await factory.CertifyThroughTheCourseAsync(learner);

        Assert.Equal(learner.UserId, certificate.GetProperty("userId").GetInt64());
        Assert.Equal(learner.Setup.CourseId, certificate.GetProperty("courseId").GetInt64());
        Assert.Equal(learner.Setup.RecipeVersionId, certificate.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(1, certificate.GetProperty("versionNo").GetInt32());
        Assert.False(string.IsNullOrEmpty(certificate.GetProperty("recipeCode").GetString()));
        Assert.Equal("VALID", certificate.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, certificate.GetProperty("supersededBy").ValueKind);
        Assert.Equal(await factory.BranchIdAsync("B01"), certificate.GetProperty("branchId").GetInt64());

        // It is on the trainee's skill passport...
        var passport = await (await trainee.GetAsync("/api/v1/me/certificates")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(passport.EnumerateArray(), c => c.Id() == certificate.Id());

        // ...and the trail says which enrolment earned it, and that the trainee was told.
        var issued = JsonSerializer.Deserialize<JsonElement>((await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "Certificate" && a.EntityId == certificate.Id() && a.Action == "ISSUE_CERTIFICATE")
            .Select(a => a.PayloadJson).SingleAsync()))!);
        Assert.Equal(learner.EnrollmentId, issued.GetProperty("enrollmentId").GetInt64());
        Assert.Equal(learner.Setup.RecipeVersionId, issued.GetProperty("recipeVersionId").GetInt64());
        Assert.Contains(await factory.WithDbAsync(db => db.AuditLogs
                .Where(a => a.EntityType == "AppUser" && a.EntityId == learner.UserId && a.Action == "NOTIFY")
                .Select(a => a.PayloadJson).ToListAsync()),
            payload => payload!.Contains("Certificate issued"));
    }

    [Fact]
    public async Task A_certificate_cannot_be_created_changed_or_deleted_through_the_api()
    {
        // Every route that concerns certificates only reads.
        var methods = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("certificates", StringComparison.OrdinalIgnoreCase))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            .Distinct().ToList();
        Assert.Equal(["GET"], methods);

        var certificate = await factory.CertifyThroughTheCourseAsync(await factory.NewEligibleLearnerAsync());
        var before = await factory.WithDbAsync(db => db.Certificates.CountAsync());
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var body = new { userId = await factory.UserIdAsync("trainee2"), courseId = certificate.GetProperty("courseId").GetInt64() };

        foreach (var client in new[] { admin, trainer })
        {
            Assert.Contains((await client.PostAsJsonAsync(Certificates, body)).StatusCode,
                new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsJsonAsync($"{Certificates}/{certificate.Id()}", body)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync($"{Certificates}/{certificate.Id()}")).StatusCode);
        }

        Assert.Equal(before, await factory.WithDbAsync(db => db.Certificates.CountAsync()));
        Assert.Equal("VALID", (await (await trainer.GetAsync($"{Certificates}/{certificate.Id()}")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trainee_reads_only_their_own_certificates()
    {
        var certificate = await factory.CertifyThroughTheCourseAsync(await factory.NewEligibleLearnerAsync("trainee2"));
        using var owner = await factory.ClientForAsync("trainee2");
        using var otherTrainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var url = $"{Certificates}/{certificate.Id()}";

        await (await owner.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        await (await auditor.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        await (await otherTrainee.GetAsync(url)).ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain((await (await otherTrainee.GetAsync("/api/v1/me/certificates")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray(),
            c => c.Id() == certificate.Id());
    }

    [Fact]
    public async Task Assessment_waits_while_the_course_is_being_updated_to_a_newer_version()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        // What a superseded recipe version does to its course (BR-15).
        await factory.WithDbAsync(async db =>
        {
            (await db.Courses.SingleAsync(c => c.Id == learner.Setup.CourseId)).MarkOutOfDate();
            await db.SaveChangesAsync();
        });

        await (await factory.AttemptQuizAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-W05", "BR-15");
        await (await factory.EvaluatePracticalAsync(learner)).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-W02", "BR-15");
    }

    // ---------------------------------------------------------------- the readiness count

    [Fact]
    public async Task Issuing_a_certificate_recounts_the_certified_staff_of_the_branch()
    {
        var setup = await factory.NewClassAsync();
        var b01 = await factory.BranchIdAsync("B01");
        var b02 = await factory.BranchIdAsync("B02");
        var recipeId = await factory.WithDbAsync(db => db.RecipeVersions.Where(v => v.Id == setup.RecipeVersionId)
            .Select(v => v.RecipeId).SingleAsync());
        // The drink is planned at both branches and needs two certified staff at each.
        await factory.WithDbAsync(async db =>
        {
            db.BranchLaunchStatuses.Add(BranchLaunchStatus.Plan(b01, recipeId, setup.RecipeVersionId, minCertifiedStaff: 2));
            db.BranchLaunchStatuses.Add(BranchLaunchStatus.Plan(b02, recipeId, setup.RecipeVersionId, minCertifiedStaff: 2));
            await db.SaveChangesAsync();
        });
        Task<(string Status, int Count, bool Met)> ReadinessAsync(long branchId) => factory.WithDbAsync(async db =>
        {
            var row = await db.BranchLaunchStatuses.AsNoTracking().SingleAsync(l => l.BranchId == branchId && l.RecipeId == recipeId);
            return (row.Status.ToString().ToUpperInvariant(), row.CertifiedCount, row.CoverageMet);
        });
        await factory.OpenClassAsync(setup.ClassId); // both trainees of B01

        await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, TestUsers.Trainee));
        Assert.Equal(("PREPARING", 1, false), await ReadinessAsync(b01));

        await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, "trainee2"));
        Assert.Equal(("READY", 2, true), await ReadinessAsync(b01));

        // Staff certified at one branch count for that branch only.
        Assert.Equal(("PREPARING", 0, false), await ReadinessAsync(b02));
    }

    // ---------------------------------------------------------------- UC-18

    [Fact]
    public async Task Training_progress_dashboard_counts_learners_by_state_and_certified_staff_per_branch()
    {
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId); // trainee and trainee2 of B01
        await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, TestUsers.Trainee));
        var behind = await factory.EnrollmentIdAsync(setup.ClassId, "trainee2");
        var yesterday = Today.AddDays(-1);
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE enrollment SET due_date = {yesterday} WHERE id = {behind}"));
        var url = $"/api/v1/dashboards/training-progress?courseId={setup.CourseId}";

        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var row = Assert.Single((await (await trainer.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());

        Assert.Equal("B01", row.GetProperty("branchCode").GetString());
        Assert.Equal(setup.CourseId, row.GetProperty("courseId").GetInt64());
        Assert.Equal(setup.RecipeVersionId, row.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(1, row.GetProperty("assigned").GetInt32());
        Assert.Equal(0, row.GetProperty("inProgress").GetInt32());
        Assert.Equal(0, row.GetProperty("eligible").GetInt32());
        Assert.Equal(1, row.GetProperty("passed").GetInt32());
        Assert.Equal(0, row.GetProperty("locked").GetInt32());
        Assert.Equal(1, row.GetProperty("overdue").GetInt32());
        Assert.Equal(1, row.GetProperty("certifiedValid").GetInt32());
        Assert.Equal(0, row.GetProperty("needsRecertification").GetInt32());

        // A branch manager is shown the branch they manage, and no other.
        using var ownManager = await factory.ClientForAsync(TestUsers.BranchManager);
        using var otherManager = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        Assert.Single((await (await ownManager.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.Empty((await (await otherManager.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
        Assert.Empty((await (await otherManager.GetAsync($"{url}&branchId={await factory.BranchIdAsync("B01")}"))
            .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
    }
}
