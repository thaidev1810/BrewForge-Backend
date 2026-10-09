using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice6;

/// <summary>
/// A course that is built from no recipe version, such as INDUCTION. It has
/// no steps to put on a practical checklist, so the checklist is made of the
/// lessons the trainer wrote in the TECHNIQUE module, and with that it
/// certifies like any other course (BR-21).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CourseWithoutRecipeApiTests(BrewForgeApiFactory factory)
{
    private static readonly string[] Techniques = ["Wash hands and station", "Greet the guest"];

    [Fact]
    public async Task Course_without_a_recipe_takes_its_practical_checklist_from_the_technique_lessons_and_certifies()
    {
        var (course, learner) = await NewEligibleLearnerOnInductionAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        // The checklist is the TECHNIQUE module as the trainer wrote it, in its order, and names no step.
        var checklist = course.GetProperty("practicalChecklist").EnumerateArray().ToList();
        Assert.Equal(Techniques, checklist.Select(item => item.GetProperty("actionText").GetString()));
        Assert.Equal([1, 2], checklist.Select(item => item.GetProperty("stepOrder").GetInt32()));
        Assert.All(checklist, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("recipeStepId").ValueKind));
        Assert.Equal(course.Module("TECHNIQUE").GetProperty("lessons").EnumerateArray().Select(l => l.Id()),
            checklist.Select(item => item.GetProperty("lessonId").GetInt64()));

        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();
        var evaluation = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new
        {
            practicalVideoId,
            items = checklist.Select(item => new { lessonId = item.GetProperty("lessonId").GetInt64(), passed = true }),
        })).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(evaluation.GetProperty("passed").GetBoolean());
        Assert.Equal("PASSED", evaluation.GetProperty("enrollmentState").GetString());
        var certificate = await (await trainer.GetAsync($"{AssessmentScenario.Certificates}/{evaluation.GetProperty("certificateId").GetInt64()}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((learner.UserId, course.Id(), "VALID"), (certificate.GetProperty("userId").GetInt64(),
            certificate.GetProperty("courseId").GetInt64(), certificate.GetProperty("status").GetString()));
        // Bound to no recipe version, because the course is not.
        Assert.Equal(JsonValueKind.Null, certificate.GetProperty("recipeVersionId").ValueKind);
    }

    [Fact]
    public async Task Practical_of_a_course_without_a_recipe_marks_every_technique_lesson_and_nothing_else()
    {
        var (course, learner) = await NewEligibleLearnerOnInductionAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var lessonIds = course.GetProperty("practicalChecklist").EnumerateArray()
            .Select(item => item.GetProperty("lessonId").GetInt64()).ToList();
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();
        Task<HttpResponseMessage> Evaluate(IEnumerable<object> items) =>
            trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new { practicalVideoId, items });

        var byStep = await (await Evaluate(lessonIds.Select(id => (object)new { recipeStepId = id, passed = true })))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var oneLeftOut = await (await Evaluate([new { lessonId = lessonIds[0], passed = true }]))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var foreign = await (await Evaluate(lessonIds.Append(999999999).Select(id => (object)new { lessonId = id, passed = true })))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("items[0].lessonId", byStep.DetailFields());
        Assert.Contains("items", oneLeftOut.DetailFields());
        Assert.Contains("items", foreign.DetailFields());

        // One item failed is the practical failed, here as anywhere.
        var failed = await (await Evaluate(lessonIds.Select((id, index) => (object)new { lessonId = id, passed = index > 0 })))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.False(failed.GetProperty("passed").GetBoolean());
        Assert.Equal(lessonIds, failed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("lessonId").GetInt64()));
    }

    [Fact]
    public async Task Course_without_a_recipe_cannot_be_submitted_while_its_technique_module_is_empty()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await NewInductionCourseAsync(techniques: []);

        // BR-31 says so first: the module is empty. With it the checklist is empty too.
        var refusal = await (await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-31");

        Assert.Contains("modules[TECHNIQUE]", refusal.DetailFields());
        Assert.Empty((await factory.GetCourseAsync(course.Id())).GetProperty("practicalChecklist").EnumerateArray());
    }

    /// <summary>An INDUCTION course in DRAFT, written by the trainer: every module has a lesson, a duration and a question.</summary>
    private async Task<JsonElement> NewInductionCourseAsync(string[] techniques)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await (await trainer.PostAsJsonAsync(Courses,
                new { courseType = "INDUCTION", title = $"Induction {Guid.NewGuid():N}"[..20] }))
            .ShouldBeAsync(HttpStatusCode.Created);

        foreach (var module in course.GetProperty("modules").EnumerateArray())
        {
            var isTechnique = module.GetProperty("moduleType").GetString() == "TECHNIQUE";
            foreach (var title in isTechnique ? techniques : ["What a new colleague needs to know"])
            {
                await (await trainer.PostAsJsonAsync($"{Modules}/{module.Id()}/lessons",
                    new { title, content = "Shown by the trainer on the first day." })).ShouldBeAsync(HttpStatusCode.Created);
            }
            await (await trainer.PutAsJsonAsync($"{Modules}/{module.Id()}", new { durationMinutes = 10 }))
                .ShouldBeAsync(HttpStatusCode.OK);
            await factory.AddQuestionAsync(course.Id(), module.Id());
        }
        return await factory.GetCourseAsync(course.Id());
    }

    /// <summary>The course published, a class of it opened for the seeded trainee, and every module studied.</summary>
    private async Task<(JsonElement Course, EligibleLearner Learner)> NewEligibleLearnerOnInductionAsync()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var course = await NewInductionCourseAsync(Techniques);
        await (await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await manager.PostAsync($"{Courses}/{course.Id()}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);

        var classId = (await (await trainer.PostAsJsonAsync(Classes, new
        {
            courseId = course.Id(), branchId = await factory.BranchIdAsync("B01"), name = $"Induction {Guid.NewGuid():N}"[..16],
            startDate = Today, endDate = Today.AddDays(7),
        })).ShouldBeAsync(HttpStatusCode.Created)).Id();
        var userId = await factory.UserIdAsync(TestUsers.Trainee);
        await factory.OpenClassAsync(classId, [userId]);

        var moduleIds = course.GetProperty("modules").EnumerateArray().Select(m => m.Id()).ToList();
        var setup = new ClassSetup(course.Id(), RecipeVersionId: 0, classId, [], moduleIds);
        return (course, await factory.StudyAsync(setup, TestUsers.Trainee));
    }
}
