using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Courses;
using BrewForge.Domain.Courses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice4;

/// <summary>
/// UC-11 to UC-13 and UC-28 through the API: BR-18, BR-19, BR-20, BR-22,
/// BR-29, BR-30, BR-31 and BR-35.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CourseApiTests(BrewForgeApiFactory factory)
{
    private static JsonElement Lesson(JsonElement module, int index) => module.GetProperty("lessons")[index];

    private static JsonElement Reference(JsonElement module, int lessonIndex = 0) =>
        Lesson(module, lessonIndex).GetProperty("reference");

    // ---------------------------------------------------------------- UC-11, BR-29

    [Fact]
    public async Task Course_is_created_as_a_draft_with_seven_modules_in_the_fixed_order()
    {
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);

        var course = await factory.NewCourseAsync(versionId);

        Assert.Equal("DRAFT", course.GetProperty("state").GetString());
        Assert.Equal("PRODUCT", course.GetProperty("courseType").GetString());
        Assert.Equal(versionId, course.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(recipeId, course.GetProperty("recipeId").GetInt64());
        Assert.Equal(1, course.GetProperty("versionNo").GetInt32());
        Assert.Equal(trainerId, course.GetProperty("createdBy").GetInt64());
        Assert.True(course.GetProperty("totalDurationMin").GetInt32() > 0);

        var modules = course.GetProperty("modules").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], modules.Select(m => m.GetProperty("moduleOrder").GetInt32()));
        Assert.Equal(
            ["PRODUCT_OVERVIEW", "INGREDIENTS", "EQUIPMENT", "SOP", "TECHNIQUE", "COMMON_MISTAKES", "EXCEPTION_HANDLING"],
            modules.Select(m => m.GetProperty("moduleType").GetString()));
        Assert.Equal(["GENERATED", "GENERATED", "GENERATED", "GENERATED", "MIXED", "AUTHORED", "AUTHORED"],
            modules.Select(m => m.GetProperty("source").GetString()));
        Assert.Equal(["COMPLETE", "COMPLETE", "COMPLETE", "COMPLETE", "DRAFT", "EMPTY", "EMPTY"],
            modules.Select(m => m.GetProperty("state").GetString()));

        Assert.Equal(80, course.GetProperty("quiz").GetProperty("passScore").GetInt32());
        Assert.Equal(0, course.GetProperty("quiz").GetProperty("questionCount").GetInt32());
    }

    [Fact]
    public async Task Sop_module_has_one_lesson_per_step_linked_to_its_step()
    {
        var (_, versionId) = await factory.NewReleasedRecipeAsync();
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray().ToList();

        var sop = (await factory.NewCourseAsync(versionId)).Module("SOP");

        var lessons = sop.GetProperty("lessons").EnumerateArray().ToList();
        Assert.Equal(steps.Select(s => s.Id()), lessons.Select(l => l.GetProperty("recipeStepId").GetInt64()));
        Assert.Equal("Step 1 - brew the oolong", lessons[0].GetProperty("title").GetString());
        Assert.All(lessons, lesson => Assert.True(lesson.GetProperty("generated").GetBoolean()));

        // The shape of the contract's CourseModule.
        Assert.Equal(4, sop.GetProperty("moduleOrder").GetInt32());
        Assert.True(sop.GetProperty("durationMinutes").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, lessons[0].GetProperty("mediaUrl").ValueKind);
    }

    // ---------------------------------------------------------------- BR-22

    [Fact]
    public async Task BR_22_lessons_show_the_reference_values_of_the_bound_version()
    {
        var course = await factory.NewCourseAsync();

        var overview = Reference(course.Module("PRODUCT_OVERVIEW"));
        Assert.Equal(1, overview.GetProperty("versionNo").GetInt32());
        Assert.Equal("TEA", overview.GetProperty("category").GetString());
        Assert.Equal(3, overview.GetProperty("stepCount").GetInt32());

        var oolong = Reference(course.Module("INGREDIENTS")).GetProperty("ingredients").EnumerateArray()
            .Single(i => i.GetProperty("ingredientCode").GetString() == "ING-OOLONG");
        Assert.Equal(18m, oolong.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal("g", oolong.GetProperty("unit").GetString());
        Assert.Equal(8, oolong.GetProperty("shelfLifeHours").GetInt32());
        Assert.Equal([1], oolong.GetProperty("usedInSteps").EnumerateArray().Select(s => s.GetInt32()));

        var brewer = Assert.Single(Reference(course.Module("EQUIPMENT")).GetProperty("equipment").EnumerateArray());
        Assert.Equal("TEA_BREWER", brewer.GetProperty("equipmentClass").GetString());
        Assert.Equal(15m, brewer.GetProperty("minThreshold").GetDecimal());
        Assert.Equal(25m, brewer.GetProperty("maxThreshold").GetDecimal());

        var step1 = Reference(course.Module("SOP"));
        Assert.Equal("Brew the oolong", step1.GetProperty("actionText").GetString());
        Assert.Equal("Water at 90 C", step1.GetProperty("techniqueGate").GetString());
        Assert.Equal(480, step1.GetProperty("durationSeconds").GetInt32());
        Assert.Contains(step1.GetProperty("ingredients").EnumerateArray(),
            i => i.GetProperty("ingredientCode").GetString() == "ING-OOLONG" && i.GetProperty("quantity").GetDecimal() == 18m);

        var gate = Reference(course.Module("TECHNIQUE"));
        Assert.Equal("Water at 90 C", gate.GetProperty("techniqueGate").GetString());
    }

    [Fact]
    public async Task BR_22_reference_values_are_not_stored_with_the_lesson()
    {
        var courseId = (await factory.NewCourseAsync()).Id();

        var generated = await factory.WithDbAsync(db => db.Courses
            .Where(c => c.Id == courseId)
            .SelectMany(c => c.Modules.Where(m => m.Source != ModuleSource.Authored).SelectMany(m => m.Lessons))
            .ToListAsync());

        Assert.NotEmpty(generated);
        Assert.All(generated, lesson => Assert.Null(lesson.Content));
    }

    [Fact]
    public async Task BR_22_changing_the_catalogue_changes_what_the_lesson_shows_without_touching_the_lesson()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = UniqueCode("ING-");
        var ingredient = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = code, name = "House syrup", unit = "ml", shelfLifeHours = 24 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var (_, versionId) = await factory.NewReleasedRecipeAsync(new
        {
            steps = new[] { Step(1, "Add the house syrup", seconds: 10, uses: [Use(code, 20m, "ml")]) },
        });
        var courseId = (await factory.NewCourseAsync(versionId)).Id();
        JsonElement Syrup(JsonElement course) => Reference(course.Module("INGREDIENTS")).GetProperty("ingredients")
            .EnumerateArray().Single(i => i.GetProperty("ingredientCode").GetString() == code);
        Assert.Equal(24, Syrup(await factory.GetCourseAsync(courseId)).GetProperty("shelfLifeHours").GetInt32());

        // The shelf-life rule changes in the catalogue; nobody edits the course.
        await (await admin.PutAsJsonAsync($"/api/v1/ingredients/{ingredient.Id()}",
                new { name = "House syrup", unit = "ml", shelfLifeHours = 6 }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(6, Syrup(await factory.GetCourseAsync(courseId)).GetProperty("shelfLifeHours").GetInt32());
    }

    [Fact]
    public async Task BR_22_and_BR_30_rebuilding_on_a_new_version_changes_what_lessons_show_and_keeps_authored_content()
    {
        var (recipeId, firstVersion) = await factory.NewReleasedRecipeAsync();   // 18 g, gate "Water at 90 C"
        var courseId = (await factory.NewPublishedCourseAsync(firstVersion)).Id();
        var before = await factory.GetCourseAsync(courseId);
        Assert.Equal(18m, Reference(before.Module("SOP")).GetProperty("ingredients")[0].GetProperty("quantity").GetDecimal());

        // A new version of the recipe is released: 16 g, gate "Water at 85 C".
        var secondVersion = await factory.ReleaseNewVersionAsync(recipeId, LighterContent());
        await factory.WithDbAsync(async db =>
        {
            var course = await db.Courses.SingleAsync(c => c.Id == courseId);
            course.MarkOutOfDate();
            await db.SaveChangesAsync();
        });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CourseService>().RebuildAsync(courseId, CancellationToken.None);
        }

        var after = await factory.GetCourseAsync(courseId);
        Assert.Equal("DRAFT", after.GetProperty("state").GetString());
        Assert.Equal(secondVersion, after.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(2, after.GetProperty("versionNo").GetInt32());

        // What the lessons show comes from the new version...
        var step1 = Reference(after.Module("SOP"));
        Assert.Equal("Brew a lighter oolong", step1.GetProperty("actionText").GetString());
        Assert.Equal(16m, step1.GetProperty("ingredients")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("Water at 85 C", Reference(after.Module("TECHNIQUE")).GetProperty("techniqueGate").GetString());
        Assert.Equal(2, after.Module("SOP").GetProperty("lessons").GetArrayLength());

        // ...while what the trainer wrote is still there, and marked for review.
        var mistakes = after.Module("COMMON_MISTAKES");
        Assert.Equal("What goes wrong, and what to do about it.", Lesson(mistakes, 0).GetProperty("content").GetString());
        Assert.Equal("NEEDS_REVIEW", mistakes.GetProperty("state").GetString());
        Assert.Equal("Watch the trainer demonstrate this, then repeat it three times.",
            Lesson(after.Module("TECHNIQUE"), 0).GetProperty("content").GetString());
        Assert.Equal("COMPLETE", after.Module("SOP").GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- BR-20, BR-18

    [Fact]
    public async Task BR_20_a_course_cannot_be_built_from_a_version_that_is_not_released()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var (_, draftVersion) = await factory.NewDraftAsync(ValidContent());

        var response = await trainer.PostAsJsonAsync(Courses,
            new { recipeVersionId = draftVersion, courseType = "PRODUCT", title = "Too early" });

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-20");
    }

    [Fact]
    public async Task BR_18_a_second_product_course_on_the_same_version_is_409_pointing_at_the_first()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var (_, versionId) = await factory.NewReleasedRecipeAsync();
        var first = (await factory.NewCourseAsync(versionId)).Id();

        var response = await trainer.PostAsJsonAsync(Courses,
            new { recipeVersionId = versionId, courseType = "PRODUCT", title = "A duplicate" });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-W04", "BR-18");
        var detail = Assert.Single(envelope.GetProperty("details").EnumerateArray());
        Assert.Equal("existingCourseId", detail.GetProperty("field").GetString());
        Assert.Equal(first.ToString(), detail.GetProperty("issue").GetString());
    }

    [Fact]
    public async Task Product_course_without_a_version_is_400()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var response = await trainer.PostAsJsonAsync(Courses, new { courseType = "PRODUCT", title = "No recipe" });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("recipeVersionId", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- BR-30

    [Fact]
    public async Task BR_30_a_generated_module_and_its_lessons_cannot_be_edited()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var sop = course.Module("SOP");
        var lessonId = Lesson(sop, 0).Id();

        await (await trainer.PutAsJsonAsync($"{Modules}/{sop.Id()}", new { durationMinutes = 99 }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");
        await (await trainer.PostAsJsonAsync($"{Modules}/{sop.Id()}/lessons", new { title = "My own step", content = "My way" }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");
        await (await trainer.PutAsJsonAsync($"{Lessons}/{lessonId}", new { title = "Renamed", content = "Changed" }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");
        await (await trainer.DeleteAsync($"{Lessons}/{lessonId}"))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");

        var unchanged = (await factory.GetCourseAsync(course.Id())).Module("SOP");
        Assert.Equal(sop.GetRawText(), unchanged.GetRawText());
    }

    [Fact]
    public async Task BR_30_regenerate_rebuilds_a_generated_module_and_refuses_an_authored_one()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var mistakes = course.Module("COMMON_MISTAKES");
        await (await trainer.PostAsJsonAsync($"{Modules}/{mistakes.Id()}/lessons",
                new { title = "Over-steeping", content = "Written by the trainer." }))
            .ShouldBeAsync(HttpStatusCode.Created);

        var regenerated = await (await trainer.PostAsync($"{Modules}/{course.Module("SOP").Id()}/regenerate", null))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(3, regenerated.GetProperty("lessons").GetArrayLength());
        Assert.Equal("COMPLETE", regenerated.GetProperty("state").GetString());

        await (await trainer.PostAsync($"{Modules}/{mistakes.Id()}/regenerate", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-30");
        var kept = Lesson((await factory.GetCourseAsync(course.Id())).Module("COMMON_MISTAKES"), 0);
        Assert.Equal("Written by the trainer.", kept.GetProperty("content").GetString());
    }

    // ---------------------------------------------------------------- UC-12

    [Fact]
    public async Task Authored_lessons_are_added_changed_and_removed()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var moduleId = course.Module("EXCEPTION_HANDLING").Id();

        var created = await (await trainer.PostAsJsonAsync($"{Modules}/{moduleId}/lessons", new
            {
                title = "The brewer does not heat", content = "Switch to the backup kettle.",
                mediaUrl = "https://video.example/embed/backup-kettle",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(1, created.GetProperty("lessonOrder").GetInt32());
        Assert.False(created.GetProperty("generated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("recipeStepId").ValueKind);

        var updated = await (await trainer.PutAsJsonAsync($"{Lessons}/{created.Id()}",
                new { title = "The brewer does not heat up", content = "Use the backup kettle and tell the manager." }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("The brewer does not heat up", updated.GetProperty("title").GetString());

        var module = await (await trainer.PutAsJsonAsync($"{Modules}/{moduleId}", new { durationMinutes = 15 }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("COMPLETE", module.GetProperty("state").GetString());
        Assert.Single(await (await trainer.GetAsync($"{Modules}/{moduleId}/lessons")).ShouldBeAsync(HttpStatusCode.OK) is var list
            ? list.EnumerateArray() : []);

        await (await trainer.DeleteAsync($"{Lessons}/{created.Id()}")).ShouldBeAsync(HttpStatusCode.NoContent);
        var afterDelete = (await factory.GetCourseAsync(course.Id())).Module("EXCEPTION_HANDLING");
        Assert.Empty(afterDelete.GetProperty("lessons").EnumerateArray());
        Assert.NotEqual("COMPLETE", afterDelete.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Media_must_be_a_web_address_not_an_uploaded_video_path()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();

        var response = await trainer.PostAsJsonAsync($"{Modules}/{course.Module("COMMON_MISTAKES").Id()}/lessons",
            new { title = "Pouring", content = "Watch the pour.", mediaUrl = "C:\\videos\\pour.mp4" });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("mediaUrl", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- UC-13, BR-35

    [Fact]
    public async Task BR_35_every_quiz_question_carries_the_module_it_tests()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var otherCourse = await factory.NewCourseAsync();
        var questions = $"{Courses}/{course.Id()}/quiz/questions";
        object Question(long? moduleId) => new
        {
            courseModuleId = moduleId, questionText = "How hot is the water for the oolong?",
            options = new[] { new { key = "A", text = "90 C" }, new { key = "B", text = "100 C" } }, correctOption = "A",
        };

        await (await trainer.PostAsJsonAsync(questions, Question(null)))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-35");
        await (await trainer.PostAsJsonAsync(questions, Question(otherCourse.Module("TECHNIQUE").Id())))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-35");

        var techniqueId = course.Module("TECHNIQUE").Id();
        var created = await (await trainer.PostAsJsonAsync(questions, Question(techniqueId)))
            .ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(techniqueId, created.GetProperty("courseModuleId").GetInt64());
        Assert.Equal("TECHNIQUE", created.GetProperty("moduleType").GetString());

        var list = await (await trainer.GetAsync(questions)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.All(list.EnumerateArray(), q => Assert.True(q.GetProperty("courseModuleId").GetInt64() > 0));
        Assert.Single(list.EnumerateArray());
        var quiz = await (await trainer.GetAsync($"{Courses}/{course.Id()}/quiz")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, quiz.GetProperty("questionCount").GetInt32());
    }

    [Fact]
    public async Task Pass_score_of_the_quiz_is_set_by_the_trainer()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();
        var quiz = $"{Courses}/{course.Id()}/quiz";

        var updated = await (await trainer.PutAsJsonAsync(quiz, new { passScore = 70 })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(70, updated.GetProperty("passScore").GetInt32());

        var envelope = await (await trainer.PutAsJsonAsync(quiz, new { passScore = 120 }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("passScore", envelope.DetailFields());
    }

    [Fact]
    public async Task Practical_checklist_items_are_steps_of_the_bound_version()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var (_, versionId) = await factory.NewReleasedRecipeAsync();
        var steps = (await factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray().ToList();
        var course = await factory.NewCourseAsync(versionId);
        var checklist = $"{Courses}/{course.Id()}/practical-checklist";

        // By default: the steps that have a technique gate (1 and 2).
        Assert.Equal([1, 2], course.GetProperty("practicalChecklist").EnumerateArray().Select(i => i.GetProperty("stepOrder").GetInt32()));

        var items = await (await trainer.PutAsJsonAsync(checklist,
                new { items = new[] { new { recipeStepId = steps[0].Id() }, new { recipeStepId = steps[2].Id() } } }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal([1, 3], items.EnumerateArray().Select(i => i.GetProperty("stepOrder").GetInt32()));
        Assert.Equal("Water at 90 C", items[0].GetProperty("techniqueGate").GetString());
        Assert.Equal("Garnish with peach", items[1].GetProperty("actionText").GetString());

        var envelope = await (await trainer.PutAsJsonAsync(checklist, new { items = new[] { new { recipeStepId = 424242L } } }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("items", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- BR-31, UC-28

    [Fact]
    public async Task BR_31_submit_is_409_while_a_module_is_empty()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewCourseAsync();

        var response = await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null);

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E12", "BR-31");
        Assert.Equal(["modules[TECHNIQUE]", "modules[COMMON_MISTAKES]", "modules[EXCEPTION_HANDLING]"],
            envelope.DetailFields());
        Assert.Equal("DRAFT", (await factory.GetCourseAsync(course.Id())).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Complete_course_is_submitted_then_approved_by_the_training_manager()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var managerId = await factory.UserIdAsync(TestUsers.TrainingManager);
        var courseId = (await factory.NewCourseAsync()).Id();
        await factory.CompleteAuthoringAsync(courseId);

        var submitted = await (await trainer.PostAsync($"{Courses}/{courseId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PENDING_APPROVAL", submitted.GetProperty("state").GetString());
        Assert.All(submitted.GetProperty("modules").EnumerateArray(),
            module => Assert.Equal("COMPLETE", module.GetProperty("state").GetString()));

        // The approval queue (SCR-30).
        var queue = await (await manager.GetAsync($"{Courses}?state=PENDING_APPROVAL&size=100")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(queue.GetProperty("items").EnumerateArray(), c => c.Id() == courseId);

        // The trainer cannot approve, and cannot edit while it is pending.
        await (await trainer.PostAsync($"{Courses}/{courseId}/approve", null)).ShouldBeErrorAsync(HttpStatusCode.Forbidden, "MSG-E01");
        await (await trainer.PutAsJsonAsync($"{Courses}/{courseId}/quiz", new { passScore = 50 }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");

        var approved = await (await manager.PostAsync($"{Courses}/{courseId}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PUBLISHED", approved.GetProperty("state").GetString());
        Assert.Equal(managerId, approved.GetProperty("approvedBy").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("publishedAt").ValueKind);

        // A published course is never edited in place.
        var mistakes = approved.Module("COMMON_MISTAKES").Id();
        await (await trainer.PostAsJsonAsync($"{Modules}/{mistakes}/lessons", new { title = "Late", content = "Too late" }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
        await (await manager.PostAsync($"{Courses}/{courseId}/approve", null))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "STATE_TRANSITION");
    }

    [Fact]
    public async Task Returned_course_goes_back_to_draft_and_the_comment_is_kept_in_the_audit_log()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        var courseId = (await factory.NewCourseAsync()).Id();
        await factory.CompleteAuthoringAsync(courseId);
        await (await trainer.PostAsync($"{Courses}/{courseId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);

        var missing = await (await manager.PostAsJsonAsync($"{Courses}/{courseId}/return", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("comment", missing.DetailFields());

        var returned = await (await manager.PostAsJsonAsync($"{Courses}/{courseId}/return",
                new { comment = "The exception handling module is too thin." }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("DRAFT", returned.GetProperty("state").GetString());

        var payload = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "Course" && a.EntityId == courseId && a.Action == "RETURN")
            .Select(a => a.PayloadJson).SingleAsync());
        Assert.Contains("The exception handling module is too thin.", payload);
    }

    [Fact]
    public async Task Course_cannot_be_deleted()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var courseId = (await factory.NewCourseAsync()).Id();

        await (await trainer.DeleteAsync($"{Courses}/{courseId}")).ShouldBeErrorAsync(HttpStatusCode.MethodNotAllowed);
        await factory.GetCourseAsync(courseId);
    }

    [Fact]
    public async Task Course_without_a_recipe_has_seven_modules_for_the_trainer_to_write()
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var course = await (await trainer.PostAsJsonAsync(Courses, new { courseType = "INDUCTION", title = "Welcome to BrewForge" }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(JsonValueKind.Null, course.GetProperty("recipeVersionId").ValueKind);
        Assert.Equal(7, course.GetProperty("modules").GetArrayLength());
        Assert.All(course.GetProperty("modules").EnumerateArray(),
            module => Assert.Equal("AUTHORED", module.GetProperty("source").GetString()));
        Assert.Empty(course.GetProperty("practicalChecklist").EnumerateArray());
    }
}
