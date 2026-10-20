using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice4;

/// <summary>
/// Notes from the counter in the course of a drink: a staff remark R&amp;D
/// accepted is read next to the step it is about, by the trainer and by the
/// learner, until a version of the recipe says it itself.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CourseFieldNotesApiTests(BrewForgeApiFactory factory)
{
    private const string Feedback = "/api/v1/recipe-feedback";

    private async Task<long> RemarkAsync(string username, long recipeId, object body)
    {
        using var client = await factory.ClientForAsync(username);
        return (await (await client.PostAsJsonAsync($"{Recipes}/{recipeId}/feedback", body)).ShouldBeAsync(HttpStatusCode.Created)).Id();
    }

    private async Task ReviewAsync(long feedbackId, string verdict, object? body = null)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        await (await specialist.PostAsJsonAsync($"{Feedback}/{feedbackId}/{verdict}", body ?? new { })).ShouldBeAsync(HttpStatusCode.OK);
    }

    private async Task<long> StepIdAsync(long versionId, int stepOrder) =>
        (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("stepOrder").GetInt32() == stepOrder).Id();

    private static List<long> NoteIds(JsonElement lessonOrModule) =>
        [.. lessonOrModule.GetProperty("fieldNotes").EnumerateArray().Select(note => note.GetProperty("feedbackId").GetInt64())];

    private static JsonElement LessonOf(JsonElement course, string moduleType, long recipeStepId) =>
        course.Module(moduleType).GetProperty("lessons").EnumerateArray()
            .Single(lesson => lesson.GetProperty("recipeStepId").ValueKind == JsonValueKind.Number
                              && lesson.GetProperty("recipeStepId").GetInt64() == recipeStepId);

    /// <summary>Every note anywhere in the course, lessons and modules alike.</summary>
    private static List<long> AllNoteIds(JsonElement course) =>
    [
        .. course.GetProperty("modules").EnumerateArray().SelectMany(module =>
            NoteIds(module).Concat(module.GetProperty("lessons").EnumerateArray().SelectMany(NoteIds))),
    ];

    [Fact]
    public async Task Accepted_staff_remark_is_read_with_the_step_it_is_about()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var firstStep = await StepIdAsync(drink.VersionId, 1); // the step with a technique gate
        var onStep = await RemarkAsync(TestUsers.Trainee, drink.RecipeId, new
        {
            source = "STAFF", category = "COMMON_MISTAKE", content = "Tapping twice packs the leaf and it brews bitter.",
            recipeStepId = firstStep,
        });
        var onDrink = await RemarkAsync("trainee2", drink.RecipeId,
            new { source = "STAFF", category = "PREPARATION_NOTE", content = "Warm the cup first on cold days." });

        // Nothing R&D has not agreed with is put in a course.
        Assert.Empty(AllNoteIds(await factory.GetCourseAsync(drink.CourseId)));

        await ReviewAsync(onStep, "accept");
        await ReviewAsync(onDrink, "accept");
        var course = await factory.GetCourseAsync(drink.CourseId);

        // With the step, wherever it is taught: the procedure and its technique gate.
        Assert.Equal([onStep], NoteIds(LessonOf(course, "SOP", firstStep)));
        Assert.Equal([onStep], NoteIds(LessonOf(course, "TECHNIQUE", firstStep)));
        Assert.Empty(NoteIds(LessonOf(course, "SOP", await StepIdAsync(drink.VersionId, 2))));
        // A note on the drink as a whole goes with the common mistakes.
        Assert.Equal([onDrink], NoteIds(course.Module("COMMON_MISTAKES")));
        Assert.Equal(new[] { onStep, onStep, onDrink }.Order(), AllNoteIds(course).Order());

        var note = LessonOf(course, "SOP", firstStep).GetProperty("fieldNotes")[0];
        Assert.Equal(("COMMON_MISTAKE", "Tapping twice packs the leaf and it brews bitter.", 1),
            (note.GetProperty("category").GetString(), note.GetProperty("content").GetString(), note.GetProperty("versionNo").GetInt32()));
        Assert.False(string.IsNullOrWhiteSpace(note.GetProperty("from").GetString()));
        // The course itself was not touched: it is still the published course it was.
        Assert.Equal("PUBLISHED", course.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Rejected_remark_and_what_a_customer_said_are_not_in_the_course()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var rejected = await RemarkAsync(TestUsers.Trainee, drink.RecipeId,
            new { source = "STAFF", category = "PREPARATION_NOTE", content = "Skip the tap, it changes nothing." });
        var customers = await RemarkAsync(TestUsers.BranchManager, drink.RecipeId,
            new { source = "CUSTOMER", category = "TASTE", content = "Too strong.", rating = 2 });

        await ReviewAsync(rejected, "reject", new { note = "The tap levels the dose." });
        await ReviewAsync(customers, "accept");

        Assert.Empty(AllNoteIds(await factory.GetCourseAsync(drink.CourseId)));
    }

    [Fact]
    public async Task Learner_reads_the_notes_in_the_modules_of_their_enrolment()
    {
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 1); // the seeded trainee is certified, trainee2 is not
        var firstStep = await StepIdAsync(drink.VersionId, 1);
        var note = await RemarkAsync(TestUsers.Trainee, drink.RecipeId, new
        {
            source = "STAFF", category = "PREPARATION_NOTE", content = "Level the dose over the bin, not over the cup.",
            recipeStepId = firstStep,
        });
        await ReviewAsync(note, "accept");

        // A colleague who is learning the drink now, at the same branch.
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var created = await (await trainer.PostAsJsonAsync(Classes, new
            {
                courseId = drink.CourseId, branchId = drink.BranchId, name = $"Class {Guid.NewGuid():N}"[..16],
                startDate = Today, endDate = Today.AddDays(30),
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await factory.OpenClassAsync(created.Id(), [await factory.UserIdAsync("trainee2")]);
        var enrollmentId = await factory.EnrollmentIdAsync(created.Id(), "trainee2");
        using var learner = await factory.ClientForAsync("trainee2");

        var modules = await (await learner.GetAsync($"{Enrollments}/{enrollmentId}/modules")).ShouldBeAsync(HttpStatusCode.OK);

        var sop = modules.EnumerateArray().Select(m => m.GetProperty("module"))
            .Single(m => m.GetProperty("moduleType").GetString() == "SOP");
        var lesson = sop.GetProperty("lessons").EnumerateArray()
            .Single(l => l.GetProperty("recipeStepId").GetInt64() == firstStep);
        Assert.Equal([note], NoteIds(lesson));
        Assert.Equal("Level the dose over the bin, not over the cup.", lesson.GetProperty("fieldNotes")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Note_leaves_the_course_of_the_version_that_took_it_in_and_stays_with_the_others()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var firstStep = await StepIdAsync(drink.VersionId, 1);
        var takenIn = await RemarkAsync(TestUsers.Trainee, drink.RecipeId, new
        {
            source = "STAFF", category = "COMMON_MISTAKE", content = "Tapping twice packs the leaf.", recipeStepId = firstStep,
        });
        var stillOpen = await RemarkAsync("trainee2", drink.RecipeId, new
        {
            source = "STAFF", category = "PREPARATION_NOTE", content = "Pour down the side of the cup.",
            recipeStepId = await StepIdAsync(drink.VersionId, 2),
        });
        await ReviewAsync(takenIn, "accept");
        await ReviewAsync(stillOpen, "accept");

        // Version 2 is released and takes one of the two in.
        var nextVersionId = await factory.ReleaseNewVersionAsync(drink.RecipeId);
        await ReviewAsync(takenIn, "address", new { recipeVersionId = nextVersionId });
        var nextCourse = await factory.NewPublishedCourseAsync(nextVersionId);

        // The course of version 1 keeps both: version 1 says neither.
        var old = await factory.GetCourseAsync(drink.CourseId);
        Assert.Equal([takenIn], NoteIds(LessonOf(old, "SOP", firstStep)));
        Assert.Contains(stillOpen, AllNoteIds(old));

        // The course of version 2 has the one its recipe does not say. Its steps are other steps, so the
        // note is on the drink and not on a lesson.
        Assert.DoesNotContain(takenIn, AllNoteIds(nextCourse));
        Assert.Equal([stillOpen], NoteIds(nextCourse.Module("COMMON_MISTAKES")));
        Assert.Equal([stillOpen], AllNoteIds(nextCourse));
        Assert.Equal(1, nextCourse.Module("COMMON_MISTAKES").GetProperty("fieldNotes")[0].GetProperty("versionNo").GetInt32());
    }

    [Fact]
    public async Task Course_built_from_no_recipe_has_no_field_notes()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await (await trainer.PostAsJsonAsync(Courses,
                new { courseType = "INDUCTION", title = $"Hygiene {Guid.NewGuid():N}"[..20] }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Empty(AllNoteIds(course));
    }
}
