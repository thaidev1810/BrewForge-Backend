using BrewForge.Domain.Common;

namespace BrewForge.Domain.Courses;

/// <summary>
/// A picture a trainer uploaded to a lesson: how the cup is held, what the
/// foam should look like. Owned by its <see cref="Lesson"/>; the file itself
/// lives in the media storage under <see cref="StorageKey"/>. A video is not
/// uploaded here: a lesson embeds one by its link.
/// </summary>
public sealed class LessonMedia
{
    public const string Rule = "LESSON_MEDIA";

    /// <summary>The largest picture that is accepted.</summary>
    public const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>How many pictures one lesson may carry.</summary>
    public const int MaxPerLesson = 8;

    /// <summary>How many bytes from the start of a file are enough to tell what kind of picture it is.</summary>
    public const int SignatureBytes = 12;

    private LessonMedia() { }

    internal LessonMedia(int sortOrder, long uploadedBy, string fileName, string contentType, long sizeBytes,
        string sha256, string storageKey, DateTimeOffset uploadedAt)
    {
        SortOrder = sortOrder;
        UploadedBy = uploadedBy;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        UploadedAt = uploadedAt;
    }

    public long Id { get; private set; }
    public long LessonId { get; private set; }

    /// <summary>The place of the picture among those of its lesson, from 1.</summary>
    public int SortOrder { get; private set; }
    public string FileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = null!;
    public string StorageKey { get; private set; } = null!;
    public long UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>
    /// What kind of picture a file is, and the extension it is stored under.
    /// The first bytes decide: a file is the picture its content says it is,
    /// and its name has to agree. A file renamed to .png is not a picture.
    /// </summary>
    public static (string Extension, string ContentType) Format(string? fileName, ReadOnlySpan<byte> start)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? "");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var kind = KindOf(start);
        new FieldErrors()
            .Check(name.Length is > 0 and <= 255, "file", "needs a file name of at most 255 characters")
            .Check(kind is not null, "file", "must be a PNG, JPEG, WebP or GIF picture")
            .Check(kind is null || kind.Value.Extensions.Contains(extension), "file",
                kind is null ? "is not a picture" : $"is a {kind.Value.Name} picture, and its name does not end in {string.Join(" or ", kind.Value.Extensions)}")
            .ThrowIfAny();
        return (kind!.Value.Extensions[0], kind.Value.ContentType);
    }

    public static void EnsureSize(long sizeBytes) =>
        new FieldErrors()
            .Check(sizeBytes > 0, "file", "is empty")
            .Check(sizeBytes <= MaxBytes, "file", $"is larger than {MaxBytes / (1024 * 1024)} MB")
            .ThrowIfAny();

    private static (string Name, string ContentType, string[] Extensions)? KindOf(ReadOnlySpan<byte> start)
    {
        if (start.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ("PNG", "image/png", [".png"]);
        if (start.StartsWith<byte>([0xFF, 0xD8, 0xFF])) return ("JPEG", "image/jpeg", [".jpg", ".jpeg"]);
        if (start.StartsWith("GIF87a"u8) || start.StartsWith("GIF89a"u8)) return ("GIF", "image/gif", [".gif"]);
        if (start.Length >= 12 && start.StartsWith("RIFF"u8) && start.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ("WebP", "image/webp", [".webp"]);
        }
        return null;
    }
}
