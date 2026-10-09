using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>Builds courses through the API, the way a trainer and a training manager would.</summary>
public static class CourseScenario
{
    public const string Courses = "/api/v1/courses";
    public const string Modules = "/api/v1/course-modules";
    public const string Lessons = "/api/v1/lessons";

    public static JsonElement Module(this JsonElement course, string moduleType) =>
        course.GetProperty("modules").EnumerateArray()
            .Single(module => module.GetProperty("moduleType").GetString() == moduleType);

    public static long Id(this JsonElement element) => element.GetProperty("id").GetInt64();

    /// <summary>A PRODUCT course in DRAFT on a freshly released recipe version.</summary>
    public static async Task<JsonElement> NewCourseAsync(this BrewForgeApiFactory factory, long? recipeVersionId = null,
        string courseType = "PRODUCT")
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        recipeVersionId ??= (await factory.NewReleasedRecipeAsync()).VersionId;

        return await (await trainer.PostAsJsonAsync(Courses,
                new { recipeVersionId, courseType, title = $"Course {Guid.NewGuid():N}"[..20] }))
            .ShouldBeAsync(HttpStatusCode.Created);
    }

    public static async Task<JsonElement> GetCourseAsync(this BrewForgeApiFactory factory, long courseId)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.GetAsync($"{Courses}/{courseId}")).ShouldBeAsync(HttpStatusCode.OK);
    }

    /// <summary>
    /// Does everything a trainer must do before a course can be submitted:
    /// prose for every technique gate, content and a duration for the
    /// authored modules, and a quiz with one question per module.
    /// </summary>
    public static async Task CompleteAuthoringAsync(this BrewForgeApiFactory factory, long courseId)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.GetCourseAsync(courseId);

        var technique = course.Module("TECHNIQUE");
        foreach (var gate in technique.GetProperty("lessons").EnumerateArray())
        {
            await (await trainer.PutAsJsonAsync($"{Lessons}/{gate.Id()}",
                    new { content = "Watch the trainer demonstrate this, then repeat it three times." }))
                .ShouldBeAsync(HttpStatusCode.OK);
        }
        if (technique.GetProperty("lessons").GetArrayLength() == 0)
        {
            await (await trainer.PostAsJsonAsync($"{Modules}/{technique.Id()}/lessons",
                    new { title = "General technique", content = "Work clean, work in order." }))
                .ShouldBeAsync(HttpStatusCode.Created);
        }
        await (await trainer.PutAsJsonAsync($"{Modules}/{technique.Id()}", new { durationMinutes = 20 }))
            .ShouldBeAsync(HttpStatusCode.OK);

        foreach (var type in new[] { "COMMON_MISTAKES", "EXCEPTION_HANDLING" })
        {
            var module = course.Module(type);
            await (await trainer.PostAsJsonAsync($"{Modules}/{module.Id()}/lessons",
                    new { title = $"{type} - lesson 1", content = "What goes wrong, and what to do about it." }))
                .ShouldBeAsync(HttpStatusCode.Created);
            await (await trainer.PutAsJsonAsync($"{Modules}/{module.Id()}", new { durationMinutes = 15 }))
                .ShouldBeAsync(HttpStatusCode.OK);
        }

        foreach (var module in course.GetProperty("modules").EnumerateArray())
        {
            await factory.AddQuestionAsync(courseId, module.Id());
        }
    }

    /// <summary>A four-option question whose correct answer is "A".</summary>
    public static async Task<JsonElement> AddQuestionAsync(this BrewForgeApiFactory factory, long courseId, long moduleId)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.PostAsJsonAsync($"{Courses}/{courseId}/quiz/questions", new
            {
                courseModuleId = moduleId,
                questionText = $"Which statement about module {moduleId} is correct?",
                options = new[]
                {
                    new { key = "A", text = "The correct one" }, new { key = "B", text = "A wrong one" },
                    new { key = "C", text = "Another wrong one" }, new { key = "D", text = "Also wrong" },
                },
                correctOption = "A",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
    }

    /// <summary>A course that has been authored, submitted and approved: PUBLISHED.</summary>
    public static async Task<JsonElement> NewPublishedCourseAsync(this BrewForgeApiFactory factory,
        long? recipeVersionId = null)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);

        var courseId = (await factory.NewCourseAsync(recipeVersionId)).Id();
        await factory.CompleteAuthoringAsync(courseId);
        await (await trainer.PostAsync($"{Courses}/{courseId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        return await (await manager.PostAsync($"{Courses}/{courseId}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);
    }
}
