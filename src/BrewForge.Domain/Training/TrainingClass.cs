using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;

namespace BrewForge.Domain.Training;

/// <summary>The <c>class_state</c> enumeration.</summary>
public enum ClassState
{
    Planned,
    Running,
    Closed,
    Cancelled,
}

/// <summary>What a caller supplies to schedule or reschedule one session.</summary>
public sealed record SessionSpec(int? SessionNo, DateOnly ScheduledDate, TimeOnly StartTime, int DurationMinutes,
    string? Location, long TrainerId, IReadOnlyCollection<long> ModuleIds);

/// <summary>The modules covered by one session.</summary>
public sealed class SessionModule
{
    private SessionModule() { }

    internal SessionModule(long courseModuleId)
    {
        CourseModuleId = courseModuleId;
    }

    public long Id { get; private set; }
    public long SessionId { get; private set; }
    public long CourseModuleId { get; private set; }
}

/// <summary>One teaching session of a class.</summary>
public sealed class TrainingSession
{
    private readonly List<SessionModule> _modules = [];

    private TrainingSession() { }

    internal TrainingSession(int sessionNo)
    {
        SessionNo = sessionNo;
    }

    public long Id { get; private set; }
    public long TrainingClassId { get; private set; }
    public int SessionNo { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public int DurationMinutes { get; private set; }
    public string? Location { get; private set; }
    public long TrainerId { get; private set; }

    public IReadOnlyList<SessionModule> Modules => _modules;

    internal void Apply(SessionSpec spec)
    {
        ScheduledDate = spec.ScheduledDate;
        StartTime = spec.StartTime;
        DurationMinutes = spec.DurationMinutes;
        Location = string.IsNullOrWhiteSpace(spec.Location) ? null : spec.Location.Trim();
        TrainerId = spec.TrainerId;

        _modules.RemoveAll(module => !spec.ModuleIds.Contains(module.CourseModuleId));
        foreach (var moduleId in spec.ModuleIds.Where(id => _modules.All(module => module.CourseModuleId != id)))
        {
            _modules.Add(new SessionModule(moduleId));
        }
    }
}

/// <summary>A scheduled run of one course for a group of trainees at a branch (UC-29).</summary>
public sealed class TrainingClass
{
    public const string StateRule = "STATE_TRANSITION";

    /// <summary>The only transitions that exist (data dictionary, section 7).</summary>
    private static readonly HashSet<(ClassState From, ClassState To)> Transitions =
    [
        (ClassState.Planned, ClassState.Running),
        (ClassState.Running, ClassState.Closed),
        (ClassState.Planned, ClassState.Cancelled),
        (ClassState.Running, ClassState.Cancelled),
    ];

    private readonly List<TrainingSession> _sessions = [];

    private TrainingClass() { }

    public long Id { get; private set; }
    public long CourseId { get; private set; }
    public long BranchId { get; private set; }
    public string Name { get; private set; } = null!;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public long OpenedBy { get; private set; }
    public ClassState State { get; private set; } = ClassState.Planned;

    public IReadOnlyList<TrainingSession> Sessions => _sessions;

    public IReadOnlyList<TrainingSession> OrderedSessions() => [.. _sessions.OrderBy(session => session.SessionNo)];

    /// <summary>A class runs a course that trainees may be enrolled on, that is, a published one.</summary>
    public static TrainingClass Create(Course course, long branchId, string? name, DateOnly startDate,
        DateOnly endDate, long openedBy)
    {
        if (course.State != CourseState.Published)
        {
            throw DomainException.RuleViolation("COURSE_NOT_PUBLISHED",
                $"A class can only be scheduled for a published course; this one is {course.State.Code()}.");
        }

        var trainingClass = new TrainingClass { CourseId = course.Id, BranchId = branchId, OpenedBy = openedBy };
        trainingClass.SetDetails(name, startDate, endDate);
        return trainingClass;
    }

    public void Update(string? name, DateOnly startDate, DateOnly endDate)
    {
        EnsureState(ClassState.Planned, "changed");
        SetDetails(name, startDate, endDate);
    }

    /// <summary>
    /// Schedules a session. BR-34: the trainer must hold a VALID certificate
    /// on the recipe version the course is bound to. A course that is not
    /// built from a recipe has no version to be certified on.
    /// </summary>
    public TrainingSession AddSession(SessionSpec spec, Course course, IEnumerable<Certificate> trainerCertificates)
    {
        EnsureSchedulable();
        Validate(spec, course, existing: null);
        EnsureTrainerIsCertified(spec.TrainerId, course, trainerCertificates);

        var session = new TrainingSession(spec.SessionNo ?? NextSessionNo());
        session.Apply(spec);
        _sessions.Add(session);
        return session;
    }

    public void UpdateSession(TrainingSession session, SessionSpec spec, Course course,
        IEnumerable<Certificate> trainerCertificates)
    {
        EnsureSchedulable();
        EnsureOwns(session);
        Validate(spec, course, session);
        EnsureTrainerIsCertified(spec.TrainerId, course, trainerCertificates);
        session.Apply(spec);
    }

    public void RemoveSession(TrainingSession session)
    {
        EnsureState(ClassState.Planned, "have a session removed");
        EnsureOwns(session);
        _sessions.Remove(session);
    }

    /// <summary>PLANNED to RUNNING. From here on attendance is recorded.</summary>
    public void Open() => TransitionTo(ClassState.Running);

    /// <summary>RUNNING to CLOSED. Attendance is frozen.</summary>
    public void Close() => TransitionTo(ClassState.Closed);

    public void Cancel() => TransitionTo(ClassState.Cancelled);

    /// <summary>Attendance may only be recorded or corrected while the class is RUNNING.</summary>
    public void EnsureAttendanceOpen() => EnsureState(ClassState.Running, "have attendance recorded");

    private void SetDetails(string? name, DateOnly startDate, DateOnly endDate)
    {
        name = name?.Trim();
        new FieldErrors()
            .RequiredMax("name", name, 120)
            .Check(endDate >= startDate, "endDate", "must not be before startDate")
            .ThrowIfAny();
        Name = name!;
        StartDate = startDate;
        EndDate = endDate;
    }

    private void Validate(SessionSpec spec, Course course, TrainingSession? existing)
    {
        if (course.Id != CourseId) throw new ArgumentException("Not the course of this class.", nameof(course));

        var courseModules = course.Modules.Select(module => module.Id).ToHashSet();
        var sessionNo = spec.SessionNo ?? existing?.SessionNo ?? NextSessionNo();
        new FieldErrors()
            .Check(sessionNo >= 1, "sessionNo", "must be 1 or greater")
            .Check(_sessions.All(s => ReferenceEquals(s, existing) || s.SessionNo != sessionNo), "sessionNo",
                "is already used by another session of this class")
            .Check(spec.SessionNo is null || existing is null || spec.SessionNo == existing.SessionNo, "sessionNo",
                "cannot be changed")
            .Check(spec.DurationMinutes > 0, "durationMinutes", "must be greater than 0")
            .Check(spec.ScheduledDate >= StartDate && spec.ScheduledDate <= EndDate, "scheduledDate",
                $"must be within the class period {StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}")
            .MaxLength("location", spec.Location?.Trim(), 120)
            .Check(spec.ModuleIds.All(courseModules.Contains), "moduleIds", "refers to a module that is not part of the course")
            .Check(spec.ModuleIds.Distinct().Count() == spec.ModuleIds.Count, "moduleIds", "lists the same module more than once")
            .ThrowIfAny();
    }

    private static void EnsureTrainerIsCertified(long trainerId, Course course, IEnumerable<Certificate> certificates)
    {
        if (course.RecipeVersionId is not { } versionId) return;
        if (certificates.Any(certificate => certificate.Certifies(trainerId, versionId))) return;

        throw DomainException.RuleViolation("BR-34",
            "The trainer does not hold a valid certificate on the recipe version this course is bound to.",
            details: new ErrorDetail("trainerId", $"user {trainerId} is not certified on recipe version {versionId}"));
    }

    private void EnsureSchedulable()
    {
        if (State is not (ClassState.Planned or ClassState.Running))
        {
            throw DomainException.RuleViolation(StateRule, $"A {State.Code()} class cannot have its sessions changed.");
        }
    }

    private void EnsureState(ClassState required, string action)
    {
        if (State != required)
        {
            throw DomainException.RuleViolation(StateRule,
                $"A class can only {action} while it is {required.Code()}; this one is {State.Code()}.");
        }
    }

    private void TransitionTo(ClassState target)
    {
        if (!Transitions.Contains((State, target)))
        {
            throw DomainException.RuleViolation(StateRule,
                $"A class cannot move from {State.Code()} to {target.Code()}.");
        }
        State = target;
    }

    private int NextSessionNo() => _sessions.Count == 0 ? 1 : _sessions.Max(session => session.SessionNo) + 1;

    private void EnsureOwns(TrainingSession session)
    {
        if (!_sessions.Contains(session)) throw new ArgumentException("The session is not part of this class.", nameof(session));
    }
}
