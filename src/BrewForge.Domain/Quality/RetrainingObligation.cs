using BrewForge.Domain.Common;

namespace BrewForge.Domain.Quality;

public enum ObligationStatus
{
    /// <summary>Named on the action, not yet given a retraining cycle.</summary>
    Pending,

    /// <summary>Linked to the enrolment cycle the employee retrains on.</summary>
    Assigned,

    /// <summary>Released from the obligation by an auditor, with a reason.</summary>
    Exempted,
}

/// <summary>
/// G-19: one employee's duty to retrain under one corrective action, kept as its own record so that closing
/// the action can check the very cycle the employee was given - not "any pass of theirs on the course". It
/// stores the target (course and SOP version) and the enrolment cycle linked to it; only a pass of that cycle
/// earned after it was linked meets the duty. An employee who left or moved is not dropped silently: their
/// duty stays open until an auditor exempts them with a reason.
/// </summary>
public sealed class RetrainingObligation
{
    public const int MaxReasonLength = 500;

    private RetrainingObligation() { }

    /// <param name="legacy">
    /// Made for an action raised before obligations were recorded: nothing proves what was retrained, so it
    /// waits for reconciliation like any open duty.
    /// </param>
    public RetrainingObligation(long correctiveActionId, long employeeId, bool legacy, DateTimeOffset createdAt)
    {
        CorrectiveActionId = correctiveActionId;
        EmployeeId = employeeId;
        Legacy = legacy;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }
    public long CorrectiveActionId { get; private set; }
    public long EmployeeId { get; private set; }
    public ObligationStatus Status { get; private set; } = ObligationStatus.Pending;

    /// <summary>The course the employee retrains on, and the SOP version it is bound to, as assigned.</summary>
    public long? CourseId { get; private set; }
    public long? RecipeVersionId { get; private set; }

    /// <summary>The enrolment cycle that meets this duty once passed.</summary>
    public long? EnrollmentId { get; private set; }

    /// <summary>When the cycle was linked: only a pass completed at or after this counts.</summary>
    public DateTimeOffset? AssignedAt { get; private set; }

    public string? ExemptReason { get; private set; }
    public long? ExemptedBy { get; private set; }
    public DateTimeOffset? ExemptedAt { get; private set; }
    public bool Legacy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Links the cycle the employee retrains on. Relinking (another course, a new cycle) restarts the clock.</summary>
    public void LinkCycle(long courseId, long? recipeVersionId, long enrollmentId, DateTimeOffset now)
    {
        if (Status == ObligationStatus.Exempted)
        {
            throw DomainException.RuleViolation("OBLIGATION_EXEMPTED",
                $"Employee {EmployeeId} was exempted from this retraining; it cannot be assigned again.");
        }
        Status = ObligationStatus.Assigned;
        CourseId = courseId;
        RecipeVersionId = recipeVersionId;
        EnrollmentId = enrollmentId;
        AssignedAt = now;
    }

    /// <summary>Releases the employee from the duty. A reason is required; the exemption is final.</summary>
    public void Exempt(long exemptedBy, string? reason, DateTimeOffset now)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        new FieldErrors().RequiredMax("reason", reason, MaxReasonLength).ThrowIfAny();
        if (Status == ObligationStatus.Exempted)
        {
            throw DomainException.RuleViolation("OBLIGATION_EXEMPTED", $"Employee {EmployeeId} is already exempted.");
        }
        Status = ObligationStatus.Exempted;
        ExemptReason = reason;
        ExemptedBy = exemptedBy;
        ExemptedAt = now;
    }

    /// <summary>
    /// Whether the duty is met, given the state and completion of the linked cycle: exempted, or that cycle
    /// passed at or after it was linked. A pass from before the link - the training that did not hold - is not.
    /// </summary>
    public bool IsMetBy(bool cyclePassed, DateTimeOffset? cycleCompletedAt) =>
        Status == ObligationStatus.Exempted
        || (Status == ObligationStatus.Assigned && cyclePassed && cycleCompletedAt is { } done && done >= AssignedAt);
}
