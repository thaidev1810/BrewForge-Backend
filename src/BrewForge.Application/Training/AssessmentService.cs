using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.Launch;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

// ---------------------------------------------------------------- quiz

/// <summary>A question as the learner sees it: without the answer.</summary>
public sealed record LearnerQuestionDto(long Id, long CourseModuleId, ModuleType ModuleType, string QuestionText,
    IReadOnlyList<QuizOption> Options);

public sealed record LearnerQuizDto(long QuizId, string Title, int PassScore, int RetakesLeft,
    IReadOnlyList<LearnerQuestionDto> Questions);

public sealed record QuizAttemptRequest(IReadOnlyList<QuizAnswerRequest>? Answers);

public sealed record QuizAnswerRequest(long? QuestionId, string? SelectedOption);

public sealed record ModuleScoreDto(ModuleType ModuleType, int Correct, int Total);

/// <summary>The quiz attempt result of the API contract, plus the certificate when this attempt earned one.</summary>
public sealed record QuizAttemptResultDto(int AttemptNo, int Score, bool Passed, int PassScore, int RetakesLeft,
    EnrollmentState EnrollmentState, IReadOnlyList<ModuleScoreDto> PerModule, DateTimeOffset AttemptedAt,
    long? CertificateId);

// ---------------------------------------------------------------- practical

/// <summary>The recording a practical evaluation is judged from, without its content.</summary>
public sealed record PracticalVideoDto(long Id, long EnrollmentId, long UploadedBy, string FileName, string ContentType,
    long SizeBytes, string Sha256, DateTimeOffset UploadedAt, long? EvaluationId);

/// <summary>A recording as it is served: its content and what the response says about it.</summary>
public sealed record PracticalVideoContent(Stream Content, string ContentType, string FileName);

public sealed record PracticalEvaluationRequest(long? PracticalVideoId, IReadOnlyList<ChecklistMarkRequest>? Items);

/// <summary>An item is named by <c>recipeStepId</c>, or by <c>lessonId</c> on a course that is built from no recipe.</summary>
public sealed record ChecklistMarkRequest(long? RecipeStepId, long? LessonId, bool? Passed, string? Note);

public sealed record PracticalEvaluationDto(long Id, long EnrollmentId, long EvaluatedBy, bool Passed,
    DateTimeOffset EvaluatedAt, IReadOnlyList<ChecklistMark> Items, long? PracticalVideoId,
    EnrollmentState EnrollmentState, long? CertificateId);

// ---------------------------------------------------------------- certificates

public sealed record CertificateDto(long Id, long UserId, string FullName, long? BranchId, long CourseId,
    string CourseTitle, long? RecipeVersionId, string? RecipeCode, string? RecipeName, int? VersionNo,
    DateTimeOffset IssuedAt, CertificateStatus Status, long? SupersededBy);

/// <summary>
/// UC-15 to UC-17. A certificate is never requested here: it is issued by
/// the enrolment itself the moment the last of its three conditions is met.
/// </summary>
public sealed class AssessmentService(IBrewForgeDbContext db, CourseService courses, LearningService learning,
    EnrollmentEvaluator evaluator, LaunchReadinessService readiness, LearningPathService learningPath,
    IPracticalVideoStorage videos, ICurrentUser currentUser, TimeProvider clock)
{
    /// <summary>
    /// The quiz as the learner takes it. Behind the same gate as the attempt
    /// itself, so the questions cannot be read before the course is done.
    /// </summary>
    public async Task<LearnerQuizDto> GetQuizAsync(long enrollmentId, CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: true, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);
        var (rules, attendance) = await evaluator.LoadAsync(enrollment, course.CourseType, cancellationToken);
        enrollment.EnsureEligibleForAssessment(attendance, rules);

        var moduleTypes = course.Modules.ToDictionary(m => m.Id, m => m.ModuleType);
        return new LearnerQuizDto(course.Quiz.Id, course.Quiz.Title, course.Quiz.PassScore, enrollment.RetakesLeft(rules),
        [
            .. course.Quiz.Questions.OrderBy(q => q.Id).Select(q =>
                new LearnerQuestionDto(q.Id, q.CourseModuleId, moduleTypes[q.CourseModuleId], q.QuestionText, q.Options())),
        ]);
    }

    /// <summary>UC-15. 409 BR-32 if not eligible, 409 BR-33 if no retakes are left.</summary>
    public async Task<QuizAttemptResultDto> AttemptQuizAsync(long enrollmentId, QuizAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: true, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);
        var (rules, attendance) = await evaluator.LoadAsync(enrollment, course.CourseType, cancellationToken);

        var given = request.Answers ?? [];
        new FieldErrors()
            .Check(given.All(a => a.QuestionId is not null), "answers", "every answer must name a questionId")
            .Check(given.Select(a => a.QuestionId).Distinct().Count() == given.Count, "answers", "answers the same question more than once")
            .ThrowIfAny();
        var answers = given.ToDictionary(a => a.QuestionId!.Value, a => a.SelectedOption?.Trim());

        var result = enrollment.AttemptQuiz(course, answers, attendance, rules, clock.GetUtcNow());

        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.QuizAttempt, new
        {
            result.Attempt.AttemptNo, result.Attempt.Score, result.Attempt.Passed, result.RetakesLeft,
            state = enrollment.State.Code(),
        });
        if (enrollment.State == EnrollmentState.Locked)
        {
            await NotifyTrainingManagersAsync(enrollment, course, cancellationToken);
        }
        var certificate = await CertifyAsync(enrollment, course, cancellationToken);

        return ToDto(result.Attempt, result.PassScore, enrollment.RetakesLeft(rules), enrollment.State, certificate?.Id);
    }

    public async Task<IReadOnlyList<QuizAttemptResultDto>> ListAttemptsAsync(long enrollmentId,
        CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: false, cancellationToken);
        var course = await db.Courses.AsNoTracking().Include(c => c.Quiz)
            .SingleAsync(c => c.Id == enrollment.CourseId, cancellationToken);
        var rules = await evaluator.RulesAsync(course.CourseType, enrollment.EnrolledOn, cancellationToken);

        return
        [
            .. enrollment.QuizAttempts.OrderBy(a => a.AttemptNo)
                .Select(a => ToDto(a, course.Quiz.PassScore, enrollment.RetakesLeft(rules), enrollment.State, null)),
        ];
    }

    /// <summary>
    /// The recording of a practical, uploaded by the trainer who ran it:
    /// 403 BR-14 for the trainee, 403 for a trainer who does not run the
    /// practical of the class. Whatever can be refused is refused before a
    /// byte is stored.
    /// </summary>
    public async Task<PracticalVideoDto> UploadPracticalVideoAsync(long enrollmentId, string? fileName,
        long announcedLength, Stream content, CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: false, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);
        var uploaderId = currentUser.RequireUserId();
        var runsThePractical = await RunsThePracticalAsync(enrollment, uploaderId, cancellationToken);

        enrollment.EnsureAcceptsPracticalVideo(course, uploaderId, runsThePractical);
        var (extension, contentType) = PracticalVideo.Format(fileName);
        PracticalVideo.EnsureSize(announcedLength);

        var stored = await videos.SaveAsync(content, extension, PracticalVideo.MaxBytes, cancellationToken);
        if (stored is null) PracticalVideo.EnsureSize(PracticalVideo.MaxBytes + 1);
        try
        {
            var video = enrollment.AddPracticalVideo(course, uploaderId, runsThePractical,
                Path.GetFileName(fileName!.Trim()), contentType, stored!.SizeBytes, stored.Sha256, stored.Key,
                clock.GetUtcNow());
            db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.UploadPracticalVideo,
                new { video.FileName, video.SizeBytes, video.Sha256, traineeId = enrollment.UserId });
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(video, enrollment);
        }
        catch
        {
            // A file nothing refers to is not kept.
            await videos.DeleteAsync(stored!.Key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>The recordings of an enrolment, newest first, each with the evaluation it was used for.</summary>
    public async Task<IReadOnlyList<PracticalVideoDto>> ListPracticalVideosAsync(long enrollmentId,
        CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: false, cancellationToken);
        return [.. enrollment.PracticalVideos.OrderByDescending(v => v.UploadedAt).Select(v => ToDto(v, enrollment))];
    }

    /// <summary>
    /// The recording itself. Who may see it follows from its enrolment: a
    /// trainee only their own, a store-level caller only their branch.
    /// </summary>
    public async Task<PracticalVideoContent> OpenPracticalVideoAsync(long videoId, CancellationToken cancellationToken)
    {
        var video = await db.PracticalVideos.AsNoTracking().SingleOrDefaultAsync(v => v.Id == videoId, cancellationToken)
                    ?? throw DomainException.NotFound("Practical video", videoId);
        var ownOnly = currentUser.Role == RoleName.Trainee;
        var ownerId = currentUser.UserId;
        var visible = await db.Enrollments.AnyAsync(
            e => e.Id == video.EnrollmentId && (!ownOnly || e.UserId == ownerId), cancellationToken);
        var content = visible ? videos.OpenRead(video.StorageKey) : null;

        return content is null
            ? throw DomainException.NotFound("Practical video", videoId)
            : new PracticalVideoContent(content, video.ContentType, video.FileName);
    }

    /// <summary>
    /// The trainer who runs the practical of a class is the one who opened
    /// the class or teaches one of its sessions. An enrolment that belongs
    /// to no class has no such person, so any trainer may run its practical.
    /// </summary>
    private async Task<bool> RunsThePracticalAsync(Enrollment enrollment, long userId,
        CancellationToken cancellationToken)
    {
        if (enrollment.TrainingClassId is not { } classId) return true;
        return await db.TrainingClasses.AnyAsync(
            c => c.Id == classId && (c.OpenedBy == userId || c.Sessions.Any(s => s.TrainerId == userId)),
            cancellationToken);
    }

    private static PracticalVideoDto ToDto(PracticalVideo video, Enrollment enrollment) =>
        new(video.Id, video.EnrollmentId, video.UploadedBy, video.FileName, video.ContentType, video.SizeBytes,
            video.Sha256, video.UploadedAt,
            enrollment.PracticalEvaluations.FirstOrDefault(e => e.PracticalVideoId == video.Id)?.Id);

    /// <summary>
    /// UC-16. 403 BR-14 if the evaluator is the trainee; 409 without the
    /// recording of the practical, uploaded by the evaluator.
    /// </summary>
    public async Task<PracticalEvaluationDto> EvaluatePracticalAsync(long enrollmentId,
        PracticalEvaluationRequest request, CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: false, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);

        var items = request.Items ?? throw DomainException.Validation("The evaluation has no items.",
            new ErrorDetail("items", "is required"));
        var bySteps = course.RecipeVersionId is not null;
        var errors = new FieldErrors();
        for (var i = 0; i < items.Count; i++)
        {
            errors.Check(bySteps ? items[i].RecipeStepId is not null : items[i].LessonId is not null,
                    $"items[{i}].{(bySteps ? "recipeStepId" : "lessonId")}", "is required")
                .Check(items[i].Passed is not null, $"items[{i}].passed", "is required");
        }
        errors.ThrowIfAny();

        var checklist = course.PracticalChecklistItemIds();
        var video = enrollment.PracticalVideos.SingleOrDefault(v => v.Id == request.PracticalVideoId);
        if (request.PracticalVideoId is not null && video is null)
        {
            throw DomainException.Validation("The evaluation names a recording that is not one of this enrolment.",
                new ErrorDetail("practicalVideoId", "is not a recording of this enrolment"));
        }
        var evaluation = enrollment.EvaluatePractical(course, currentUser.RequireUserId(),
            [.. items.Select(i => new ChecklistMark(bySteps ? i.RecipeStepId : null, i.Passed!.Value,
                string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim(), bySteps ? null : i.LessonId))],
            checklist, video, clock.GetUtcNow());

        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.PracticalEvaluation,
            new { evaluation.Passed, items = items.Count, traineeId = enrollment.UserId, practicalVideoId = video?.Id });
        var certificate = await CertifyAsync(enrollment, course, cancellationToken);

        return new PracticalEvaluationDto(evaluation.Id, enrollment.Id, evaluation.EvaluatedBy, evaluation.Passed,
            evaluation.EvaluatedAt, evaluation.Marks(), evaluation.PracticalVideoId, enrollment.State, certificate?.Id);
    }

    public async Task<IReadOnlyList<CertificateDto>> MyCertificatesAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        return await ToDtosAsync(db.Certificates.AsNoTracking().Where(c => c.UserId == userId), cancellationToken);
    }

    public async Task<CertificateDto> GetCertificateAsync(long id, CancellationToken cancellationToken)
    {
        var certificate = (await ToDtosAsync(db.Certificates.AsNoTracking().Where(c => c.Id == id), cancellationToken))
                          .SingleOrDefault() ?? throw DomainException.NotFound("Certificate", id);
        // A trainee sees only their own; anyone else's does not exist for them.
        if (currentUser.Role == RoleName.Trainee && certificate.UserId != currentUser.UserId)
        {
            throw DomainException.NotFound("Certificate", id);
        }
        return certificate;
    }

    /// <summary>
    /// Saves the assessment just made and lets the enrolment issue its
    /// certificate if it now can (BR-21). Issuing recounts the certified
    /// staff of the trainee's branch for the launch gate.
    /// </summary>
    private async Task<Certificate?> CertifyAsync(Enrollment enrollment, Course course,
        CancellationToken cancellationToken)
    {
        var owned = await db.Certificates.Where(c => c.UserId == enrollment.UserId && c.CourseId == course.Id)
            .ToListAsync(cancellationToken);

        var onEarlierVersions = await CertificatesOnEarlierVersionsAsync(enrollment.UserId, course, cancellationToken);

        var certificate = enrollment.TryCertify(course, owned, clock.GetUtcNow(), onEarlierVersions);
        if (certificate is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (!owned.Contains(certificate)) db.Certificates.Add(certificate);
        db.Audit(AuditEntities.Certificate, () => certificate.Id, AuditActions.IssueCertificate, new
        {
            certificate.UserId, certificate.CourseId, certificate.RecipeVersionId, enrollmentId = enrollment.Id,
            superseded = owned.Concat(onEarlierVersions)
                .Where(c => c.Status == CertificateStatus.Superseded && !ReferenceEquals(c, certificate)).Select(c => c.Id).Distinct(),
        });
        db.Notify(enrollment.UserId, "Certificate issued", $"You are now certified on '{course.Title}'.",
            clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        await readiness.RecomputeForCertificateAsync(certificate.UserId, certificate.RecipeVersionId, cancellationToken);
        // Passing a course that others require opens the next stage of the holder's path.
        if (await db.TrainingRegulations.AnyAsync(r => r.PrerequisiteType == course.CourseType, cancellationToken))
        {
            await learningPath.AssignOpenStagesAsync(certificate.UserId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return certificate;

    }

    /// <summary>
    /// The certificates the user holds, through any other course, on earlier
    /// versions of the drink this course is bound to. One earned on this
    /// version replaces them.
    /// </summary>
    private async Task<List<Certificate>> CertificatesOnEarlierVersionsAsync(long userId, Course course,
        CancellationToken cancellationToken)
    {
        if (course.RecipeVersionId is not { } versionId) return [];
        var version = await db.RecipeVersions.AsNoTracking().Where(v => v.Id == versionId)
            .Select(v => new { v.RecipeId, v.VersionNo }).SingleAsync(cancellationToken);
        var earlier = await db.RecipeVersions.AsNoTracking()
            .Where(v => v.RecipeId == version.RecipeId && v.VersionNo < version.VersionNo)
            .Select(v => v.Id).ToListAsync(cancellationToken);

        return await db.Certificates
            .Where(c => c.UserId == userId && c.CourseId != course.Id && c.RecipeVersionId != null
                        && earlier.Contains(c.RecipeVersionId.Value) && c.Status != CertificateStatus.Superseded)
            .ToListAsync(cancellationToken);
    }

    private async Task NotifyTrainingManagersAsync(Enrollment enrollment, Course course,
        CancellationToken cancellationToken)
    {
        var managers = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Role.RoleName == RoleName.TrainingManager && u.Status == UserStatus.Active)
            .Select(u => u.Id).ToListAsync(cancellationToken);
        foreach (var managerId in managers)
        {
            db.Notify(managerId, "Enrolment locked",
                $"Enrolment {enrollment.Id} on '{course.Title}' used every retake and is locked.",
                clock.GetUtcNow(), new { enrollmentId = enrollment.Id });
        }
    }

    private async Task<List<CertificateDto>> ToDtosAsync(IQueryable<Certificate> certificates,
        CancellationToken cancellationToken)
    {
        var rows = await certificates
            .Join(db.Users.IgnoreQueryFilters(), c => c.UserId, u => u.Id, (c, u) => new { Certificate = c, u.FullName, u.BranchId })
            .Join(db.Courses, x => x.Certificate.CourseId, course => course.Id, (x, course) => new { x.Certificate, x.FullName, x.BranchId, course.Title })
            .OrderByDescending(x => x.Certificate.IssuedAt)
            .ToListAsync(cancellationToken);

        var versionIds = rows.Select(r => r.Certificate.RecipeVersionId).OfType<long>().Distinct().ToList();
        var versions = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, v.VersionNo, r.RecipeCode, r.Name })
            .ToDictionaryAsync(v => v.Id, cancellationToken);

        return
        [
            .. rows.Select(r =>
            {
                var version = r.Certificate.RecipeVersionId is { } id ? versions.GetValueOrDefault(id) : null;
                return new CertificateDto(r.Certificate.Id, r.Certificate.UserId, r.FullName, r.BranchId,
                    r.Certificate.CourseId, r.Title, r.Certificate.RecipeVersionId, version?.RecipeCode, version?.Name,
                    version?.VersionNo, r.Certificate.IssuedAt, r.Certificate.Status, r.Certificate.SupersededBy);
            }),
        ];
    }

    private static QuizAttemptResultDto ToDto(QuizAttempt attempt, int passScore, int retakesLeft,
        EnrollmentState state, long? certificateId) =>
        new(attempt.AttemptNo, attempt.Score, attempt.Passed, passScore, retakesLeft, state,
            [.. attempt.Sheet().PerModule.Select(m => new ModuleScoreDto(m.ModuleType, m.Correct, m.Total))],
            attempt.AttemptedAt, certificateId);
}
