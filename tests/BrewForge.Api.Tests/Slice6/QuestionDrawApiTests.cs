using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;

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
}
