using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;

namespace BrewForge.Domain.Training;

/// <summary>The <c>enrollment_state</c> enumeration.</summary>
public enum EnrollmentState
{
    Assigned,
    InProgress,
    Eligible,
    Passed,
    Locked,
    Closed,
}

/// <summary>The <c>attendance_status</c> enumeration.</summary>
public enum AttendanceStatus
{
    Present,
    Absent,
    Excused,
}

/// <summary>
/// A trainee's attendance over the sessions of a class. An excused session
/// does not count against the trainee; a session not yet recorded counts as
/// not attended until it is.
/// </summary>
public readonly record struct AttendanceSummary(int TotalSessions, int Present, int Absent, int Excused)
{
    /// <summary>An enrolment outside a class has no sessions to attend.</summary>
    public static readonly AttendanceSummary NoSessions = new(0, 0, 0, 0);

    private int Counted => TotalSessions - Excused;

    private int NotYetRecorded => TotalSessions - Present - Absent - Excused;

    public int Percent => Counted <= 0 ? 100 : Present * 100 / Counted;

    /// <summary>Exact, in integers: 4 of 5 sessions is 80 percent, not 79.99.</summary>
    public bool Meets(int minimumPercent) => Counted <= 0 || Present * 100 >= minimumPercent * Counted;

    /// <summary>
    /// Whether the minimum is still within reach if the trainee attends every
    /// session that has not been recorded yet. False as soon as it has become
    /// arithmetically impossible.
    /// </summary>
    public bool CanStillMeet(int minimumPercent) =>
        Counted <= 0 || (Present + NotYetRecorded) * 100 >= minimumPercent * Counted;

    public static AttendanceSummary From(int totalSessions, IEnumerable<AttendanceStatus> recorded)
    {
        var statuses = recorded.ToList();
        return new AttendanceSummary(totalSessions,
            statuses.Count(status => status == AttendanceStatus.Present),
            statuses.Count(status => status == AttendanceStatus.Absent),
            statuses.Count(status => status == AttendanceStatus.Excused));
    }
}

/// <summary>The outcome of the eligibility check (BR-32).</summary>
public sealed record EligibilityResult(bool Eligible, IReadOnlyList<long> MissingModuleIds, int AttendancePercent,
    int MinAttendancePercent, bool AttendanceMet, bool AttendanceStillReachable);

/// <summary>A trainee's completion of one module.</summary>
public sealed class ModuleProgress
{
    private ModuleProgress() { }

    internal ModuleProgress(long courseModuleId)
    {
        CourseModuleId = courseModuleId;
    }

    public long Id { get; private set; }
    public long EnrollmentId { get; private set; }
    public long CourseModuleId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsComplete => CompletedAt is not null;

    internal void Complete(DateTimeOffset now) => CompletedAt ??= now;

    internal void Reset() => CompletedAt = null;
}

/// <summary>Whether one enrolled trainee attended one session (UC-30).</summary>
public sealed class Attendance
{
    private Attendance() { }

    public Attendance(long sessionId, long enrollmentId, AttendanceStatus status, string? note, long recordedBy,
        DateTimeOffset recordedAt)
    {
        SessionId = sessionId;
        EnrollmentId = enrollmentId;
        Set(status, note, recordedBy, recordedAt);
    }

    public long Id { get; private set; }
    public long SessionId { get; private set; }
    public long EnrollmentId { get; private set; }
    public AttendanceStatus Status { get; private set; }
    public string? Note { get; private set; }
    public long RecordedBy { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>Changes the entry. Returns false when nothing actually changed.</summary>
    public bool Correct(AttendanceStatus status, string? note, long recordedBy, DateTimeOffset recordedAt)
    {
        note = Clean(note);
        if (status == Status && note == Note) return false;
        Set(status, note, recordedBy, recordedAt);
        return true;
    }

    private void Set(AttendanceStatus status, string? note, long recordedBy, DateTimeOffset recordedAt)
    {
        note = Clean(note);
        new FieldErrors()
            .Check(Enum.IsDefined(status), "status", "is not a valid attendance status")
            .MaxLength("note", note, 255)
            .ThrowIfAny();
        Status = status;
        Note = note;
        RecordedBy = recordedBy;
        RecordedAt = recordedAt;
    }

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}

/// <summary>
/// A trainee's registration on a course, with the deadline from the training
/// regulation. The lifecycle of the data dictionary is enforced here: a
/// transition that is not in the table does not exist.
/// </summary>
public sealed class Enrollment
{
    public const string StateRule = "STATE_TRANSITION";

    private static readonly HashSet<(EnrollmentState From, EnrollmentState To)> Transitions =
    [
        (EnrollmentState.Assigned, EnrollmentState.InProgress),
        (EnrollmentState.InProgress, EnrollmentState.Eligible),
        (EnrollmentState.Eligible, EnrollmentState.Passed),
        (EnrollmentState.Eligible, EnrollmentState.Locked),
        (EnrollmentState.Locked, EnrollmentState.Assigned),
        (EnrollmentState.Assigned, EnrollmentState.Closed),
        (EnrollmentState.InProgress, EnrollmentState.Closed),
        (EnrollmentState.Eligible, EnrollmentState.Closed),
    ];

    private readonly List<ModuleProgress> _modules = [];

    private Enrollment() { }

    public long Id { get; private set; }
    public long CourseId { get; private set; }
    public long? TrainingClassId { get; private set; }
    public long UserId { get; private set; }
    public AppUser User { get; private set; } = null!;
    public DateOnly? DueDate { get; private set; }
    public int ProgressPercent { get; private set; }
    public EnrollmentState State { get; private set; } = EnrollmentState.Assigned;
    public DateTimeOffset EnrolledAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<ModuleProgress> Modules => _modules;

    /// <summary>Whether the state model has a transition from one state to the other.</summary>
    public static bool IsAllowed(EnrollmentState from, EnrollmentState to) => Transitions.Contains((from, to));

    /// <summary>The day the enrolment was created: the day that decides which regulation applies to it.</summary>
    public DateOnly EnrolledOn => DateOnly.FromDateTime(EnrolledAt.UtcDateTime);

    /// <summary>
    /// Assigns a published course to a user. The deadline comes from the
    /// regulation, and one progress row is created per module so that "all
    /// seven complete" can be checked row by row (BR-32).
    /// </summary>
    public static Enrollment Assign(Course course, long userId, long? trainingClassId, TrainingRules rules,
        DateTimeOffset now)
    {
        if (course.State != CourseState.Published)
        {
            throw DomainException.RuleViolation("COURSE_NOT_PUBLISHED",
                $"Only a published course can be assigned; this one is {course.State.Code()}.");
        }

        var enrollment = new Enrollment
        {
            CourseId = course.Id,
            UserId = userId,
            TrainingClassId = trainingClassId,
            EnrolledAt = now,
            DueDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(rules.DueDays),
        };
        enrollment._modules.AddRange(course.OrderedModules().Select(module => new ModuleProgress(module.Id)));
        return enrollment;
    }

    /// <summary>UC-14. The first completed module moves ASSIGNED to IN_PROGRESS. Safe to repeat.</summary>
    public void CompleteModule(long courseModuleId, DateTimeOffset now)
    {
        if (State is not (EnrollmentState.Assigned or EnrollmentState.InProgress or EnrollmentState.Eligible))
        {
            throw DomainException.RuleViolation(StateRule,
                $"Modules cannot be completed on an enrolment that is {State.Code()}.");
        }
        var module = _modules.SingleOrDefault(m => m.CourseModuleId == courseModuleId)
                     ?? throw DomainException.NotFound("Course module", courseModuleId);

        module.Complete(now);
        if (State == EnrollmentState.Assigned) TransitionTo(EnrollmentState.InProgress);
        RefreshProgress();
    }

    /// <summary>
    /// BR-32: eligible only when every module is complete and attendance is
    /// at or above the minimum. Reports, changes nothing.
    /// </summary>
    public EligibilityResult CheckEligibility(AttendanceSummary attendance, TrainingRules rules)
    {
        var missing = _modules.Where(module => !module.IsComplete).Select(module => module.CourseModuleId).ToList();
        var attendanceMet = attendance.Meets(rules.MinAttendancePct);
        return new EligibilityResult(missing.Count == 0 && attendanceMet, missing, attendance.Percent,
            rules.MinAttendancePct, attendanceMet, attendance.CanStillMeet(rules.MinAttendancePct));
    }

    /// <summary>
    /// The eligibility checker: IN_PROGRESS to ELIGIBLE, and only through
    /// here. There is no other way for an enrolment to become ELIGIBLE.
    /// </summary>
    public EligibilityResult EvaluateEligibility(AttendanceSummary attendance, TrainingRules rules)
    {
        var result = CheckEligibility(attendance, rules);
        if (result.Eligible && State == EnrollmentState.InProgress) TransitionTo(EnrollmentState.Eligible);
        return result;
    }

    /// <summary>
    /// The gate in front of the assessment (BR-32). The state alone is not
    /// trusted: the check is made again, because modules can be reset and
    /// attendance corrected after an enrolment became ELIGIBLE.
    /// </summary>
    public void EnsureEligibleForAssessment(AttendanceSummary attendance, TrainingRules rules)
    {
        var result = CheckEligibility(attendance, rules);
        if (State == EnrollmentState.Eligible && result.Eligible) return;

        var details = new List<ErrorDetail>();
        if (State != EnrollmentState.Eligible) details.Add(new ErrorDetail("state", $"is {State.Code()}, not ELIGIBLE"));
        if (result.MissingModuleIds.Count > 0)
        {
            details.Add(new ErrorDetail("modules", $"{result.MissingModuleIds.Count} modules are not complete"));
        }
        if (!result.AttendanceMet)
        {
            details.Add(new ErrorDetail("attendance",
                $"{result.AttendancePercent}% is below the minimum of {result.MinAttendancePercent}%"));
        }
        throw DomainException.RuleViolation("BR-32",
            "The assessment opens only when every module is complete and attendance is at or above the minimum.",
            details: [.. details]);
    }

    /// <summary>Withdrawn by the training manager: the trainee left, or the course was withdrawn.</summary>
    public void Close() => TransitionTo(EnrollmentState.Closed);

    /// <summary>LOCKED to ASSIGNED: the trainee retakes the course from the start.</summary>
    public void ResetForRetake()
    {
        TransitionTo(EnrollmentState.Assigned);
        foreach (var module in _modules) module.Reset();
        CompletedAt = null;
        RefreshProgress();
    }

    // ---------------------------------------------------------------- assessment (driven by the domain, never by a caller)

    internal void MarkPassed(DateTimeOffset now)
    {
        TransitionTo(EnrollmentState.Passed);
        CompletedAt = now;
    }

    internal void Lock() => TransitionTo(EnrollmentState.Locked);

    /// <summary>The modules a trainee failed in the quiz have to be studied again.</summary>
    internal void ResetModules(IEnumerable<long> courseModuleIds)
    {
        var ids = courseModuleIds.ToHashSet();
        foreach (var module in _modules.Where(module => ids.Contains(module.CourseModuleId))) module.Reset();
        RefreshProgress();
    }

    private void RefreshProgress() =>
        ProgressPercent = _modules.Count == 0 ? 0 : _modules.Count(module => module.IsComplete) * 100 / _modules.Count;

    private void TransitionTo(EnrollmentState target)
    {
        if (!Transitions.Contains((State, target)))
        {
            throw DomainException.RuleViolation(StateRule,
                $"An enrolment cannot move from {State.Code()} to {target.Code()}.");
        }
        State = target;
    }
}
