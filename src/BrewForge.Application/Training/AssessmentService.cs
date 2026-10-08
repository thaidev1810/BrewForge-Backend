using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.Launch;
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

public sealed record PracticalEvaluationRequest(IReadOnlyList<ChecklistMarkRequest>? Items);

public sealed record ChecklistMarkRequest(long? RecipeStepId, bool? Passed, string? Note);

public sealed record PracticalEvaluationDto(long Id, long EnrollmentId, long EvaluatedBy, bool Passed,
    DateTimeOffset EvaluatedAt, IReadOnlyList<ChecklistMark> Items, EnrollmentState EnrollmentState,
    long? CertificateId);

// ---------------------------------------------------------------- certificates

public sealed record CertificateDto(long Id, long UserId, string FullName, long? BranchId, long CourseId,
    string CourseTitle, long? RecipeVersionId, string? RecipeCode, string? RecipeName, int? VersionNo,
    DateTimeOffset IssuedAt, CertificateStatus Status, long? SupersededBy);

/// <summary>
/// UC-15 to UC-17. A certificate is never requested here: it is issued by
/// the enrolment itself the moment the last of its three conditions is met.
/// </summary>
public sealed class AssessmentService(IBrewForgeDbContext db, CourseService courses, LearningService learning,
    EnrollmentEvaluator evaluator, LaunchReadinessService readiness, ICurrentUser currentUser, TimeProvider clock)
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

    /// <summary>UC-16. 403 BR-14 if the evaluator is the trainee.</summary>
    public async Task<PracticalEvaluationDto> EvaluatePracticalAsync(long enrollmentId,
        PracticalEvaluationRequest request, CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, ownerOnly: false, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);

        var items = request.Items ?? throw DomainException.Validation("The evaluation has no items.",
            new ErrorDetail("items", "is required"));
        var errors = new FieldErrors();
        for (var i = 0; i < items.Count; i++)
        {
            errors.Check(items[i].RecipeStepId is not null, $"items[{i}].recipeStepId", "is required")
                .Check(items[i].Passed is not null, $"items[{i}].passed", "is required");
        }
        errors.ThrowIfAny();

        var checklist = course.Modules.Single(m => m.ModuleType == ModuleType.Technique).Lessons
            .Where(lesson => lesson.RecipeStepId is not null).Select(lesson => lesson.RecipeStepId!.Value).ToList();
        var evaluation = enrollment.EvaluatePractical(course, currentUser.RequireUserId(),
            [.. items.Select(i => new ChecklistMark(i.RecipeStepId!.Value, i.Passed!.Value, string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim()))],
            checklist, clock.GetUtcNow());

        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.PracticalEvaluation,
            new { evaluation.Passed, items = items.Count, traineeId = enrollment.UserId });
        var certificate = await CertifyAsync(enrollment, course, cancellationToken);

        return new PracticalEvaluationDto(evaluation.Id, enrollment.Id, evaluation.EvaluatedBy, evaluation.Passed,
            evaluation.EvaluatedAt, evaluation.Marks(), enrollment.State, certificate?.Id);
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

        var certificate = enrollment.TryCertify(course, owned, clock.GetUtcNow());
        if (certificate is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (!owned.Contains(certificate)) db.Certificates.Add(certificate);
        db.Audit(AuditEntities.Certificate, () => certificate.Id, AuditActions.IssueCertificate, new
        {
            certificate.UserId, certificate.CourseId, certificate.RecipeVersionId, enrollmentId = enrollment.Id,
            superseded = owned.Where(c => c.Status == CertificateStatus.Superseded && !ReferenceEquals(c, certificate)).Select(c => c.Id),
        });
        db.Audit(AuditEntities.User, () => enrollment.UserId, AuditActions.Notify, new
        {
            subject = "Certificate issued",
            message = $"You are now certified on '{course.Title}'.",
        });
        await db.SaveChangesAsync(cancellationToken);

        await readiness.RecomputeForCertificateAsync(certificate.UserId, certificate.RecipeVersionId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return certificate;
    }

    private async Task NotifyTrainingManagersAsync(Enrollment enrollment, Course course,
        CancellationToken cancellationToken)
    {
        var managers = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Role.RoleName == RoleName.TrainingManager && u.Status == UserStatus.Active)
            .Select(u => u.Id).ToListAsync(cancellationToken);
        foreach (var managerId in managers)
        {
            db.Audit(AuditEntities.User, () => managerId, AuditActions.Notify, new
            {
                subject = "Enrolment locked",
                message = $"Enrolment {enrollment.Id} on '{course.Title}' used every retake and is locked.",
                enrollmentId = enrollment.Id,
            });
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
