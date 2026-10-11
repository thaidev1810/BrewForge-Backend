using System.Text.RegularExpressions;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;

namespace BrewForge.Application.Quality;

public sealed record EvidenceDto(string Key, string Url, long SizeBytes);

public sealed record EvidenceContent(Stream Content, string ContentType);

/// <summary>
/// Photos an auditor attaches to a checklist item. Stored beside the practical recordings, under a key the
/// storage chooses; the item keeps the returned URL.
/// </summary>
public sealed partial class EvidenceService(IPracticalVideoStorage storage)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
    };

    public async Task<EvidenceDto> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!Types.ContainsKey(extension))
        {
            throw DomainException.Validation("The evidence must be a JPG, PNG or WEBP image.",
                new ErrorDetail("file", "unsupported file type"));
        }

        var stored = await storage.SaveAsync(content, extension, MaxBytes, cancellationToken)
            ?? throw DomainException.Validation("The image is larger than 5 MB.", new ErrorDetail("file", "too large"));
        return new EvidenceDto(stored.Key, $"/api/v1/branch-audits/evidence/{stored.Key}", stored.SizeBytes);
    }

    public EvidenceContent Open(string key)
    {
        // Only keys the storage itself produced: yyyy/MM/<guid>.<image extension>.
        var extension = Path.GetExtension(key ?? "").ToLowerInvariant();
        if (!KeyShape().IsMatch(key ?? "") || !Types.TryGetValue(extension, out var contentType)
            || storage.OpenRead(key!) is not { } stream)
        {
            throw DomainException.NotFound("Evidence", key ?? "");
        }
        return new EvidenceContent(stream, contentType);
    }

    [GeneratedRegex(@"^\d{4}/\d{2}/[0-9a-f]{32}\.(jpg|jpeg|png|webp)$")]
    private static partial Regex KeyShape();
}
