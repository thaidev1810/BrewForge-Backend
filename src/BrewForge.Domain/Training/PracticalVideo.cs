using BrewForge.Domain.Common;

namespace BrewForge.Domain.Training;

/// <summary>
/// The recording of one practical: what the trainer who ran it filmed and
/// uploaded. It is the evidence an evaluation is judged from, and nothing
/// more; no machine looks at it (BR-17). The file itself lives in the video
/// storage under <see cref="StorageKey"/>.
/// </summary>
public sealed class PracticalVideo
{
    public const string Rule = "PRACTICAL_VIDEO";

    /// <summary>The largest recording that is accepted.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
    };

    private PracticalVideo() { }

    internal PracticalVideo(long uploadedBy, string fileName, string contentType, long sizeBytes, string sha256,
        string storageKey, DateTimeOffset uploadedAt)
    {
        UploadedBy = uploadedBy;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        UploadedAt = uploadedAt;
    }

    public long Id { get; private set; }
    public long EnrollmentId { get; private set; }

    /// <summary>The trainer who ran the practical. Never the trainee (BR-14).</summary>
    public long UploadedBy { get; private set; }
    public string FileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = null!;
    public string StorageKey { get; private set; } = null!;
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>
    /// The extension and the content type a recording is stored under. Only
    /// the name is trusted for this, never the content type the client sent.
    /// </summary>
    public static (string Extension, string ContentType) Format(string? fileName)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? "");
        var extension = Path.GetExtension(name);
        new FieldErrors()
            .Check(name.Length is > 0 and <= 255, "file", "needs a file name of at most 255 characters")
            .Check(ContentTypes.ContainsKey(extension), "file", "must be a video of type .mp4, .mov or .webm")
            .ThrowIfAny();
        return (extension.ToLowerInvariant(), ContentTypes[extension]);
    }

    public static void EnsureSize(long sizeBytes) =>
        new FieldErrors()
            .Check(sizeBytes > 0, "file", "is empty")
            .Check(sizeBytes <= MaxBytes, "file", $"is larger than {MaxBytes / (1024 * 1024)} MB")
            .ThrowIfAny();
}
