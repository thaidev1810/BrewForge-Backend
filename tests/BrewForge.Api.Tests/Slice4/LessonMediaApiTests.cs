using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Courses;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;

namespace BrewForge.Api.Tests.Slice4;

/// <summary>
/// The pictures of a lesson through the API: uploaded by the trainer to a
/// lesson they write, served to whoever reads the lesson, and gone with the
/// lesson.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LessonMediaApiTests(BrewForgeApiFactory factory)
{
    private const string Media = "/api/v1/lesson-media";

    /// <summary>A PNG as far as its first bytes go, which is as far as anything looks.</summary>
    private static byte[] Png(int size = 4000) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(0, size - 8).Select(i => (byte)(i % 199))];

    private async Task<HttpResponseMessage> UploadAsync(long lessonId, byte[] content, string fileName = "pour.png",
        string username = TestUsers.Trainer)
    {
        using var client = await factory.ClientForAsync(username);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync($"{Lessons}/{lessonId}/media", form);
    }

    /// <summary>A draft course and a lesson the trainer wrote in its COMMON_MISTAKES module.</summary>
    private async Task<(long CourseId, long LessonId)> NewLessonAsync()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var lesson = await (await trainer.PostAsJsonAsync($"{Modules}/{course.Module("COMMON_MISTAKES").Id()}/lessons",
            new { title = "Pouring too fast", content = "Pour down the side of the cup." })).ShouldBeAsync(HttpStatusCode.Created);
        return (course.Id(), lesson.Id());
    }

    private async Task<JsonElement> LessonOfAsync(long courseId, long lessonId) =>
        (await factory.GetCourseAsync(courseId)).GetProperty("modules").EnumerateArray()
            .SelectMany(module => module.GetProperty("lessons").EnumerateArray()).Single(lesson => lesson.Id() == lessonId);

    private int StoredPictures()
    {
        var root = Path.Combine(factory.VideoRoot, "lesson-media");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;
    }

    [Fact]
    public async Task Picture_is_uploaded_to_a_lesson_shown_with_it_and_served_to_whoever_reads_it()
    {
        var (courseId, lessonId) = await NewLessonAsync();
        var bytes = Png();

        var response = await UploadAsync(lessonId, bytes, "How to pour.PNG");
        var picture = await response.ShouldBeAsync(HttpStatusCode.Created);
        var second = await (await UploadAsync(lessonId, Png(1200), "cup.png")).ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal((lessonId, 1, "How to pour.PNG", "image/png", (long)bytes.Length, $"{Media}/{picture.Id()}"),
            (picture.GetProperty("lessonId").GetInt64(), picture.GetProperty("sortOrder").GetInt32(), picture.GetProperty("fileName").GetString(),
                picture.GetProperty("contentType").GetString(), picture.GetProperty("sizeBytes").GetInt64(), picture.GetProperty("url").GetString()));
        Assert.Equal(picture.GetProperty("url").GetString(), response.Headers.Location?.OriginalString);
        Assert.Equal(2, second.GetProperty("sortOrder").GetInt32());

        // The lesson carries its pictures in order wherever it is rendered.
        var lesson = await LessonOfAsync(courseId, lessonId);
        Assert.Equal([picture.Id(), second.Id()], lesson.GetProperty("media").EnumerateArray().Select(m => m.Id()));

        // Served byte for byte, to a trainee as to the trainer: a learner sees the picture in the lesson.
        foreach (var username in new[] { TestUsers.Trainer, TestUsers.Trainee, TestUsers.Auditor })
        {
            using var reader = await factory.ClientForAsync(username);
            using var served = await reader.GetAsync($"{Media}/{picture.Id()}");
            Assert.Equal((HttpStatusCode.OK, "image/png"), (served.StatusCode, served.Content.Headers.ContentType?.MediaType));
            Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync());
        }
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "Lesson" && a.EntityId == lessonId && a.Action == "ADD_LESSON_MEDIA")));
    }

    [Fact]
    public async Task Picture_is_removed_by_the_trainer_and_its_file_goes_with_it()
    {
        var (courseId, lessonId) = await NewLessonAsync();
        var stored = StoredPictures();
        var picture = await (await UploadAsync(lessonId, Png())).ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(stored + 1, StoredPictures());
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        using var removed = await trainer.DeleteAsync($"{Media}/{picture.Id()}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await (await trainer.GetAsync($"{Media}/{picture.Id()}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await trainer.DeleteAsync($"{Media}/{picture.Id()}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        Assert.Empty((await LessonOfAsync(courseId, lessonId)).GetProperty("media").EnumerateArray());
        Assert.Equal(stored, StoredPictures());
    }

    [Fact]
    public async Task Deleting_a_lesson_takes_its_pictures_and_their_files_with_it()
    {
        var (_, lessonId) = await NewLessonAsync();
        var stored = StoredPictures();
        var picture = await (await UploadAsync(lessonId, Png())).ShouldBeAsync(HttpStatusCode.Created);
        await (await UploadAsync(lessonId, Png(900))).ShouldBeAsync(HttpStatusCode.Created);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        using var deleted = await trainer.DeleteAsync($"{Lessons}/{lessonId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.LessonMedia.AnyAsync(m => m.LessonId == lessonId)));
        await (await trainer.GetAsync($"{Media}/{picture.Id()}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        Assert.Equal(stored, StoredPictures());
    }

    [Theory]
    [InlineData("notes.png", "these are notes, not a picture")]
    [InlineData("script.png", "#!/bin/sh\nrm -rf /")]
    [InlineData("empty.png", "")]
    public async Task File_that_is_not_a_picture_is_refused_whatever_it_is_called_and_nothing_is_kept(string fileName, string content)
    {
        var (_, lessonId) = await NewLessonAsync();
        var stored = StoredPictures();

        var refusal = await (await UploadAsync(lessonId, Encoding.UTF8.GetBytes(content), fileName)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("file", refusal.DetailFields());
        Assert.Equal(stored, StoredPictures());
    }

    [Fact]
    public async Task Picture_named_as_another_kind_or_too_large_or_missing_is_refused()
    {
        var (_, lessonId) = await NewLessonAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var noFile = new MultipartFormDataContent { { new StringContent("x"), "note" } };

        var wrongName = await (await UploadAsync(lessonId, Png(), "pour.jpg")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var tooLarge = await (await UploadAsync(lessonId, Png((int)LessonMedia.MaxBytes + 1))).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var missing = await (await trainer.PostAsync($"{Lessons}/{lessonId}/media", noFile)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var notAForm = await (await trainer.PostAsJsonAsync($"{Lessons}/{lessonId}/media", new { })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.All(new[] { wrongName, tooLarge, missing, notAForm }, refusal => Assert.Contains("file", refusal.DetailFields()));
        await (await UploadAsync(999999999, Png())).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BR_30_a_lesson_generated_from_the_recipe_takes_no_picture_and_a_technique_gate_does()
    {
        var course = await factory.NewCourseAsync();
        var step = course.Module("SOP").GetProperty("lessons")[0].Id();
        var gate = course.Module("TECHNIQUE").GetProperty("lessons")[0].Id();

        await (await UploadAsync(step, Png())).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");
        await (await UploadAsync(gate, Png())).ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Pictures_of_a_published_course_are_as_fixed_as_its_text()
    {
        var course = await factory.NewCourseAsync();
        var gate = course.Module("TECHNIQUE").GetProperty("lessons")[0].Id();
        var picture = await (await UploadAsync(gate, Png())).ShouldBeAsync(HttpStatusCode.Created);
        await factory.CompleteAuthoringAsync(course.Id());
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        await (await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await manager.PostAsync($"{Courses}/{course.Id()}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);

        await (await UploadAsync(gate, Png())).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await trainer.DeleteAsync($"{Media}/{picture.Id()}")).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        // Still there, for those who learn from the published course.
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        using var served = await trainee.GetAsync($"{Media}/{picture.Id()}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }
}
