using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice6;

/// <summary>
/// The quiz as a bank of questions, through the API: the trainer says how
/// many of each module an attempt is given, the learner is served that draw,
/// and the attempt is marked against it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuestionDrawApiTests(BrewForgeApiFactory factory)
{
    private const int BankPerModule = 6;

    /// <summary>
    /// A draft course ready to be submitted whose quiz has six questions on
    /// SOP and on TECHNIQUE and one on each of the other five modules:
    /// seventeen in all.
    /// </summary>
    private async Task<long> NewCourseWithBankAsync()
    {
        var course = await factory.NewCourseAsync();
        await factory.CompleteAuthoringAsync(course.Id());
        foreach (var type in new[] { "SOP", "TECHNIQUE" })
        {
            for (var i = 1; i < BankPerModule; i++) await factory.AddQuestionAsync(course.Id(), course.Module(type).Id());
        }
        return course.Id();
    }

    /// <summary>The seeded trainee, eligible on a published course whose quiz draws so many questions a module.</summary>
    private async Task<EligibleLearner> NewLearnerOnABankAsync(int drawPerModule)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var courseId = await NewCourseWithBankAsync();
        await (await trainer.PutAsJsonAsync($"{Courses}/{courseId}/quiz", new { drawPerModule })).ShouldBeAsync(HttpStatusCode.OK);
        await (await trainer.PostAsync($"{Courses}/{courseId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        var course = await (await manager.PostAsync($"{Courses}/{courseId}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);

        var created = await (await trainer.PostAsJsonAsync(Classes, new
            {
                courseId, branchId = await factory.BranchIdAsync("B01"), name = $"Class {Guid.NewGuid():N}"[..16],
                startDate = Today, endDate = Today.AddDays(30),
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var setup = new ClassSetup(courseId, course.GetProperty("recipeVersionId").GetInt64(), created.Id(), [],
            [.. course.GetProperty("modules").EnumerateArray().Select(m => m.Id())]);
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        return await factory.StudyAsync(setup, TestUsers.Trainee);
    }

    private static List<JsonElement> Questions(JsonElement quiz) => [.. quiz.GetProperty("questions").EnumerateArray()];

    private static object Sheet(IEnumerable<long> questionIds, string option) =>
        new { answers = questionIds.Select(questionId => new { questionId, selectedOption = option }).ToList() };

    // ---------------------------------------------------------------- the trainer

    [Fact]
    public async Task Trainer_says_how_many_questions_of_each_module_an_attempt_is_given()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var quiz = $"{Courses}/{await NewCourseWithBankAsync()}/quiz";

        // Until a draw is set every question is asked.
        var before = await (await trainer.GetAsync(quiz)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("drawPerModule").ValueKind);
        Assert.Equal((17, 17), (before.GetProperty("questionCount").GetInt32(), before.GetProperty("questionsPerAttempt").GetInt32()));

        // Two from each of the two modules that have six, and the one there is of each of the other five.
        var drawn = await (await trainer.PutAsJsonAsync(quiz, new { drawPerModule = 2 })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, drawn.GetProperty("drawPerModule").GetInt32());
        Assert.Equal((17, 9), (drawn.GetProperty("questionCount").GetInt32(), drawn.GetProperty("questionsPerAttempt").GetInt32()));

        // Left as it is when the request does not name it.
        var rescored = await (await trainer.PutAsJsonAsync(quiz, new { passScore = 70 })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((70, 2), (rescored.GetProperty("passScore").GetInt32(), rescored.GetProperty("drawPerModule").GetInt32()));

        var refusal = await (await trainer.PutAsJsonAsync(quiz, new { drawPerModule = 51 })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("drawPerModule", refusal.DetailFields());

        // 0 turns the draw off.
        var off = await (await trainer.PutAsJsonAsync(quiz, new { drawPerModule = 0 })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("drawPerModule").ValueKind);
        Assert.Equal(17, off.GetProperty("questionsPerAttempt").GetInt32());
    }

    // ---------------------------------------------------------------- the learner

    [Fact]
    public async Task Learner_is_served_the_draw_of_the_attempt_and_marked_against_it()
    {
        var learner = await NewLearnerOnABankAsync(drawPerModule: 2);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);

        var quiz = await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, quiz.GetProperty("attemptNo").GetInt32());
        var questions = Questions(quiz);
        Assert.Equal(9, questions.Count);
        // Every module is still tested, the two with a bank by two questions each.
        var perModule = questions.GroupBy(q => q.GetProperty("moduleType").GetString()!).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(7, perModule.Count);
        Assert.Equal((2, 2), (perModule["SOP"], perModule["TECHNIQUE"]));

        // Asking again does not draw again.
        var again = await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(questions.Select(q => q.Id()), Questions(again).Select(q => q.Id()));

        var result = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts", Sheet(questions.Select(q => q.Id()), "A")))
            .ShouldBeAsync(HttpStatusCode.OK);

        // Nine asked, nine right: nothing is counted as unanswered for the eight that were not drawn.
        Assert.Equal((100, true), (result.GetProperty("score").GetInt32(), result.GetProperty("passed").GetBoolean()));
        Assert.Equal(9, result.GetProperty("perModule").EnumerateArray().Sum(m => m.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task Answer_to_a_question_outside_the_draw_is_refused()
    {
        var learner = await NewLearnerOnABankAsync(drawPerModule: 2);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var served = Questions(await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK))
            .Select(q => q.Id()).ToList();
        var bank = (await (await trainer.GetAsync($"{Courses}/{learner.Setup.CourseId}/quiz/questions")).ShouldBeAsync(HttpStatusCode.OK))
            .EnumerateArray().Select(q => q.Id()).ToList();
        var notServed = bank.First(id => !served.Contains(id));

        var refusal = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts", Sheet(served.Append(notServed), "A")))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("answers", refusal.DetailFields());
        Assert.Empty((await (await trainee.GetAsync($"{learner.Url}/quiz-attempts")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task Retake_is_served_the_draw_of_the_next_attempt()
    {
        var learner = await NewLearnerOnABankAsync(drawPerModule: 2);
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var first = Questions(await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK));

        var failed = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts", Sheet(first.Select(q => q.Id()), "B")))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((1, false), (failed.GetProperty("attemptNo").GetInt32(), failed.GetProperty("passed").GetBoolean()));
        await factory.CompleteModulesAsync(TestUsers.Trainee, learner.EnrollmentId, learner.Setup.ModuleIds);

        var quiz = await (await trainee.GetAsync($"{learner.Url}/quiz")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, quiz.GetProperty("attemptNo").GetInt32());
        var second = Questions(quiz);
        Assert.Equal(9, second.Count);

        // Whatever the second draw is, it is what the second attempt is marked against.
        var passed = await (await trainee.PostAsJsonAsync($"{learner.Url}/quiz-attempts", Sheet(second.Select(q => q.Id()), "A")))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((2, 100, true), (passed.GetProperty("attemptNo").GetInt32(), passed.GetProperty("score").GetInt32(),
            passed.GetProperty("passed").GetBoolean()));
    }
}
