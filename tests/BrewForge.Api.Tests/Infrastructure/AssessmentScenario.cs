using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>A learner enrolled on a class who has studied every module: ELIGIBLE.</summary>
public sealed record EligibleLearner(ClassSetup Setup, string Username, long UserId, long EnrollmentId)
{
    public string Url => $"{Enrollments}/{EnrollmentId}";
}

/// <summary>Takes a learner through the quiz and the practical, the way a trainee and a trainer would.</summary>
public static class AssessmentScenario
{
    public const string Certificates = "/api/v1/certificates";

    /// <summary>
    /// A published course, a class without sessions, and the given learner
    /// alone on its roster with all seven modules complete.
    /// </summary>
    public static async Task<EligibleLearner> NewEligibleLearnerAsync(this BrewForgeApiFactory factory,
        string username = TestUsers.Trainee)
    {
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(username)]);
        return await factory.StudyAsync(setup, username);
    }

    /// <summary>The learner, already on the roster of the class, completes every module.</summary>
    public static async Task<EligibleLearner> StudyAsync(this BrewForgeApiFactory factory, ClassSetup setup,
        string username)
    {
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, username);
        var done = await factory.CompleteModulesAsync(username, enrollmentId, setup.ModuleIds);
        Assert.Equal("ELIGIBLE", done.GetProperty("state").GetString());
        return new EligibleLearner(setup, username, await factory.UserIdAsync(username), enrollmentId);
    }

    /// <summary>
    /// An answer sheet for the course quiz, which has one question per module
    /// whose correct option is "A": right everywhere except in the modules named.
    /// </summary>
    public static async Task<object> AnswersAsync(this BrewForgeApiFactory factory, long courseId,
        params string[] wrongModules)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var questions = await (await trainer.GetAsync($"{CourseScenario.Courses}/{courseId}/quiz/questions"))
            .ShouldBeAsync(HttpStatusCode.OK);
        return new
        {
            answers = questions.EnumerateArray().Select(question => new
            {
                questionId = question.Id(),
                selectedOption = wrongModules.Contains(question.GetProperty("moduleType").GetString()) ? "B" : "A",
            }).ToList(),
        };
    }

    /// <summary>The learner attempts the quiz, answering wrongly in the modules named.</summary>
    public static async Task<HttpResponseMessage> AttemptQuizAsync(this BrewForgeApiFactory factory,
        EligibleLearner learner, params string[] wrongModules)
    {
        using var client = await factory.ClientForAsync(learner.Username);
        return await client.PostAsJsonAsync($"{learner.Url}/quiz-attempts",
            await factory.AnswersAsync(learner.Setup.CourseId, wrongModules));
    }

    /// <summary>The recipe steps on the practical checklist of the course.</summary>
    public static async Task<IReadOnlyList<long>> ChecklistAsync(this BrewForgeApiFactory factory, long courseId) =>
    [
        .. (await factory.GetCourseAsync(courseId)).GetProperty("practicalChecklist").EnumerateArray()
            .Select(item => item.GetProperty("recipeStepId").GetInt64()),
    ];

    /// <summary>The trainer observes the practical: every item passed, or the first one failed.</summary>
    public static async Task<HttpResponseMessage> EvaluatePracticalAsync(this BrewForgeApiFactory factory,
        EligibleLearner learner, bool allPassed = true, string evaluator = TestUsers.Trainer)
    {
        using var client = await factory.ClientForAsync(evaluator);
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);
        return await client.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new
        {
            items = checklist.Select((stepId, index) => new
            {
                recipeStepId = stepId,
                passed = allPassed || index > 0,
                note = allPassed || index > 0 ? null : "Poured too fast",
            }),
        });
    }

    /// <summary>Quiz passed, practical passed: returns the certificate that follows.</summary>
    public static async Task<JsonElement> CertifyThroughTheCourseAsync(this BrewForgeApiFactory factory,
        EligibleLearner learner)
    {
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var evaluation = await (await factory.EvaluatePracticalAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);

        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.GetAsync($"{Certificates}/{evaluation.GetProperty("certificateId").GetInt64()}"))
            .ShouldBeAsync(HttpStatusCode.OK);
    }
}
