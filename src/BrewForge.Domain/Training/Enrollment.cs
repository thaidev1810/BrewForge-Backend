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
    private readonly List<QuizAttempt> _quizAttempts = [];
    private readonly List<PracticalEvaluation> _practicalEvaluations = [];
    private readonly List<PracticalVideo> _practicalVideos = [];

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

    /// <summary>Every quiz attempt ever made on this enrolment, including those before a reset.</summary>
    public IReadOnlyList<QuizAttempt> QuizAttempts => _quizAttempts;
    public IReadOnlyList<PracticalEvaluation> PracticalEvaluations => _practicalEvaluations;

    /// <summary>Every recording of a practical of this enrolment. Each is the evidence of one evaluation.</summary>
    public IReadOnlyList<PracticalVideo> PracticalVideos => _practicalVideos;

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

    /// <summary>
    /// LOCKED to ASSIGNED: the trainee retakes the course from the start. The
    /// enrolment starts over as of now, under the regulation in force now:
    /// progress is cleared, the deadline is set again, and attempts made
    /// before this moment no longer count against the retake limit.
    /// </summary>
    public void ResetForRetake(TrainingRules rules, DateTimeOffset now)
    {
        TransitionTo(EnrollmentState.Assigned);
        foreach (var module in _modules) module.Reset();
        EnrolledAt = now;
        DueDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(rules.DueDays);
        CompletedAt = null;
        RefreshProgress();
    }

    // ---------------------------------------------------------------- assessment

    /// <summary>The attempts of the current cycle: those made since the enrolment was created or last reset.</summary>
    public IReadOnlyList<QuizAttempt> CurrentAttempts() =>
        [.. _quizAttempts.Where(attempt => attempt.AttemptedAt >= EnrolledAt).OrderBy(attempt => attempt.AttemptNo)];

    /// <summary>BR-33: how many more times the quiz may be attempted after a failure.</summary>
    public int RetakesLeft(TrainingRules rules) =>
        Math.Max(0, rules.MaxRetakes - Math.Max(0, CurrentAttempts().Count - 1));

    public bool HasPassedQuiz => CurrentAttempts().Any(attempt => attempt.Passed);

    /// <summary>The verdict that counts is the most recent one of the current cycle.</summary>
    public PracticalEvaluation? LatestPractical() =>
        _practicalEvaluations.Where(evaluation => evaluation.EvaluatedAt >= EnrolledAt)
            .OrderByDescending(evaluation => evaluation.EvaluatedAt).ThenByDescending(evaluation => evaluation.Id)
            .FirstOrDefault();

    /// <summary>
    /// UC-15. Scores an attempt against the pass mark of the quiz and says,
    /// module by module, where the trainee went wrong. Refused when no retake
    /// is left (BR-33) and unless the enrolment passes the eligibility gate
    /// at this moment (BR-32).
    ///
    /// On a failure the modules the trainee got wrong are reset, so they are
    /// studied again before the next attempt, and the enrolment is LOCKED
    /// when that was the last attempt the regulation allows.
    /// </summary>
    public QuizResult AttemptQuiz(Course course, IReadOnlyDictionary<long, string?> answers,
        AttendanceSummary attendance, TrainingRules rules, DateTimeOffset now)
    {
        EnsureCourseIsCurrent(course, ErrorCodes.CourseBeingUpdated,
            "This course is being updated to a newer recipe version. Wait for the updated course before taking the quiz.");

        var attemptsSoFar = CurrentAttempts().Count;
        if (State == EnrollmentState.Locked || attemptsSoFar > rules.MaxRetakes)
        {
            throw DomainException.RuleViolation("BR-33",
                $"No retakes are left: the regulation allows {rules.MaxRetakes} after the first attempt. " +
                "The training manager can reset the enrolment.",
                details: new ErrorDetail("attempts", $"{attemptsSoFar} of {rules.MaxRetakes + 1} used"));
        }
        EnsureEligibleForAssessment(attendance, rules);
        if (HasPassedQuiz)
        {
            throw DomainException.RuleViolation("QUIZ_ALREADY_PASSED", "The quiz of this enrolment has already been passed.");
        }

        var questions = course.Quiz.Questions;
        var unknown = answers.Keys.Where(id => questions.All(question => question.Id != id)).ToList();
        new FieldErrors()
            .Check(unknown.Count == 0, "answers", $"refers to questions that are not in this quiz: {string.Join(", ", unknown)}")
            .ThrowIfAny();

        var moduleTypes = course.Modules.ToDictionary(module => module.Id, module => module.ModuleType);
        var perModule = questions
            .GroupBy(question => question.CourseModuleId)
            .Select(group => new ModuleScore(group.Key, moduleTypes[group.Key],
                group.Count(question => answers.GetValueOrDefault(question.Id) == question.CorrectOption), group.Count()))
            .OrderBy(score => score.ModuleType)
            .ToList();
        var correct = perModule.Sum(score => score.Correct);
        var score = questions.Count == 0 ? 0 : correct * 100 / questions.Count;
        var passed = questions.Count > 0 && score >= course.Quiz.PassScore;

        var attemptNo = _quizAttempts.Count == 0 ? 1 : _quizAttempts.Max(attempt => attempt.AttemptNo) + 1;
        var attempt = new QuizAttempt(course.Quiz.Id, attemptNo, score, passed, new QuizAnswerSheet(answers, perModule), now);
        _quizAttempts.Add(attempt);

        if (!passed)
        {
            ResetModules(perModule.Where(module => module.Failed).Select(module => module.CourseModuleId));
            // That was the last attempt the regulation allows.
            if (CurrentAttempts().Count > rules.MaxRetakes) Lock();
        }
        return new QuizResult(attempt, course.Quiz.PassScore, RetakesLeft(rules), perModule);
    }

    /// <summary>
    /// Whether a recording of the practical may be uploaded now, asked before
    /// the file is stored. It is uploaded by the trainer who runs the
    /// practical and by nobody else: never by the trainee (BR-14), and while
    /// the practical is open, as an evaluation is.
    /// </summary>
    public void EnsureAcceptsPracticalVideo(Course course, long uploaderId, bool uploaderRunsThePractical)
    {
        if (uploaderId == UserId)
        {
            throw DomainException.Forbidden("BR-14", "A trainee can never upload the recording of their own practical.");
        }
        if (!uploaderRunsThePractical)
        {
            throw DomainException.Forbidden(PracticalVideo.Rule,
                "The recording of a practical is uploaded by the trainer who runs the practical of this class.");
        }
        EnsurePracticalIsOpen(course);
    }

    /// <summary>Records a recording that has been stored. The same refusals as <see cref="EnsureAcceptsPracticalVideo"/>.</summary>
    public PracticalVideo AddPracticalVideo(Course course, long uploaderId, bool uploaderRunsThePractical,
        string fileName, string contentType, long sizeBytes, string sha256, string storageKey, DateTimeOffset now)
    {
        EnsureAcceptsPracticalVideo(course, uploaderId, uploaderRunsThePractical);
        PracticalVideo.EnsureSize(sizeBytes);

        var video = new PracticalVideo(uploaderId, fileName, contentType, sizeBytes, sha256, storageKey, now);
        _practicalVideos.Add(video);
        return video;
    }

    /// <summary>
    /// UC-16. Records the trainer's observation of every item of the
    /// practical checklist. The evaluator may never be the trainee (BR-14),
    /// and the marks must cover the checklist exactly: an item left out
    /// cannot be an item passed. The evaluation is judged from a recording
    /// of the practical, uploaded by the evaluator; without one there is no
    /// evaluation, and one recording is the evidence of one evaluation.
    /// </summary>
    public PracticalEvaluation EvaluatePractical(Course course, long evaluatorId, IReadOnlyList<ChecklistMark> marks,
        IReadOnlyCollection<long> checklistItemIds, PracticalVideo? video, DateTimeOffset now)
    {
        if (evaluatorId == UserId)
        {
            throw DomainException.Forbidden("BR-14", "A trainee can never evaluate or certify themselves.");
        }
        EnsurePracticalIsOpen(course);

        if (video is null)
        {
            throw DomainException.RuleViolation(PracticalVideo.Rule,
                "A practical evaluation needs the recording of the practical. Upload it, then name it in the evaluation.",
                details: new ErrorDetail("practicalVideoId", "is required"));
        }
        if (!_practicalVideos.Contains(video) || video.UploadedAt < EnrolledAt)
        {
            throw DomainException.RuleViolation(PracticalVideo.Rule,
                "That recording is not one of the present practical of this enrolment.",
                details: new ErrorDetail("practicalVideoId", "is not a recording of this practical"));
        }
        if (video.UploadedBy != evaluatorId)
        {
            throw DomainException.Forbidden(PracticalVideo.Rule,
                "A practical is evaluated by the trainer who ran it and uploaded its recording.");
        }
        if (_practicalEvaluations.Any(evaluation => ReferenceEquals(evaluation.Video, video)))
        {
            throw DomainException.RuleViolation(PracticalVideo.Rule,
                "That recording already belongs to an evaluation. Every practical has its own recording.",
                details: new ErrorDetail("practicalVideoId", "is already used by another evaluation"));
        }

        // Items are steps of the recipe, or lessons where the course is built from no recipe.
        var bySteps = course.RecipeVersionId is not null;
        var marked = marks.Select(mark => mark.ItemId()).ToList();
        new FieldErrors()
            .Check(checklistItemIds.Count > 0, "items", "this course has no practical checklist to evaluate")
            .Check(marks.All(mark => bySteps
                    ? mark is { RecipeStepId: not null, LessonId: null }
                    : mark is { LessonId: not null, RecipeStepId: null }),
                "items", bySteps ? "every item is marked by its recipeStepId" : "every item is marked by its lessonId")
            .Check(marked.Distinct().Count() == marked.Count, "items", "marks the same item more than once")
            .Check(marked.All(checklistItemIds.Contains), "items", "marks an item that is not on the practical checklist")
            .Check(checklistItemIds.All(marked.Contains), "items", "must mark every item of the practical checklist")
            .Check(marks.All(mark => mark.Note is null || mark.Note.Length <= 500), "items", "a note may have at most 500 characters")
            .ThrowIfAny();

        var evaluation = new PracticalEvaluation(evaluatorId, marks, video, now);
        _practicalEvaluations.Add(evaluation);
        return evaluation;
    }

    private void EnsurePracticalIsOpen(Course course)
    {
        EnsureCourseIsCurrent(course, ErrorCodes.SourceVersionSuperseded,
            "The recipe version behind this course was superseded. Reload the current course before submitting.");
        if (State != EnrollmentState.Eligible)
        {
            throw DomainException.RuleViolation("BR-32",
                $"The practical evaluation opens once the enrolment is ELIGIBLE; this one is {State.Code()}.",
                details: new ErrorDetail("state", $"is {State.Code()}, not ELIGIBLE"));
        }
    }

    /// <summary>
    /// UC-17. Issues the certificate when, and only when, all three
    /// conditions hold: every module complete, the quiz passed and every
    /// practical item passed (BR-21). The certificate is bound to the recipe
    /// version the course was built from (BR-13), any older certificate of
    /// the same user for the course is superseded, and the enrolment becomes
    /// PASSED. Returns null while a condition is still open.
    ///
    /// This is the only place in the system that creates a certificate.
    /// </summary>
    public Certificate? TryCertify(Course course, IReadOnlyCollection<Certificate> certificatesOfUser,
        DateTimeOffset now)
    {
        if (course.Id != CourseId) throw new ArgumentException("Not the course of this enrolment.", nameof(course));
        if (State != EnrollmentState.Eligible) return null;
        if (_modules.Any(module => !module.IsComplete)) return null;
        if (!HasPassedQuiz || LatestPractical() is not { Passed: true }) return null;

        var ofThisCourse = certificatesOfUser.Where(c => c.UserId == UserId && c.CourseId == CourseId).ToList();
        var certificate = ofThisCourse.SingleOrDefault(c => c.RecipeVersionId == course.RecipeVersionId);
        if (certificate is null)
        {
            certificate = new Certificate(UserId, CourseId, course.RecipeVersionId, now);
        }
        else
        {
            certificate.Renew(now);
        }
        foreach (var older in ofThisCourse.Where(c => !ReferenceEquals(c, certificate) && c.Status != CertificateStatus.Superseded))
        {
            older.SupersedeBy(certificate);
        }

        MarkPassed(now);
        return certificate;
    }

    private static void EnsureCourseIsCurrent(Course course, string code, string message)
    {
        if (course.State != CourseState.Published)
        {
            throw DomainException.RuleViolation("BR-15", message, code,
                new ErrorDetail("course", $"is {course.State.Code()}"));
        }
    }

    private void MarkPassed(DateTimeOffset now)
    {
        TransitionTo(EnrollmentState.Passed);
        CompletedAt = now;
    }

    private void Lock() => TransitionTo(EnrollmentState.Locked);

    /// <summary>The modules a trainee failed in the quiz have to be studied again.</summary>
    private void ResetModules(IEnumerable<long> courseModuleIds)
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
