using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Courses;

/// <summary>
/// The pictures a trainer uploads to a lesson: what counts as a picture, and
/// where one may be added (a lesson the trainer writes, in a draft).
/// </summary>
public sealed class LessonMediaTests
{
    private const long Trainer = 6;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] Gif = "GIF89a\u0001\u0000\u0001\u0000\u0000\u0000"u8.ToArray();
    private static readonly byte[] WebP = [.. "RIFF"u8, 36, 0, 0, 0, .. "WEBP"u8];

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    /// <summary>A draft course with one lesson written by the trainer in COMMON_MISTAKES.</summary>
    private static (Course Course, CourseModule Module, Lesson Lesson) DraftWithLesson()
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", ReleasedVersion(), Trainer);
        // Ids as the modules would have after being stored: a quiz question names its module by id.
        foreach (var each in course.Modules) WithId(each, 700 + each.ModuleOrder);
        var module = course.Modules.Single(m => m.ModuleType == ModuleType.CommonMistakes);
        return (course, module, course.AddLesson(module, "Pouring too fast", "Pour down the side of the cup.", null));
    }

    private static LessonMedia Add(Course course, CourseModule module, Lesson lesson, string name = "pour.png") =>
        course.AddLessonMedia(module, lesson, Trainer, name, "image/png", 2048, new string('a', 64), $"test/{Guid.NewGuid():N}.png", Now);

    // ---------------------------------------------------------------- what a picture is

    [Fact]
    public void Picture_is_what_its_first_bytes_say_and_its_name_agrees()
    {
        Assert.Equal((".png", "image/png"), LessonMedia.Format("pour.PNG", Png));
        Assert.Equal((".jpg", "image/jpeg"), LessonMedia.Format("pour.jpeg", Jpeg));
        Assert.Equal((".jpg", "image/jpeg"), LessonMedia.Format("pour.jpg", Jpeg));
        Assert.Equal((".gif", "image/gif"), LessonMedia.Format("pour.gif", Gif));
        Assert.Equal((".webp", "image/webp"), LessonMedia.Format("pour.webp", WebP));
    }

    [Fact]
    public void File_renamed_to_look_like_a_picture_is_refused_and_so_is_a_picture_named_as_another_kind()
    {
        var script = "#!/bin/sh\nrm -rf"u8.ToArray();

        var notAPicture = Refused(() => LessonMedia.Format("pour.png", script));
        var wrongName = Refused(() => LessonMedia.Format("pour.jpg", Png));
        var noName = Refused(() => LessonMedia.Format(" ", Png));
        var tooShort = Refused(() => LessonMedia.Format("pour.png", Png.AsSpan(0, 4)));

        Assert.All(new[] { notAPicture, wrongName, noName, tooShort }, refusal =>
        {
            Assert.Equal(ErrorKind.Validation, refusal.Kind);
            Assert.Contains(refusal.Details, d => d.Field == "file");
        });
        Assert.Contains(wrongName.Details, d => d.Issue.Contains("PNG picture"));
    }

    [Fact]
    public void Picture_is_neither_empty_nor_larger_than_the_limit()
    {
        LessonMedia.EnsureSize(1);
        LessonMedia.EnsureSize(LessonMedia.MaxBytes);

        Assert.Equal(ErrorKind.Validation, Refused(() => LessonMedia.EnsureSize(0)).Kind);
        Assert.Equal(ErrorKind.Validation, Refused(() => LessonMedia.EnsureSize(LessonMedia.MaxBytes + 1)).Kind);
    }

    // ---------------------------------------------------------------- where a picture may be

    [Fact]
    public void Pictures_of_a_lesson_are_kept_in_the_order_they_were_added()
    {
        var (course, module, lesson) = DraftWithLesson();

        var first = Add(course, module, lesson, "hold.png");
        var second = Add(course, module, lesson, "pour.png");

        Assert.Equal([(1, "hold.png"), (2, "pour.png")], lesson.OrderedMedia().Select(m => (m.SortOrder, m.FileName)));
        Assert.Equal((Trainer, Now, 2048L), (first.UploadedBy, first.UploadedAt, first.SizeBytes));

        // Taking the first away does not renumber the second, and the next one comes after it.
        course.RemoveLessonMedia(module, lesson, first);
        Assert.Equal(3, Add(course, module, lesson).SortOrder);
        Assert.Equal([second.FileName, "pour.png"], lesson.OrderedMedia().Select(m => m.FileName));
    }

    [Fact]
    public void Lesson_carries_a_limited_number_of_pictures()
    {
        var (course, module, lesson) = DraftWithLesson();
        for (var i = 0; i < LessonMedia.MaxPerLesson; i++) Add(course, module, lesson);

        var refusal = Refused(() => Add(course, module, lesson));

        Assert.Equal((ErrorKind.RuleViolation, LessonMedia.Rule), (refusal.Kind, refusal.Rule));
        Assert.Equal(LessonMedia.MaxPerLesson, lesson.Media.Count);
        Assert.Equal(LessonMedia.Rule, Refused(() => course.EnsureAcceptsLessonMedia(module, lesson)).Rule);
    }

    [Fact]
    public void BR_30_a_generated_module_is_the_recipes_and_takes_no_picture_from_the_trainer()
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", ReleasedVersion(), Trainer);
        var sop = course.Modules.Single(m => m.ModuleType == ModuleType.Sop);
        var step = sop.Lessons[0];

        Assert.Equal("BR-30", Refused(() => Add(course, sop, step)).Rule);
        Assert.Equal("BR-30", Refused(() => course.EnsureAcceptsLessonMedia(sop, step)).Rule);
        Assert.Empty(step.Media);
    }

    [Fact]
    public void Technique_gate_of_a_mixed_module_takes_pictures_like_its_prose()
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", ReleasedVersion(), Trainer);
        var technique = course.Modules.Single(m => m.ModuleType == ModuleType.Technique);

        Add(course, technique, technique.Lessons[0]);

        Assert.Single(technique.Lessons[0].Media);
    }

    [Fact]
    public void Picture_is_added_and_removed_only_while_the_course_is_a_draft()
    {
        var (course, module, lesson) = DraftWithLesson();
        var picture = Add(course, module, lesson);
        var technique = course.Modules.Single(m => m.ModuleType == ModuleType.Technique);
        foreach (var gate in technique.Lessons.ToList()) course.UpdateLesson(technique, gate, null, "How the gate is done.", null);
        course.SetModuleDuration(technique, 10);
        course.SetModuleDuration(module, 10);
        var exceptions = course.Modules.Single(m => m.ModuleType == ModuleType.ExceptionHandling);
        course.AddLesson(exceptions, "When it goes wrong", "Stop and tell the shift lead.", null);
        course.SetModuleDuration(exceptions, 10);
        course.AddQuizQuestion(module.Id, "How is the milk poured?", [new("A", "Down the side"), new("B", "Fast")], "A");
        course.Submit();

        Assert.Equal(Course.StateRule, Refused(() => Add(course, module, lesson)).Rule);
        Assert.Equal(Course.StateRule, Refused(() => course.RemoveLessonMedia(module, lesson, picture)).Rule);
        Assert.Single(lesson.Media);
    }

    [Fact]
    public void Picture_of_another_lesson_is_not_this_lessons_to_remove()
    {
        var (course, module, lesson) = DraftWithLesson();
        var other = course.AddLesson(module, "Another mistake", "Do not shake a hot drink.", null);
        var picture = Add(course, module, lesson);

        Assert.Throws<ArgumentException>(() => course.RemoveLessonMedia(module, other, picture));
    }
}
