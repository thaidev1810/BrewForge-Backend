using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Training;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Training;

/// <summary>UC-14, lesson by lesson: progress is recorded per lesson and completes the module.</summary>
public sealed class LessonProgressTests
{
    private const long Trainer = 6;
    private const long Trainee = 61;
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly TrainingRules Rules = TrainingRules.Default;

    /// <summary>A published course with ids on its modules and lessons, as stored.</summary>
    private static Course PublishedCourse()
    {
        var course = WithId(Course.Create(CourseType.Product, "Oolong milk tea", ReleasedVersion(), Trainer), 20);
        foreach (var module in course.Modules) WithId(module, 200 + module.ModuleOrder);
        foreach (var module in course.Modules.Where(m => m.Source != ModuleSource.Generated))
        {
            foreach (var lesson in module.Lessons.ToList()) course.UpdateLesson(module, lesson, null, "Prose", null);
            if (module.Lessons.Count == 0) course.AddLesson(module, "Lesson", "Content", null);
            course.SetModuleDuration(module, 10);
        }
        var lessonId = 5000L;
        foreach (var lesson in course.Modules.SelectMany(m => m.Lessons)) WithId(lesson, lessonId++);
        QuizOption[] options = [new("A", "Right"), new("B", "Wrong")];
        var questionId = 9000L;
        foreach (var module in course.Modules) WithId(course.AddQuizQuestion(module.Id, "Which?", options, "A"), questionId++);
        WithId(course.Quiz, 2000);
        course.Submit();
        course.Approve(8, Now);
        return course;
    }

    private static CourseModule Sop(Course course) => course.Modules.Single(m => m.ModuleType == ModuleType.Sop);

    [Fact]
    public void Reading_every_lesson_of_a_module_completes_the_module()
    {
        var course = PublishedCourse();
        var enrollment = Enrollment.Assign(course, Trainee, null, Rules, Now);
        var sop = Sop(course);
        var lessons = sop.Lessons.OrderBy(l => l.LessonOrder).ToList();
        Assert.Equal(3, lessons.Count); // one per step of the recipe

        // The first lesson starts the course; the module is not complete yet.
        Assert.False(enrollment.CompleteLesson(course, lessons[0].Id, Now));
        Assert.Equal(EnrollmentState.InProgress, enrollment.State);
        Assert.True(enrollment.HasRead(sop.Id, lessons[0].Id));
        Assert.False(enrollment.HasRead(sop.Id, lessons[1].Id));
        Assert.Equal(0, enrollment.ProgressPercent);

        Assert.False(enrollment.CompleteLesson(course, lessons[1].Id, Now));
        // Reading the same lesson again changes nothing.
        Assert.False(enrollment.CompleteLesson(course, lessons[1].Id, Now.AddMinutes(5)));
        Assert.Equal(2, enrollment.Lessons.Count);

        // The last one completes the module.
        Assert.True(enrollment.CompleteLesson(course, lessons[2].Id, Now.AddMinutes(10)));
        Assert.True(enrollment.Modules.Single(m => m.CourseModuleId == sop.Id).IsComplete);
        Assert.Equal(14, enrollment.ProgressPercent); // 1 of 7 modules
    }

    [Fact]
    public void Completing_a_whole_module_counts_its_lessons_as_read()
    {
        var course = PublishedCourse();
        var enrollment = Enrollment.Assign(course, Trainee, null, Rules, Now);
        var sop = Sop(course);

        enrollment.CompleteModule(sop.Id, Now);

        Assert.All(sop.Lessons, lesson => Assert.True(enrollment.HasRead(sop.Id, lesson.Id)));
    }

    [Fact]
    public void Lesson_that_is_not_part_of_the_course_is_not_found_and_a_finished_enrolment_takes_no_lesson()
    {
        var course = PublishedCourse();
        var enrollment = Enrollment.Assign(course, Trainee, null, Rules, Now);

        Assert.Equal(ErrorKind.NotFound, Assert.Throws<DomainException>(() => enrollment.CompleteLesson(course, 424242, Now)).Kind);

        enrollment.Close();
        var refusal = Assert.Throws<DomainException>(() => enrollment.CompleteLesson(course, Sop(course).Lessons[0].Id, Now));
        Assert.Equal(Enrollment.StateRule, refusal.Rule);
        Assert.Empty(enrollment.Lessons);
    }

    [Fact]
    public void Failing_a_module_in_the_quiz_sends_the_learner_back_through_its_lessons()
    {
        var course = PublishedCourse();
        var enrollment = Enrollment.Assign(course, Trainee, null, Rules, Now);
        foreach (var lesson in course.Modules.SelectMany(m => m.Lessons)) enrollment.CompleteLesson(course, lesson.Id, Now);
        enrollment.EvaluateEligibility(AttendanceSummary.NoSessions, Rules);
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);
        var sop = Sop(course);
        var technique = course.Modules.Single(m => m.ModuleType == ModuleType.Technique);

        // Wrong in SOP and in the technique: 5 of 7, failed.
        var wrong = new[] { sop.Id, technique.Id };
        enrollment.AttemptQuiz(course, course.Quiz.Questions.ToDictionary(q => q.Id, q => (string?)(wrong.Contains(q.CourseModuleId) ? "B" : "A")),
            AttendanceSummary.NoSessions, Rules, Now);

        Assert.All(sop.Lessons, lesson => Assert.False(enrollment.HasRead(sop.Id, lesson.Id)));
        Assert.All(technique.Lessons, lesson => Assert.False(enrollment.HasRead(technique.Id, lesson.Id)));
        var overview = course.Modules.Single(m => m.ModuleType == ModuleType.ProductOverview);
        Assert.All(overview.Lessons, lesson => Assert.True(enrollment.HasRead(overview.Id, lesson.Id)));
    }
}
