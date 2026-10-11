using BrewForge.Domain.Common;

namespace BrewForge.Domain.Quality;

public enum CorrectiveActionStatus
{
    Open,
    InProgress,
    Closed,
}

/// <summary>
/// What a failed audit obliges a branch to do. OPEN, then IN_PROGRESS, then
/// CLOSED; a closed action is final.
/// </summary>
public sealed class CorrectiveAction : INeverDeleted
{
    private CorrectiveAction() { }

    public long Id { get; private set; }
    public long BranchAuditId { get; private set; }
    public long BranchId { get; private set; }
    public string Description { get; private set; } = null!;
    public bool RetrainingRequired { get; private set; }

    /// <summary>The staff to retrain; empty when no retraining is required.</summary>
    public long[] EmployeeIds { get; private set; } = [];
    public CorrectiveActionStatus Status { get; private set; } = CorrectiveActionStatus.Open;
    public DateOnly? DueDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public long? ClosedBy { get; private set; }
    public string? ClosureNote { get; private set; }

    string INeverDeleted.RetentionRule => "BR-37";

    public static CorrectiveAction Raise(BranchAudit audit, DateTimeOffset now, bool retrainingRequired,
        IEnumerable<long> employeeIds, DateOnly? dueDate)
    {
        var description = "Deviations: " + string.Join("; ", audit.Deviations.Select(Describe));
        if (description.Length > 1000) description = description[..997] + "...";

        return new CorrectiveAction
        {
            BranchAuditId = audit.Id, BranchId = audit.BranchId, Description = description,
            RetrainingRequired = retrainingRequired,
            EmployeeIds = retrainingRequired ? [.. employeeIds.Distinct()] : [],
            DueDate = dueDate, CreatedAt = now,
        };
    }

    public void Start()
    {
        if (Status != CorrectiveActionStatus.Open) throw NotAllowed("start");
        Status = CorrectiveActionStatus.InProgress;
    }

    public void Close(long userId, DateTimeOffset now, string? note)
    {
        if (Status == CorrectiveActionStatus.Closed) throw NotAllowed("close");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        new FieldErrors().MaxLength("closureNote", note, 500).ThrowIfAny();

        Status = CorrectiveActionStatus.Closed;
        ClosedAt = now;
        ClosedBy = userId;
        ClosureNote = note;
    }

    private static string Describe(BranchAuditItem item) =>
        item.Expected is null && item.Actual is null
            ? item.Criterion
            : $"{item.Criterion} (expected {item.Expected ?? "-"}, actual {item.Actual ?? "-"})";

    private DomainException NotAllowed(string action) =>
        DomainException.RuleViolation("BR-37", $"A corrective action that is {EnumCode<CorrectiveActionStatus>.ToCode(Status)} cannot be {action}ed.");
}
