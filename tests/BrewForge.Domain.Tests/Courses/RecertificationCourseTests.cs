using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Courses;

/// <summary>
/// A RECERTIFICATION course is for staff certified on the version before the
/// one it is bound to. It teaches what differs between the two, not the
/// drink from the start.
/// </summary>
public sealed class RecertificationCourseTests
{
    private const long Trainer = 6;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly QuizOption[] Options = [new("A", "Right"), new("B", "Wrong")];

    /// <summary>Version 1 of the fixture drink.</summary>
    private static RecipeVersion First() => ReleasedVersion(versionId: 310, versionNo: 1);

    /// <summary>Version 2: the first step brews less leaf in cooler water; the other two steps are as they were.</summary>
    private static RecipeVersion Second() =>
        ReleasedVersion(versionId: 320, versionNo: 2, oolongGrams: 16m, firstGate: "Water at 85 C");

    private static Course Recertification(RecipeVersion version, RecipeVersion? previous)
    {
        var course = Course.Create(CourseType.Recertification, "Oolong milk tea - what changed", version, Trainer, previous);
        foreach (var module in course.Modules) WithId(module, 900 + module.ModuleOrder);
        return course;
    }

    private static CourseModule ModuleOf(Course course, ModuleType type) =>
        course.Modules.Single(module => module.ModuleType == type);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    [Fact]
    public void Recertification_course_teaches_the_steps_that_changed_and_not_the_others()
    {
        var (first, second) = (First(), Second());

        var course = Recertification(second, first);
        var whole = Course.Create(CourseType.Product, "Oolong milk tea", second, Trainer);

        // BR-29 still: seven modules, generated and authored as on any course built from a version.
        Assert.Equal(7, course.Modules.Count);
        Assert.Equal(second.Id, course.RecipeVersionId);
        Assert.Equal(whole.OrderedModules().Select(m => m.Source), course.OrderedModules().Select(m => m.Source));

        // One step changed, so one step is taught and one gate is observed; the whole course has all three.
        var changedStep = second.Steps.Single(step => step.StepOrder == 1).Id;
        Assert.Equal([(long?)changedStep], ModuleOf(course, ModuleType.Sop).Lessons.Select(lesson => lesson.RecipeStepId));
        Assert.Equal([(long?)changedStep], ModuleOf(course, ModuleType.Technique).Lessons.Select(lesson => lesson.RecipeStepId));
        Assert.Equal([changedStep], course.PracticalChecklistItemIds());
        Assert.Equal(3, ModuleOf(whole, ModuleType.Sop).Lessons.Count);
        Assert.Equal("What changed since version 1", ModuleOf(course, ModuleType.ProductOverview).Lessons.Single().Title);
        Assert.True(ModuleOf(course, ModuleType.Sop).DurationMinutes < ModuleOf(whole, ModuleType.Sop).DurationMinutes);
    }

    [Fact]
    public void Step_that_is_gone_is_named_in_the_procedure_and_is_not_a_step_to_practise()
    {
        var first = First();
        var second = Released(versionId: 320, versionNo: 2,
            new StepSpec(1, "Brew the oolong", "TEA_BREWER", "Water at 90 C", 480,
                [new IngredientSpec(Oolong, 18m, "g"), new IngredientSpec(Water, 300m, "ml")], []),
            new StepSpec(2, "Add the milk", null, "Pour down the side of the cup", 20,
                [new IngredientSpec(Milk, 120m, "ml")], [new DependencySpec(1, DependencyType.FinishToStart)]));

        var course = Recertification(second, first);

        var lesson = Assert.Single(ModuleOf(course, ModuleType.Sop).Lessons);
        Assert.Equal(("No longer done - garnish with peach", (long?)null), (lesson.Title, lesson.RecipeStepId));
        Assert.Empty(course.PracticalChecklistItemIds());
    }

    [Fact]
    public void Recertification_needs_a_version_and_an_earlier_one_to_have_been_certified_on()
    {
        var first = First();

        var noVersion = Refused(() => Course.Create(CourseType.Recertification, "What changed", version: null, Trainer));
        var firstVersion = Refused(() => Recertification(first, previous: null));

        Assert.Contains(noVersion.Details, d => d.Field == "recipeVersionId");
        Assert.Equal((ErrorKind.RuleViolation, Course.RecertificationRule), (firstVersion.Kind, firstVersion.Rule));
    }

    [Fact]
    public void Recertification_is_refused_when_nothing_changed_between_the_two_versions()
    {
        var first = First();
        var same = ReleasedVersion(versionId: 320, versionNo: 2);

        var refusal = Refused(() => Recertification(same, first));

        Assert.Equal((ErrorKind.RuleViolation, Course.RecertificationRule), (refusal.Kind, refusal.Rule));
    }

    [Fact]
    public void BR_20_a_recertification_course_too_is_built_only_on_a_released_version()
    {
        var first = First();
        var draft = RecipeVersion.CreateDraft(first.RecipeId, versionNo: 2, Author);
        draft.ReplaceContent([new StepSpec(1, "Brew it differently", null, null, 60, [], [])], Author);

        Assert.Equal("BR-20", Refused(() => Recertification(draft, first)).Rule);
    }

    [Fact]
    public void Regenerating_a_module_of_a_recertification_course_keeps_it_to_what_changed()
    {
        var (first, second) = (First(), Second());
        var course = Recertification(second, first);
        var sop = ModuleOf(course, ModuleType.Sop);

        course.RegenerateModule(sop, second, first);

        Assert.Single(sop.Lessons);
        // Without the earlier version there is no difference to regenerate from.
        Assert.Equal(Course.RecertificationRule, Refused(() => course.RegenerateModule(sop, second)).Rule);
    }

    [Fact]
    public void Recertification_course_is_not_rebuilt_on_a_third_version()
    {
        var (first, second) = (First(), Second());
        var third = ReleasedVersion(versionId: 330, versionNo: 3, oolongGrams: 20m);
        var course = Recertification(second, first);
        var technique = ModuleOf(course, ModuleType.Technique);
        foreach (var gate in technique.Lessons.ToList()) course.UpdateLesson(technique, gate, null, "How the gate is done now.", null);
        course.SetModuleDuration(technique, 10);
        foreach (var type in new[] { ModuleType.CommonMistakes, ModuleType.ExceptionHandling })
        {
            course.AddLesson(ModuleOf(course, type), "What tends to go wrong now", "Old habits.", null);
            course.SetModuleDuration(ModuleOf(course, type), 5);
        }
        course.AddQuizQuestion(ModuleOf(course, ModuleType.Sop).Id, "How much leaf now?", Options, "A");
        course.Submit();
        course.Approve(8, Now);
        course.MarkOutOfDate();

        var refusal = Refused(() => course.RebuildOn(third, second));

        Assert.Equal(Course.RecertificationRule, refusal.Rule);
        Assert.Equal((CourseState.OutOfDate, (long?)second.Id), (course.State, course.RecipeVersionId));
    }

    /// <summary>A RELEASED version of the fixture recipe with the given steps, with ids as after being stored.</summary>
    private static RecipeVersion Released(long versionId, int versionNo, params StepSpec[] steps)
    {
        var version = RecipeVersion.CreateDraft(recipeId: 12, versionNo, Author);
        version.ReplaceContent(steps, Author);
        WithId(version, versionId);
        foreach (var step in version.Steps) WithId(step, versionId * 10 + step.StepOrder);

        var report = version.Validate(Catalog());
        version.Submit(report);
        var release = RecipeRelease.Prepare(version, null, report, approverId: Author + 100, versionNo, Now);
        release.SupersedePrevious();
        release.Seal();
        return version;
    }
}
