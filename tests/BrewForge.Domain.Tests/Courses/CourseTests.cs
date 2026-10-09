using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Courses;

/// <summary>
/// UC-11 to UC-13 and UC-28: BR-18, BR-19, BR-20, BR-22, BR-29, BR-30, BR-31
/// and BR-35.
/// </summary>
public sealed class CourseTests
{
    private const long Trainer = 6;
    private const long TrainingManager = 8;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly QuizOption[] Options = [new("A", "Right"), new("B", "Wrong")];

    /// <summary>A PRODUCT course in DRAFT, with ids on its modules as it would have after being stored.</summary>
    private static Course NewCourse(RecipeVersion? version = null)
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", version ?? ReleasedVersion(), Trainer);
        foreach (var module in course.Modules) WithId(module, 700 + module.ModuleOrder);
        return course;
    }

    private static CourseModule ModuleOf(Course course, ModuleType type) =>
        course.Modules.Single(module => module.ModuleType == type);

    /// <summary>Everything a trainer has to do before the course can be submitted.</summary>
    private static Course Authored(RecipeVersion? version = null)
    {
        var course = NewCourse(version);
        var technique = ModuleOf(course, ModuleType.Technique);
        foreach (var gate in technique.Lessons.ToList())
        {
            course.UpdateLesson(technique, gate, null, $"How to do the gate of {gate.Title}", null);
        }
        course.SetModuleDuration(technique, 20);
        foreach (var type in new[] { ModuleType.CommonMistakes, ModuleType.ExceptionHandling })
        {
            var module = ModuleOf(course, type);
            course.AddLesson(module, $"{type} lesson", "What goes wrong and what to do.", null);
            course.SetModuleDuration(module, 15);
        }
        course.AddQuizQuestion(ModuleOf(course, ModuleType.Sop).Id, "Which step comes first?", Options, "A");
        return course;
    }

    private static Course Published(RecipeVersion? version = null)
    {
        var course = Authored(version);
        course.Submit();
        course.Approve(TrainingManager, Now);
        return course;
    }

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- BR-29

    [Fact]
    public void BR_29_course_has_exactly_seven_modules_in_the_fixed_order()
    {
        var course = NewCourse();

        var modules = course.OrderedModules();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], modules.Select(m => m.ModuleOrder));
        Assert.Equal(
        [
            ModuleType.ProductOverview, ModuleType.Ingredients, ModuleType.Equipment, ModuleType.Sop,
            ModuleType.Technique, ModuleType.CommonMistakes, ModuleType.ExceptionHandling,
        ], modules.Select(m => m.ModuleType));
        Assert.Equal(
        [
            ModuleSource.Generated, ModuleSource.Generated, ModuleSource.Generated, ModuleSource.Generated,
            ModuleSource.Mixed, ModuleSource.Authored, ModuleSource.Authored,
        ], modules.Select(m => m.Source));
        Assert.Equal(CourseState.Draft, course.State);
    }

    [Fact]
    public void Course_that_is_not_built_from_a_recipe_has_seven_authored_modules()
    {
        var course = Course.Create(CourseType.Induction, "Welcome to the chain", version: null, Trainer);

        Assert.Equal(7, course.Modules.Count);
        Assert.All(course.Modules, module =>
        {
            Assert.Equal(ModuleSource.Authored, module.Source);
            Assert.Equal(ModuleState.Empty, module.State);
        });
        Assert.Null(course.RecipeVersionId);
    }

    // ---------------------------------------------------------------- BR-20, BR-18

    [Theory]
    [InlineData(VersionState.Draft)]
    [InlineData(VersionState.Validated)]
    [InlineData(VersionState.Superseded)]
    public void BR_20_only_a_released_version_can_be_the_source_of_a_course(VersionState state)
    {
        RecipeVersion version;
        switch (state)
        {
            case VersionState.Draft:
                version = Draft(Step(1));
                break;
            case VersionState.Validated:
                version = ValidOolongMilkTea();
                version.Submit(version.Validate(Catalog()));
                break;
            default:
                version = ReleasedVersion(versionId: 310, versionNo: 1);
                var next = ValidOolongMilkTea();
                next.Submit(next.Validate(Catalog()));
                var release = RecipeRelease.Prepare(next, version, next.Validate(Catalog()), 99, 2, Now);
                release.SupersedePrevious();
                break;
        }
        Assert.Equal(state, version.State);

        var refusal = Refused(() => Course.Create(CourseType.Product, "A course", version, Trainer));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-20", refusal.Rule);
    }

    [Fact]
    public void Product_course_requires_a_recipe_version()
    {
        var refusal = Refused(() => Course.Create(CourseType.Product, "A course", version: null, Trainer));

        Assert.Contains(refusal.Details, d => d.Field == "recipeVersionId");
    }

    [Fact]
    public void BR_18_the_binding_of_a_course_cannot_be_changed()
    {
        var first = ReleasedVersion(versionId: 310);
        var other = ReleasedVersion(versionId: 320, versionNo: 2);

        // There is no setter to call...
        Assert.False(typeof(Course).GetProperty(nameof(Course.RecipeVersionId))!.SetMethod!.IsPublic);

        // ...and the one operation that moves a binding refuses a course that is not out of date.
        var draft = NewCourse(first);
        var published = Published(first);
        Assert.All(new[] { draft, published }, course =>
        {
            var refusal = Refused(() => course.RebuildOn(other, first));
            Assert.Equal("BR-18", refusal.Rule);
            Assert.Equal(first.Id, course.RecipeVersionId);
        });
    }

    [Fact]
    public void BR_18_an_out_of_date_course_is_only_rebuilt_on_a_version_of_the_same_recipe()
    {
        var first = ReleasedVersion(versionId: 310, recipeId: 12);
        var ofAnotherRecipe = ReleasedVersion(versionId: 990, recipeId: 77);
        var course = Published(first);
        course.MarkOutOfDate();

        var refusal = Refused(() => course.RebuildOn(ofAnotherRecipe, first));

        Assert.Equal("BR-18", refusal.Rule);
        Assert.Equal(first.Id, course.RecipeVersionId);
        Assert.Equal(CourseState.OutOfDate, course.State);
    }

    // ---------------------------------------------------------------- generation, BR-22

    [Fact]
    public void Generated_modules_are_built_from_the_steps_of_the_version()
    {
        var version = ReleasedVersion();
        var course = NewCourse(version);
        var steps = version.OrderedSteps();

        // SOP: one lesson per step, in order, each keeping the link to its step.
        var sop = ModuleOf(course, ModuleType.Sop).OrderedLessons();
        Assert.Equal(steps.Select(s => (long?)s.Id), sop.Select(l => l.RecipeStepId));
        Assert.Equal(["Step 1 - brew the oolong", "Step 2 - add the milk", "Step 3 - garnish with peach"],
            sop.Select(l => l.Title));

        // TECHNIQUE: one gate item per step that has a technique gate (steps 1 and 2).
        var gates = ModuleOf(course, ModuleType.Technique).OrderedLessons();
        Assert.Equal([steps[0].Id, steps[1].Id], gates.Select(l => l.RecipeStepId!.Value));

        foreach (var type in new[] { ModuleType.ProductOverview, ModuleType.Ingredients, ModuleType.Equipment, ModuleType.Sop })
        {
            var module = ModuleOf(course, type);
            Assert.Equal(ModuleState.Complete, module.State);
            Assert.True(module.DurationMinutes > 0);
            Assert.NotEmpty(module.Lessons);
        }
        Assert.True(course.TotalDurationMin > 0);
    }

    [Fact]
    public void BR_22_no_reference_value_is_copied_into_a_generated_lesson()
    {
        var course = NewCourse();

        // Quantities, thresholds, gates and shelf-life rules are rendered from
        // the bound version; the lesson itself stores none of them.
        var generated = course.Modules.Where(m => m.Source != ModuleSource.Authored).SelectMany(m => m.Lessons).ToList();
        Assert.NotEmpty(generated);
        Assert.All(generated, lesson =>
        {
            Assert.Null(lesson.Content);
            Assert.DoesNotContain("18", lesson.Title);
            Assert.DoesNotContain("90 C", lesson.Title);
        });
    }

    // ---------------------------------------------------------------- BR-30

    [Fact]
    public void BR_30_a_generated_module_cannot_be_edited_by_the_trainer()
    {
        var course = NewCourse();
        var sop = ModuleOf(course, ModuleType.Sop);
        var lesson = sop.Lessons[0];

        Action[] attempts =
        [
            () => course.SetModuleDuration(sop, 99),
            () => course.AddLesson(sop, "My own step", "Do it my way", null),
            () => course.UpdateLesson(sop, lesson, "Renamed", "Changed", null),
            () => course.RemoveLesson(sop, lesson),
        ];

        Assert.All(attempts, attempt =>
        {
            var refusal = Refused(attempt);
            Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
            Assert.Equal("BR-30", refusal.Rule);
        });
        Assert.Equal(3, sop.Lessons.Count);
        Assert.Null(lesson.Content);
    }

    [Theory]
    [InlineData(ModuleType.CommonMistakes)]
    [InlineData(ModuleType.ExceptionHandling)]
    public void BR_30_regeneration_refuses_to_touch_an_authored_module(ModuleType type)
    {
        var version = ReleasedVersion();
        var course = NewCourse(version);
        var module = ModuleOf(course, type);
        course.AddLesson(module, "Hand-written lesson", "Written by the trainer.", null);

        var refusal = Refused(() => course.RegenerateModule(module, version));

        Assert.Equal("BR-30", refusal.Rule);
        Assert.Equal("Written by the trainer.", Assert.Single(module.Lessons).Content);
    }

    [Fact]
    public void Regenerating_a_generated_module_rebuilds_it_from_the_bound_version()
    {
        var version = ReleasedVersion();
        var course = NewCourse(version);
        var sop = ModuleOf(course, ModuleType.Sop);

        course.RegenerateModule(sop, version);

        Assert.Equal(version.OrderedSteps().Select(s => (long?)s.Id), sop.OrderedLessons().Select(l => l.RecipeStepId));
        Assert.Equal(ModuleState.Complete, sop.State);
    }

    [Fact]
    public void BR_30_rebuilding_on_a_new_version_regenerates_the_recipe_and_keeps_what_the_trainer_wrote()
    {
        var first = ReleasedVersion(versionId: 310, versionNo: 1);
        var second = ReleasedVersion(versionId: 320, versionNo: 2, oolongGrams: 16m, firstGate: "Water at 85 C");
        var course = Published(first);
        var mistakes = ModuleOf(course, ModuleType.CommonMistakes);
        var technique = ModuleOf(course, ModuleType.Technique);
        var proseOfGate1 = technique.OrderedLessons()[0].Content;

        course.MarkOutOfDate();
        course.RebuildOn(second, first);

        // The binding moved, and the course is a draft again.
        Assert.Equal(second.Id, course.RecipeVersionId);
        Assert.Equal(CourseState.Draft, course.State);
        Assert.Null(course.ApprovedBy);
        Assert.Null(course.PublishedAt);

        // Generated: rebuilt, now pointing at the steps of the new version.
        var sop = ModuleOf(course, ModuleType.Sop);
        Assert.Equal(second.OrderedSteps().Select(s => (long?)s.Id), sop.OrderedLessons().Select(l => l.RecipeStepId));
        Assert.Equal(ModuleState.Complete, sop.State);

        // Authored: untouched, word for word.
        var kept = Assert.Single(mistakes.Lessons);
        Assert.Equal("CommonMistakes lesson", kept.Title);
        Assert.Equal("What goes wrong and what to do.", kept.Content);
        Assert.Equal(15, mistakes.DurationMinutes);

        // Mixed: the gate list follows the new version, and the prose written
        // for the gate of step 1 is still attached to the gate of step 1.
        var gates = technique.OrderedLessons();
        Assert.Equal([second.OrderedSteps()[0].Id, second.OrderedSteps()[1].Id], gates.Select(l => l.RecipeStepId!.Value));
        Assert.Equal(proseOfGate1, gates[0].Content);

        // What the trainer wrote has to be looked at again before the course is resubmitted.
        Assert.Equal(ModuleState.NeedsReview, mistakes.State);
        Assert.Equal(ModuleState.NeedsReview, technique.State);
        Assert.Equal("BR-31", Refused(course.Submit).Rule);
    }

    [Fact]
    public void Gate_item_of_the_technique_module_takes_prose_but_keeps_its_title_and_cannot_be_deleted()
    {
        var course = NewCourse();
        var technique = ModuleOf(course, ModuleType.Technique);
        var gate = technique.OrderedLessons()[0];
        var title = gate.Title;
        var stepId = gate.RecipeStepId;

        course.UpdateLesson(technique, gate, "A title of my own", "Keep the kettle at a rolling simmer.", null);

        Assert.Equal(title, gate.Title);
        Assert.Equal(stepId, gate.RecipeStepId);
        Assert.Equal("Keep the kettle at a rolling simmer.", gate.Content);
        Assert.Equal("BR-30", Refused(() => course.RemoveLesson(technique, gate)).Rule);
    }

    // ---------------------------------------------------------------- BR-31, BR-19

    [Fact]
    public void BR_31_submit_is_refused_while_a_module_has_no_content()
    {
        var course = NewCourse();

        var refusal = Refused(course.Submit);

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-31", refusal.Rule);
        Assert.Equal("MSG-E12", refusal.Code);
        Assert.Equal(["modules[TECHNIQUE]", "modules[COMMON_MISTAKES]", "modules[EXCEPTION_HANDLING]"],
            refusal.Details.Select(d => d.Field));
        Assert.Equal(CourseState.Draft, course.State);
    }

    [Fact]
    public void BR_31_submit_is_refused_while_a_module_has_content_but_no_duration()
    {
        var course = Authored();
        var mistakes = ModuleOf(course, ModuleType.CommonMistakes);
        course.SetModuleDuration(mistakes, null);

        var refusal = Refused(course.Submit);

        Assert.Equal("BR-31", refusal.Rule);
        var detail = Assert.Single(refusal.Details);
        Assert.Equal("modules[COMMON_MISTAKES]", detail.Field);
        Assert.Equal("has no duration", detail.Issue);
    }

    [Fact]
    public void BR_19_a_lesson_without_content_keeps_the_course_from_being_submitted()
    {
        var course = Authored();
        var mistakes = ModuleOf(course, ModuleType.CommonMistakes);
        course.AddLesson(mistakes, "A lesson the trainer has not written yet", content: null, mediaUrl: null);

        var refusal = Refused(course.Submit);

        Assert.Equal("MSG-E12", refusal.Code);
        Assert.Contains(refusal.Details, d => d is { Field: "modules[COMMON_MISTAKES]", Issue: "has no content" });
    }

    [Fact]
    public void Course_whose_quiz_has_no_question_cannot_be_submitted()
    {
        var course = NewCourse();
        var technique = ModuleOf(course, ModuleType.Technique);
        foreach (var gate in technique.Lessons.ToList()) course.UpdateLesson(technique, gate, null, "Prose", null);
        course.SetModuleDuration(technique, 20);
        foreach (var type in new[] { ModuleType.CommonMistakes, ModuleType.ExceptionHandling })
        {
            course.AddLesson(ModuleOf(course, type), "Lesson", "Content", null);
            course.SetModuleDuration(ModuleOf(course, type), 10);
        }

        var refusal = Refused(course.Submit);

        Assert.Equal("BR-21", refusal.Rule);
        Assert.Equal(CourseState.Draft, course.State);
    }

    [Fact]
    public void Course_whose_practical_checklist_is_empty_cannot_be_submitted()
    {
        // Everything else is in place, but the trainer took every step off the checklist.
        var version = ReleasedVersion();
        var course = Authored(version);
        var technique = ModuleOf(course, ModuleType.Technique);
        course.SetPracticalChecklist([], version);
        course.AddLesson(technique, "General technique", "Work clean, work in order.", null);

        var refusal = Refused(course.Submit);

        // Nobody could pass a practical that has nothing to observe, so nobody could be certified (BR-21).
        Assert.Equal("BR-21", refusal.Rule);
        Assert.Contains(refusal.Details, d => d is { Field: "practicalChecklist", Issue: "is empty" });
        Assert.Equal(CourseState.Draft, course.State);

        // With one step back on it, the course can go for approval.
        course.SetPracticalChecklist([version.Steps[0].Id], version);
        foreach (var gate in technique.Lessons.Where(l => l.RecipeStepId is not null && l.Content is null).ToList())
        {
            course.UpdateLesson(technique, gate, null, "Prose", null);
        }
        course.Submit();
        Assert.Equal(CourseState.PendingApproval, course.State);
    }

    [Fact]
    public void Fully_authored_course_is_submitted_and_approved()
    {
        var course = Authored();

        course.Submit();
        Assert.Equal(CourseState.PendingApproval, course.State);

        course.Approve(TrainingManager, Now);
        Assert.Equal(CourseState.Published, course.State);
        Assert.Equal(TrainingManager, course.ApprovedBy);
        Assert.Equal(Now, course.PublishedAt);
    }

    // ---------------------------------------------------------------- BR-35

    [Fact]
    public void BR_35_a_question_without_a_module_is_refused()
    {
        var course = NewCourse();

        var refusal = Refused(() => course.AddQuizQuestion(null, "Untagged question?", Options, "A"));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-35", refusal.Rule);
        Assert.Contains(refusal.Details, d => d.Field == "courseModuleId");
        Assert.Equal(0, course.Quiz.QuestionCount);
    }

    [Fact]
    public void BR_35_a_question_tagged_with_a_module_of_another_course_is_refused()
    {
        var course = NewCourse();

        var refusal = Refused(() => course.AddQuizQuestion(424242, "Whose module is this?", Options, "A"));

        Assert.Equal("BR-35", refusal.Rule);
        Assert.Empty(course.Quiz.Questions);
    }

    [Fact]
    public void Question_carries_the_module_it_tests()
    {
        var course = NewCourse();
        var technique = ModuleOf(course, ModuleType.Technique);

        var question = course.AddQuizQuestion(technique.Id, "How hot is the water?", [new("A", "90 C"), new("B", "100 C")], "A");

        Assert.Equal(technique.Id, question.CourseModuleId);
        Assert.Equal(1, course.Quiz.QuestionCount);
        Assert.Equal(["A", "B"], question.Options().Select(o => o.Key));
        Assert.Equal("A", question.CorrectOption);
    }

    [Fact]
    public void Question_needs_two_options_and_a_correct_one_among_them()
    {
        var course = NewCourse();
        var moduleId = ModuleOf(course, ModuleType.Sop).Id;

        Assert.Contains(Refused(() => course.AddQuizQuestion(moduleId, "One option?", [new("A", "Only")], "A")).Details,
            d => d.Field == "options");
        Assert.Contains(Refused(() => course.AddQuizQuestion(moduleId, "Wrong key?", Options, "Z")).Details,
            d => d.Field == "correctOption");
        Assert.Contains(Refused(() => course.AddQuizQuestion(moduleId, "Same key twice?", [new("A", "x"), new("A", "y")], "A")).Details,
            d => d.Field == "options");
        Assert.Contains(Refused(() => course.AddQuizQuestion(moduleId, "", Options, "A")).Details,
            d => d.Field == "questionText");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Pass_score_is_a_percentage(int passScore)
    {
        var course = NewCourse();

        var refusal = Refused(() => course.UpdateQuiz(null, passScore));

        Assert.Contains(refusal.Details, d => d.Field == "passScore");
        Assert.Equal(80, course.Quiz.PassScore);
    }

    // ---------------------------------------------------------------- practical checklist

    [Fact]
    public void Practical_checklist_is_made_of_steps_of_the_bound_version()
    {
        var version = ReleasedVersion();
        var course = NewCourse(version);
        var technique = ModuleOf(course, ModuleType.Technique);
        var steps = version.OrderedSteps();
        course.UpdateLesson(technique, technique.OrderedLessons()[0], null, "Prose for the gate of step 1", null);

        // Observe steps 1 and 3; step 2 comes off the list.
        course.SetPracticalChecklist([steps[0].Id, steps[2].Id], version);

        var items = technique.Lessons.Where(l => l.RecipeStepId is not null).OrderBy(l => l.LessonOrder).ToList();
        Assert.Equal([steps[0].Id, steps[2].Id], items.Select(l => l.RecipeStepId!.Value));
        Assert.Equal("Prose for the gate of step 1", items[0].Content); // kept
        Assert.Null(items[1].Content);

        var refusal = Refused(() => course.SetPracticalChecklist([424242], version));
        Assert.Contains(refusal.Details, d => d.Field == "items");
    }

    // ---------------------------------------------------------------- the state model

    [Fact]
    public void Course_is_edited_in_draft_only()
    {
        var pending = Authored();
        pending.Submit();
        var published = Published();

        foreach (var course in new[] { pending, published })
        {
            var mistakes = ModuleOf(course, ModuleType.CommonMistakes);
            Action[] edits =
            [
                () => course.AddLesson(mistakes, "Late addition", "Content", null),
                () => course.SetModuleDuration(mistakes, 30),
                () => course.UpdateQuiz("New title", 70),
                () => course.AddQuizQuestion(mistakes.Id, "Late question?", Options, "A"),
            ];
            Assert.All(edits, edit => Assert.Equal(Course.StateRule, Refused(edit).Rule));
        }
    }

    [Fact]
    public void Transitions_outside_the_state_model_are_refused()
    {
        var draft = NewCourse();
        Assert.Equal(Course.StateRule, Refused(() => draft.Approve(TrainingManager, Now)).Rule);
        Assert.Equal(Course.StateRule, Refused(draft.Return).Rule);
        Assert.Equal(Course.StateRule, Refused(draft.MarkOutOfDate).Rule);
        Assert.Equal(Course.StateRule, Refused(draft.Archive).Rule);

        var published = Published();
        Assert.Equal(Course.StateRule, Refused(published.Submit).Rule);
        Assert.Equal(Course.StateRule, Refused(published.Return).Rule);

        published.Archive();
        Assert.Equal(CourseState.Archived, published.State);
        Assert.Equal(Course.StateRule, Refused(published.MarkOutOfDate).Rule);
    }

    [Fact]
    public void Returned_course_is_a_draft_again_and_can_be_resubmitted()
    {
        var course = Authored();
        course.Submit();

        course.Return();

        Assert.Equal(CourseState.Draft, course.State);
        course.Submit();
        Assert.Equal(CourseState.PendingApproval, course.State);
    }

    [Fact]
    public void Lesson_needs_a_title_and_a_web_address_for_its_media()
    {
        var course = NewCourse();
        var mistakes = ModuleOf(course, ModuleType.CommonMistakes);

        Assert.Contains(Refused(() => course.AddLesson(mistakes, " ", "Content", null)).Details, d => d.Field == "title");
        Assert.Contains(Refused(() => course.AddLesson(mistakes, "Title", "Content", "C:\\videos\\pour.mp4")).Details,
            d => d.Field == "mediaUrl");

        var lesson = course.AddLesson(mistakes, "Title", "Content", "https://video.example/embed/pour");
        Assert.Equal("https://video.example/embed/pour", lesson.MediaUrl);
    }
}
