using BrewForge.Domain.Common;

namespace BrewForge.Domain.Training;

public enum ScheduleStatus
{
    Scheduled,
    Completed,
    Cancelled,
}

/// <summary>
/// The appointment of a practical assessment: when, where, and which trainer.
/// An enrolment has at most one open appointment; a finished or cancelled one
/// is kept.
/// </summary>
public sealed class PracticalSchedule
{
    public const string Rule = "BR-39";

    private PracticalSchedule() { }

    public long Id { get; private set; }
    public long EnrollmentId { get; private set; }
    public long TrainerId { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public string Location { get; private set; } = null!;
    public string? Note { get; private set; }
    public ScheduleStatus Status { get; private set; } = ScheduleStatus.Scheduled;
    public DateTimeOffset CreatedAt { get; private set; }

    public static PracticalSchedule Create(Enrollment enrollment, long trainerId, DateTimeOffset? at, string? location,
        string? note, DateTimeOffset now)
    {
        if (enrollment.State != EnrollmentState.Passed)
        {
            throw DomainException.RuleViolation(Rule,
                $"A practical is scheduled after the quiz is passed; this enrolment is {enrollment.State.Code()}.");
        }

        var schedule = new PracticalSchedule { EnrollmentId = enrollment.Id, TrainerId = trainerId, CreatedAt = now };
        schedule.Set(at, location, note, now);
        return schedule;
    }

    public void Reschedule(DateTimeOffset? at, string? location, string? note, DateTimeOffset now)
    {
        EnsureOpen("rescheduled");
        Set(at, location, note, now);
    }

    public void Cancel()
    {
        EnsureOpen("cancelled");
        Status = ScheduleStatus.Cancelled;
    }

    public void Complete()
    {
        EnsureOpen("completed");
        Status = ScheduleStatus.Completed;
    }

    private void Set(DateTimeOffset? at, string? location, string? note, DateTimeOffset now)
    {
        location = location?.Trim() ?? "";
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        new FieldErrors()
            .Check(at is not null, "scheduledAt", "is required")
            .Check(at is null || at > now, "scheduledAt", "must be in the future")
            .RequiredMax("location", location, 160)
            .MaxLength("note", note, 500)
            .ThrowIfAny();

        ScheduledAt = at!.Value;
        Location = location;
        Note = note;
    }

    private void EnsureOpen(string action)
    {
        if (Status != ScheduleStatus.Scheduled)
        {
            throw DomainException.RuleViolation(Rule,
                $"An appointment that is {EnumCode<ScheduleStatus>.ToCode(Status)} cannot be {action}.");
        }
    }
}
