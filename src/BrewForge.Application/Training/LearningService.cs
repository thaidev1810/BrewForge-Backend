using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>UC-14: a learner's own enrolments, module progress and eligibility (SCR-17, SCR-18).</summary>
public sealed class LearningService(IBrewForgeDbContext db, CourseService courses, CourseRenderer renderer,
    EnrollmentEvaluator evaluator, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<IReadOnlyList<EnrollmentDto>> MyEnrollmentsAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var enrollments = await db.Enrollments.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.EnrolledAt).ToListAsync(cancellationToken);
        return await ToDtosAsync(enrollments, cancellationToken);
    }

    public async Task<EnrollmentDto> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ToDtosAsync([await FindAsync(id, ownerOnly: false, cancellationToken)], cancellationToken))[0];

    /// <summary>The seven modules as the learner sees them, each lesson rendered from the bound version.</summary>
    public async Task<IReadOnlyList<EnrollmentModuleDto>> GetModulesAsync(long id, CancellationToken cancellationToken)
    {
        var enrollment = await FindAsync(id, ownerOnly: false, cancellationToken);
        var course = await courses.FindAsync(enrollment.CourseId, cancellationToken);
        var source = await renderer.LoadSourceAsync(course.RecipeVersionId, cancellationToken);
        var progress = enrollment.Modules.ToDictionary(m => m.CourseModuleId);

        return
        [
            .. CourseRenderer.RenderModules(course, source).Select(module =>
            {
                var done = progress.GetValueOrDefault(module.Id);
                return new EnrollmentModuleDto(module, done?.IsComplete ?? false, done?.CompletedAt);
            }),
        ];
    }

    /// <summary>
    /// UC-14. Marks a module complete, then runs the eligibility checker:
    /// when this was the last module and attendance is sufficient, the
    /// enrolment becomes ELIGIBLE (BR-32).
    /// </summary>
    public async Task<EnrollmentDto> CompleteModuleAsync(long id, long moduleId, CancellationToken cancellationToken)
    {
        var enrollment = await FindAsync(id, ownerOnly: true, cancellationToken);
        var courseType = await CourseTypeAsync(enrollment.CourseId, cancellationToken);

        enrollment.CompleteModule(moduleId, clock.GetUtcNow());
        var result = await evaluator.EvaluateAsync(enrollment, courseType, cancellationToken);

        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.CompleteModule,
            new { moduleId, enrollment.ProgressPercent, state = enrollment.State.Code(), result.Eligible });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([enrollment], cancellationToken))[0];
    }

    public async Task<EligibilityDto> EligibilityAsync(long id, CancellationToken cancellationToken)
    {
        var enrollment = await FindAsync(id, ownerOnly: false, cancellationToken);
        var course = await db.Courses.AsNoTracking().Include(c => c.Modules)
            .SingleAsync(c => c.Id == enrollment.CourseId, cancellationToken);
        var (rules, attendance) = await evaluator.LoadAsync(enrollment, course.CourseType, cancellationToken);
        var result = enrollment.CheckEligibility(attendance, rules);
        var moduleTypes = course.Modules.ToDictionary(m => m.Id, m => m.ModuleType);

        return new EligibilityDto(result.Eligible && enrollment.State == EnrollmentState.Eligible,
            [.. result.MissingModuleIds.Select(moduleId => new MissingModuleDto(moduleId, moduleTypes[moduleId]))],
            result.AttendancePercent, result.MinAttendancePercent, result.AttendanceStillReachable,
            enrollment.RetakesLeft(rules), rules.RegulationId, enrollment.State);
    }

    /// <summary>The training manager withdraws an enrolment: the trainee left, or the course was withdrawn.</summary>
    public async Task<EnrollmentDto> CloseAsync(long id, CancellationToken cancellationToken)
    {
        var enrollment = await FindAsync(id, ownerOnly: false, cancellationToken);
        enrollment.Close();
        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.Close);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([enrollment], cancellationToken))[0];
    }

    /// <summary>The training manager resets a LOCKED enrolment: the trainee retakes the course from the start.</summary>
    public async Task<EnrollmentDto> ResetAsync(long id, CancellationToken cancellationToken)
    {
        var enrollment = await FindAsync(id, ownerOnly: false, cancellationToken);
        var now = clock.GetUtcNow();
        var rules = await evaluator.RulesAsync(await CourseTypeAsync(enrollment.CourseId, cancellationToken),
            DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);

        enrollment.ResetForRetake(rules, now);
        db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.Reset,
            new { enrollment.DueDate, regulationId = rules.RegulationId });
        db.Audit(AuditEntities.User, () => enrollment.UserId, AuditActions.Notify,
            new { subject = "Course reset", message = "Your enrolment was reset. Start the course again from the first module." });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([enrollment], cancellationToken))[0];
    }


    /// <summary>
    /// Loads an enrolment the caller may see. A trainee sees only their own;
    /// an action that belongs to the learner (<paramref name="ownerOnly"/>)
    /// is the learner's alone, whatever the caller's role. Anything else is
    /// answered as not found.
    /// </summary>
    internal async Task<Enrollment> FindAsync(long id, bool ownerOnly, CancellationToken cancellationToken)
    {
        var enrollment = await db.Enrollments
                             .Include(e => e.Modules).Include(e => e.QuizAttempts)
                             .Include(e => e.PracticalEvaluations).Include(e => e.PracticalVideos)
                             .SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
                         ?? throw DomainException.NotFound("Enrolment", id);

        var mustOwn = ownerOnly || currentUser.Role == RoleName.Trainee;
        if (mustOwn && currentUser.IsAuthenticated && enrollment.UserId != currentUser.UserId)
        {
            throw DomainException.NotFound("Enrolment", id);
        }
        return enrollment;
    }

    internal Task<CourseType> CourseTypeAsync(long courseId, CancellationToken cancellationToken) =>
        db.Courses.Where(c => c.Id == courseId).Select(c => c.CourseType).SingleAsync(cancellationToken);

    private async Task<IReadOnlyList<EnrollmentDto>> ToDtosAsync(IReadOnlyList<Enrollment> enrollments,
        CancellationToken cancellationToken)
    {
        var courseIds = enrollments.Select(e => e.CourseId).Distinct().ToList();
        var facts = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.CourseType, c.RecipeVersionId })
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return
        [
            .. enrollments.Select(e =>
            {
                var course = facts[e.CourseId];
                var open = e.State is EnrollmentState.Assigned or EnrollmentState.InProgress or EnrollmentState.Eligible;
                return new EnrollmentDto(e.Id, e.CourseId, course.Title, course.CourseType, course.RecipeVersionId,
                    e.TrainingClassId, e.UserId, e.DueDate, e.ProgressPercent, e.State, e.EnrolledAt, e.CompletedAt,
                    Overdue: open && e.DueDate is { } due && due < today);
            }),
        ];
    }
}
