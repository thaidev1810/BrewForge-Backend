using BrewForge.Domain.Common;

namespace BrewForge.Domain.Quality;

public enum AuditResult
{
    Passed,
    Failed,
}

/// <summary>One line of the audit checklist: what the SOP expects, what the auditor saw.</summary>
public sealed class BranchAuditItem
{
    private BranchAuditItem() { }

    public long Id { get; private set; }
    public long BranchAuditId { get; private set; }
    public string Criterion { get; private set; } = null!;
    public string? Expected { get; private set; }
    public string? Actual { get; private set; }
    public bool Passed { get; private set; }

    /// <summary>A failed critical item fails the audit whatever the score.</summary>
    public bool Critical { get; private set; }
    public string? Comment { get; private set; }
    public string? EvidenceUrl { get; private set; }

    internal static BranchAuditItem Create(string criterion, string? expected, string? actual, bool passed,
        bool critical, string? comment, string? evidenceUrl)
    {
        criterion = criterion?.Trim() ?? "";
        expected = Blank(expected);
        actual = Blank(actual);
        comment = Blank(comment);
        evidenceUrl = Blank(evidenceUrl);
        new FieldErrors()
            .RequiredMax("criterion", criterion, 160)
            .MaxLength("expected", expected, 255)
            .MaxLength("actual", actual, 255)
            .MaxLength("comment", comment, 500)
            .MaxLength("evidenceUrl", evidenceUrl, 500)
            .ThrowIfAny();

        return new BranchAuditItem
        {
            Criterion = criterion, Expected = expected, Actual = actual, Passed = passed, Critical = critical,
            Comment = comment, EvidenceUrl = evidenceUrl,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A quality auditor's visit to one branch against one released SOP version.
/// The score and the verdict are computed here from the checklist, never sent
/// by the client, and the audit cannot be changed afterwards.
/// </summary>
public sealed class BranchAudit : INeverDeleted
{
    /// <summary>The share of passed items below which an audit fails, unless the administrator set another (system setting).</summary>
    public const int DefaultPassMark = 90;

    private readonly List<BranchAuditItem> _items = [];

    private BranchAudit() { }

    public long Id { get; private set; }
    public long BranchId { get; private set; }
    public long RecipeVersionId { get; private set; }
    public long AuditorId { get; private set; }
    public DateTimeOffset AuditedAt { get; private set; }
    public int Score { get; private set; }
    public AuditResult Result { get; private set; }
    public string? Comment { get; private set; }
    public IReadOnlyList<BranchAuditItem> Items => _items;

    string INeverDeleted.RetentionRule => "BR-37";

    public static BranchAudit Record(long branchId, long recipeVersionId, long auditorId, DateTimeOffset now,
        string? comment, IEnumerable<AuditItemInput> items, int passMark = DefaultPassMark)
    {
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        new FieldErrors().MaxLength("comment", comment, 1000).ThrowIfAny();

        var audit = new BranchAudit
        {
            BranchId = branchId, RecipeVersionId = recipeVersionId, AuditorId = auditorId, AuditedAt = now,
            Comment = comment,
        };
        foreach (var item in items ?? [])
        {
            audit._items.Add(BranchAuditItem.Create(item.Criterion!, item.Expected, item.Actual, item.Passed,
                item.Critical, item.Comment, item.EvidenceUrl));
        }
        if (audit._items.Count == 0)
        {
            throw DomainException.Validation("An audit needs at least one checklist item.",
                new ErrorDetail("items", "is required"));
        }

        audit.Score = (int)Math.Round(100.0 * audit._items.Count(i => i.Passed) / audit._items.Count,
            MidpointRounding.AwayFromZero);
        audit.Result = audit.Score >= passMark && audit._items.All(i => i.Passed || !i.Critical)
            ? AuditResult.Passed
            : AuditResult.Failed;
        return audit;
    }

    public IReadOnlyList<BranchAuditItem> Deviations => [.. _items.Where(i => !i.Passed)];
}

public sealed record AuditItemInput(string? Criterion, string? Expected, string? Actual, bool Passed, bool Critical,
    string? Comment, string? EvidenceUrl);
