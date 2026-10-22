using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>
/// The stage of an assignment in the training flow, derived from the enrolment. Being past the
/// deadline is a separate flag (<c>isOverdue</c>), not a stage, so it never hides the real progress.
/// </summary>
public enum AssignmentStage
{
    Assigned,
    InProgress,
    TheoryCompleted,
    QuizPassed,
    PracticalPending,
    Completed,
    Locked,
    Closed,
}

public sealed record AssignmentRequest(long? CourseId, long? BranchId, IReadOnlyList<long>? EmployeeIds,
    bool? AllTrainees, DateOnly? Deadline, string? Reason);

public sealed record AssignmentResultDto(long CourseId, IReadOnlyList<long> EnrollmentIds,
    IReadOnlyList<long> AlreadyEnrolled);

/// <summary>What became of one named user in an assignment: a fresh enrolment, or the open one they already had.</summary>
public sealed record AssignmentOutcome(long UserId, long EnrollmentId, bool AlreadyEnrolled);

public sealed record AssignmentDto(long EnrollmentId, long UserId, string? FullName, long? BranchId, string? BranchCode,
    long CourseId, string CourseTitle, DateOnly? DueDate, int ProgressPercent, AssignmentStage Stage, bool IsOverdue,
    EnrollmentState EnrollmentState);

/// <summary>
/// A trainer gives a published course to the staff of a branch with a deadline and a reason. The result is
/// an ordinary enrolment, outside any class; the status shown is derived from it.
/// </summary>
public sealed class AssignmentService(IBrewForgeDbContext db, EnrollmentEvaluator evaluator, CourseService courses,
    LearningPathService learningPath, TimeProvider clock)
{
    public const int MaxReasonLength = 255;

    private static readonly SortMap<Enrollment> Sorting = new SortMap<Enrollment>("dueDate", e => e.Id)
        .Add("id", e => e.Id)
        .Add("dueDate", e => e.DueDate)
        .Add("enrolledAt", e => e.EnrolledAt)
        .Add("progressPercent", e => e.ProgressPercent);

    public async Task<PagedResult<AssignmentDto>> ListAsync(PageQuery paging, long? branchId, long? courseId,
        string? stage, bool? overdue, CancellationToken cancellationToken)
    {
        var stageFilter = PagingExtensions.ParseFilter<AssignmentStage>(stage, "stage");

        var query = db.Enrollments.AsNoTracking().Include(e => e.User).Include(e => e.Modules)
            .Include(e => e.QuizAttempts).Include(e => e.PracticalEvaluations).AsQueryable();
        if (branchId is not null) query = query.Where(e => e.User.BranchId == branchId);
        if (courseId is not null) query = query.Where(e => e.CourseId == courseId);

        if (stageFilter is null && overdue is null)
        {
            var page = await query.ToPagedAsync(paging, Sorting, e => e, cancellationToken);
            return new PagedResult<AssignmentDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size,
                page.Total);
        }

        // Stage and overdue are derived from the enrolment, so they cannot be filtered in the database.
        // The caller's sort and page bounds are still honoured: validate and order exactly as the
        // unfiltered branch does, then derive, filter and page the rows in memory.
        new FieldErrors()
            .Check(paging.Page >= 1, "page", "must be 1 or greater")
            .Check(paging.Size is >= 1 and <= PageQuery.MaxSize, "size", $"must be between 1 and {PageQuery.MaxSize}")
            .ThrowIfAny();
        var ordered = await Sorting.Apply(query, paging.Sort).ToListAsync(cancellationToken);
        var matching = (await ToDtosAsync(ordered, cancellationToken))
            .Where(a => (stageFilter is null || a.Stage == stageFilter) && (overdue is null || a.IsOverdue == overdue))
            .ToList();
        return new PagedResult<AssignmentDto>(
            [.. matching.Skip((paging.Page - 1) * paging.Size).Take(paging.Size)], paging.Page, paging.Size, matching.Count);
    }

    public async Task<AssignmentResultDto> AssignAsync(AssignmentRequest request, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        new FieldErrors()
            .Check(request.CourseId is not null, "courseId", "is required")
            .Check(request.BranchId is not null, "branchId", "is required")
            .Check(request.AllTrainees == true || request.EmployeeIds is { Count: > 0 }, "employeeIds",
                "name the employees, or set allTrainees")
            .MaxLength("reason", reason, MaxReasonLength)
            .ThrowIfAny();

        var course = await courses.FindAsync(request.CourseId!.Value, cancellationToken);
        var branchId = request.BranchId!.Value;
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw DomainException.NotFound("Branch", branchId);
        }

        var users = await RosterAsync(branchId, request, cancellationToken);
        // G-09: a course is taken after its prerequisite is passed, the same as when a class is opened.
        // Named employees who have not are refused by name; of a whole branch, those who qualify go on.
        var named = request.EmployeeIds is { Count: > 0 };
        var eligible = await learningPath.OnlyWithPrerequisiteAsync([.. users], course.CourseType, named, cancellationToken);
        var outcomes = await AssignToAsync(course, eligible, request.Deadline, reason, cancellationToken);
        return new AssignmentResultDto(course.Id,
            [.. outcomes.Where(o => !o.AlreadyEnrolled).Select(o => o.EnrollmentId)],
            [.. outcomes.Where(o => o.AlreadyEnrolled).Select(o => o.UserId)]);
    }

    /// <summary>Enrols the users on the course, skipping those who already hold an open enrolment on it.</summary>
    internal async Task<IReadOnlyList<AssignmentOutcome>> AssignToAsync(Course course, IReadOnlyList<AppUser> users,
        DateOnly? deadline, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (deadline is not null && deadline < today)
        {
            throw DomainException.Validation("The deadline is in the past.", new ErrorDetail("deadline", "must not be in the past"));
        }

        var rules = await evaluator.RulesAsync(course.CourseType, today, cancellationToken);
        var ids = users.Select(u => u.Id).ToList();
        var already = (await db.Enrollments.IgnoreQueryFilters()
                .Where(e => e.CourseId == course.Id && e.State != EnrollmentState.Closed && ids.Contains(e.UserId))
                .Select(e => new { e.UserId, e.Id }).ToListAsync(cancellationToken))
            .ToDictionary(e => e.UserId, e => e.Id);

        var created = new Dictionary<long, Enrollment>();
        foreach (var user in users.Where(u => !already.ContainsKey(u.Id)))
        {
            var enrollment = Enrollment.Assign(course, user.Id, null, rules, now, deadline);
            db.Enrollments.Add(enrollment);
            created[user.Id] = enrollment;
            db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.Assign,
                new { enrollment.CourseId, enrollment.UserId, enrollment.DueDate, reason });
            db.Notify(user.Id, "Course assigned",
                $"You have been assigned '{course.Title}'. Due {enrollment.DueDate:yyyy-MM-dd}."
                + (reason is null ? "" : $" Reason: {reason}"), now);
        }
        await db.SaveChangesAsync(cancellationToken);

        return [.. users.Select(u => already.TryGetValue(u.Id, out var existing)
            ? new AssignmentOutcome(u.Id, existing, AlreadyEnrolled: true)
            : new AssignmentOutcome(u.Id, created[u.Id].Id, AlreadyEnrolled: false))];
    }

    private async Task<IReadOnlyList<AppUser>> RosterAsync(long branchId, AssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EmployeeIds is not { Count: > 0 })
        {
            var all = await db.Users.Include(u => u.Role)
                .Where(u => u.BranchId == branchId && u.Role.RoleName == RoleName.Trainee && u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);
            if (all.Count == 0)
            {
                throw DomainException.RuleViolation("ASSIGNMENT_HAS_NO_TRAINEES",
                    "The branch has no active trainee. Name the employees explicitly.");
            }
            return all;
        }

        var ids = request.EmployeeIds.Distinct().ToList();
        var users = await db.Users.Include(u => u.Role).Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
        var errors = new FieldErrors();
        foreach (var id in ids)
        {
            var user = users.SingleOrDefault(u => u.Id == id);
            errors.Check(user is not null, "employeeIds", $"user {id} does not exist")
                .Check(user is null || user.IsActive, "employeeIds", $"user {id} is inactive")
                .Check(user is null || user.BranchId == branchId, "employeeIds", $"user {id} belongs to another branch")
                .Check(user is null || user.Role.RoleName is RoleName.Trainee or RoleName.Trainer, "employeeIds",
                    $"user {id} is neither a trainee nor a trainer");
        }
        errors.ThrowIfAny();
        return users;
    }

    private async Task<IReadOnlyList<AssignmentDto>> ToDtosAsync(IReadOnlyList<Enrollment> enrollments,
        CancellationToken cancellationToken)
    {
        var courseIds = enrollments.Select(e => e.CourseId).Distinct().ToList();
        var titles = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, cancellationToken);
        // An appointment counts only if it belongs to the CURRENT cycle: one made before the enrolment
        // last started (a reset moves EnrolledAt forward and cancels the old ones) must not move the stage.
        var enrolledAt = enrollments.ToDictionary(e => e.Id, e => e.EnrolledAt);
        var enrollmentIds = enrolledAt.Keys.ToList();
        var scheduled = (await db.PracticalSchedules.AsNoTracking()
                .Where(s => s.Status == ScheduleStatus.Scheduled && enrollmentIds.Contains(s.EnrollmentId))
                .Select(s => new { s.EnrollmentId, s.CreatedAt }).ToListAsync(cancellationToken))
            .Where(s => enrolledAt.TryGetValue(s.EnrollmentId, out var at) && s.CreatedAt >= at)
            .Select(s => s.EnrollmentId).ToHashSet();
        var branchIds = enrollments.Select(e => e.User.BranchId).Where(b => b is not null).Distinct().ToList();
        var codes = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return [.. enrollments.Select(e =>
        {
            var stage = StageOf(e, scheduled.Contains(e.Id));
            // Overdue is a separate flag, and only while the work is still outstanding.
            var isOverdue = e.DueDate is { } due && due < today
                && stage is not (AssignmentStage.Completed or AssignmentStage.Closed or AssignmentStage.Locked);
            return new AssignmentDto(e.Id, e.UserId, e.User.FullName, e.User.BranchId,
                e.User.BranchId is { } b ? codes.GetValueOrDefault(b) : null, e.CourseId,
                titles.GetValueOrDefault(e.CourseId) ?? "", e.DueDate, e.ProgressPercent, stage, isOverdue, e.State);
        })];
    }

    /// <summary>
    /// The stage of an assignment, read from the enrolment and the CURRENT training cycle only: a reset
    /// starts a fresh cycle, so quiz attempts, appointments and practicals from before it do not count.
    /// The enrolment's own state wins over any history — a CLOSED or LOCKED cycle is reported as such,
    /// and COMPLETED means this enrolment reached PASSED (its cycle certified), not that the person once
    /// held a certificate (an older or NEEDS_RECERT certificate does not complete a new cycle). A passed
    /// quiz is QUIZ_PASSED until a practical is booked or has been failed this cycle, then PRACTICAL_PENDING.
    /// Being past the deadline is reported by isOverdue, not here; a cancelled appointment is not a booking,
    /// and completing one is not passing the practical.
    /// </summary>
    internal static AssignmentStage StageOf(Enrollment e, bool practicalBooked)
    {
        if (e.State == EnrollmentState.Closed) return AssignmentStage.Closed;
        if (e.State == EnrollmentState.Locked) return AssignmentStage.Locked;

        return e.State switch
        {
            EnrollmentState.Assigned => AssignmentStage.Assigned,
            EnrollmentState.InProgress => AssignmentStage.InProgress,
            EnrollmentState.Eligible when !e.HasPassedQuiz => AssignmentStage.TheoryCompleted,
            EnrollmentState.Eligible when practicalBooked || e.LatestPractical() is { Passed: false }
                => AssignmentStage.PracticalPending,
            EnrollmentState.Eligible => AssignmentStage.QuizPassed,
            EnrollmentState.Passed => AssignmentStage.Completed,
            _ => AssignmentStage.Assigned,
        };
    }
}
