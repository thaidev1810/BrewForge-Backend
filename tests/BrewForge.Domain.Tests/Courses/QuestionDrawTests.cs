using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Courses;

/// <summary>
/// A quiz as a bank of questions: an attempt is given so many from each
/// module, and other ones the next time.
/// </summary>
public sealed class QuestionDrawTests
{
    private const long Trainer = 6;
    private const int PerModule = 6;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly QuizOption[] Options = [new("A", "Right"), new("B", "Wrong")];

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    /// <summary>
    /// A course whose quiz has six questions on each of two modules (SOP:
    /// ids 1-6, TECHNIQUE: ids 7-12) and one on a third (COMMON_MISTAKES:
    /// id 13), every correct answer being "A".
    /// </summary>
    private static Course CourseWithBank(int? drawPerModule, bool published = true)
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", ReleasedVersion(), Trainer);
        WithId(course, 20);
        foreach (var module in course.Modules) WithId(module, 200 + module.ModuleOrder);
        foreach (var module in course.Modules.Where(m => m.Source != ModuleSource.Generated))
        {
            foreach (var lesson in module.Lessons.ToList()) course.UpdateLesson(module, lesson, null, "Prose", null);
            if (module.Lessons.Count == 0) course.AddLesson(module, "Lesson", "Content", null);
            course.SetModuleDuration(module, 10);
        }

        long id = 1;
        foreach (var type in new[] { ModuleType.Sop, ModuleType.Technique })
        {
            var moduleId = course.Modules.Single(m => m.ModuleType == type).Id;
            for (var i = 0; i < PerModule; i++) WithId(course.AddQuizQuestion(moduleId, $"Question {id}?", Options, "A"), id++);
        }
        WithId(course.AddQuizQuestion(course.Modules.Single(m => m.ModuleType == ModuleType.CommonMistakes).Id, "Question 13?", Options, "A"), id);
        WithId(course.Quiz, 2000);
        course.UpdateQuiz(null, null, drawPerModule);
        if (published)
        {
            course.Submit();
            course.Approve(8, Now);
        }
        return course;
    }

    private static long[] Ids(IEnumerable<QuizQuestion> questions) => [.. questions.Select(question => question.Id)];

    // ---------------------------------------------------------------- the draw

    [Fact]
    public void Quiz_without_a_draw_asks_every_question_on_every_attempt()
    {
        var quiz = CourseWithBank(drawPerModule: null).Quiz;

        Assert.Equal(Enumerable.Range(1, 13).Select(i => (long)i), Ids(quiz.DrawFor(501, 1)));
        Assert.Equal(Ids(quiz.DrawFor(501, 1)), Ids(quiz.DrawFor(777, 4)));
        Assert.Equal((13, 13), (quiz.QuestionCount, quiz.QuestionsPerAttempt()));
    }

    [Fact]
    public void Draw_takes_so_many_questions_from_each_module_and_all_of_a_module_that_has_fewer()
    {
        var course = CourseWithBank(drawPerModule: 2);

        var drawn = course.Quiz.DrawFor(501, 1);

        // BR-35 still means something: every module the quiz tests is tested in every attempt.
        Assert.Equal([(ModuleType.Sop, 2), (ModuleType.Technique, 2), (ModuleType.CommonMistakes, 1)],
            drawn.GroupBy(q => course.Module(q.CourseModuleId).ModuleType).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())));
        Assert.Equal((13, 5), (course.Quiz.QuestionCount, course.Quiz.QuestionsPerAttempt()));
        Assert.Equal(drawn.Select(q => q.Id).Order(), drawn.Select(q => q.Id));
    }

    [Fact]
    public void Draw_of_an_attempt_is_the_same_whenever_it_is_asked_for()
    {
        var quiz = CourseWithBank(drawPerModule: 3).Quiz;

        // What a trainee is shown is what their answers are marked against.
        Assert.Equal(Ids(quiz.DrawFor(501, 2)), Ids(quiz.DrawFor(501, 2)));
    }

    [Fact]
    public void Another_attempt_and_another_trainee_are_given_another_draw()
    {
        var quiz = CourseWithBank(drawPerModule: 3).Quiz;

        var attempts = Enumerable.Range(1, 8).Select(attempt => string.Join(',', Ids(quiz.DrawFor(501, attempt)))).Distinct().Count();
        var trainees = Enumerable.Range(501, 8).Select(enrollment => string.Join(',', Ids(quiz.DrawFor(enrollment, 1)))).Distinct().Count();

        // Twenty ways to take three of six, squared for the two modules: eight draws do not all coincide.
        Assert.True(attempts > 1, "every attempt was given the same questions");
        Assert.True(trainees > 1, "every trainee was given the same questions");
    }

    [Fact]
    public void Every_question_of_a_module_is_drawn_by_somebody()
    {
        var quiz = CourseWithBank(drawPerModule: 1).Quiz;

        var seen = Enumerable.Range(1, 200).SelectMany(enrollment => Ids(quiz.DrawFor(enrollment, 1))).Distinct().Order();

        Assert.Equal(Enumerable.Range(1, 13).Select(i => (long)i), seen);
    }

    [Fact]
    public void Draw_is_set_with_the_quiz_while_the_course_is_a_draft()
    {
        var course = CourseWithBank(drawPerModule: null, published: false);

        course.UpdateQuiz(null, null, drawPerModule: 4);
        Assert.Equal(4, course.Quiz.DrawPerModule);
        course.UpdateQuiz("Oolong quiz", 70);
        Assert.Equal(4, course.Quiz.DrawPerModule); // left as it is when not named
        course.UpdateQuiz(null, null, drawPerModule: 0);
        Assert.Null(course.Quiz.DrawPerModule); // 0 asks every question

        Assert.All(new[] { -1, Quiz.MaxDrawPerModule + 1 }, bad =>
            Assert.Contains(Refused(() => course.UpdateQuiz(null, null, bad)).Details, d => d.Field == "drawPerModule"));

        course.Submit();
        Assert.Equal(Course.StateRule, Refused(() => course.UpdateQuiz(null, null, 2)).Rule);
    }
}
