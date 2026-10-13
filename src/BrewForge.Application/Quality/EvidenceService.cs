using System.Text.RegularExpressions;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Quality;

public sealed record EvidenceDto(string Key, string Url, long SizeBytes);

public sealed record EvidenceContent(Stream Content, string ContentType);

/// <summary>
/// Photos an auditor attaches to a checklist item. Stored beside the practical recordings, under a key the
/// storage chooses; the item keeps the returned URL.
/// </summary>
public sealed partial class EvidenceService(IPracticalVideoStorage storage, IBrewForgeDbContext db,
    ICurrentUser currentUser)
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

    public async Task<EvidenceContent> OpenAsync(string key, CancellationToken cancellationToken)
    {
        // Only keys the storage itself produced: yyyy/MM/<guid>.<image extension>.
        var extension = Path.GetExtension(key ?? "").ToLowerInvariant();
        if (!KeyShape().IsMatch(key ?? "") || !Types.TryGetValue(extension, out var contentType))
        {
            throw DomainException.NotFound("Evidence", key ?? "");
        }

        // A branch-scoped caller (a branch manager) may read only evidence attached to an audit of
        // their own branch; the branch_audit query filter confines the lookup to that branch. Head
        // office (auditor, R&D manager, admin) may read any image, including one just uploaded and
        // not yet attached to an audit.
        if (currentUser.RestrictedToBranchId is not null)
        {
            var url = $"/api/v1/branch-audits/evidence/{key}";
            if (!await db.BranchAudits.AnyAsync(a => a.Items.Any(i => i.EvidenceUrl == url), cancellationToken))
            {
                throw DomainException.NotFound("Evidence", key!);
            }
        }

        if (storage.OpenRead(key!) is not { } stream)
        {
            throw DomainException.NotFound("Evidence", key!);
        }
        return new EvidenceContent(stream, contentType);
    }

    [GeneratedRegex(@"^\d{4}/\d{2}/[0-9a-f]{32}\.(jpg|jpeg|png|webp)$")]
    private static partial Regex KeyShape();
}
