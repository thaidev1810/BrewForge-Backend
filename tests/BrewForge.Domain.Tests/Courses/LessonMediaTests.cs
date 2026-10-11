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

}
