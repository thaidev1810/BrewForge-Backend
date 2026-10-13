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

/// <summary>Where one assignment stands, in the words of the training flow.</summary>
public enum AssignmentStatus
{
    Assigned,
    InProgress,
    TheoryCompleted,
    QuizPassed,
    PracticalPending,
    Completed,
    Overdue,
    Locked,
    Closed,
}

public sealed record AssignmentRequest(long? CourseId, long? BranchId, IReadOnlyList<long>? EmployeeIds,
    bool? AllTrainees, DateOnly? Deadline, string? Reason);

public sealed record AssignmentResultDto(long CourseId, IReadOnlyList<long> EnrollmentIds,
    IReadOnlyList<long> AlreadyEnrolled);

public sealed record AssignmentDto(long EnrollmentId, long UserId, string? FullName, long? BranchId, string? BranchCode,
    long CourseId, string CourseTitle, DateOnly? DueDate, int ProgressPercent, AssignmentStatus Status,
    EnrollmentState EnrollmentState);

/// <summary>
/// A trainer gives a published course to the staff of a branch with a deadline and a reason. The result is
/// an ordinary enrolment, outside any class; the status shown is derived from it.
/// </summary>
public sealed class AssignmentService(IBrewForgeDbContext db, EnrollmentEvaluator evaluator, CourseService courses,
    TimeProvider clock)
{
    public const int MaxReasonLength = 255;

    private static readonly SortMap<Enrollment> Sorting = new SortMap<Enrollment>("dueDate", e => e.Id)
        .Add("id", e => e.Id)
        .Add("dueDate", e => e.DueDate)
        .Add("enrolledAt", e => e.EnrolledAt)
        .Add("progressPercent", e => e.ProgressPercent);

    public async Task<PagedResult<AssignmentDto>> ListAsync(PageQuery paging, long? branchId, long? courseId,
        string? status, CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<AssignmentStatus>(status, "status");

        var query = db.Enrollments.AsNoTracking().Include(e => e.User).Include(e => e.Modules)
            .Include(e => e.PracticalEvaluations).AsQueryable();
        if (branchId is not null) query = query.Where(e => e.User.BranchId == branchId);
        if (courseId is not null) query = query.Where(e => e.CourseId == courseId);

        if (statusFilter is null)
        {
            var page = await query.ToPagedAsync(paging, Sorting, e => e, cancellationToken);
            return new PagedResult<AssignmentDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size,
                page.Total);
        }

        // The status is derived from the enrolment, so it cannot be filtered in the database.
        // The caller's sort and page bounds are still honoured: validate and order exactly as the
        // unfiltered branch does, then derive, filter and page the rows in memory.
        new FieldErrors()
            .Check(paging.Page >= 1, "page", "must be 1 or greater")
            .Check(paging.Size is >= 1 and <= PageQuery.MaxSize, "size", $"must be between 1 and {PageQuery.MaxSize}")
            .ThrowIfAny();
        var ordered = await Sorting.Apply(query, paging.Sort).ToListAsync(cancellationToken);
        var matching = (await ToDtosAsync(ordered, cancellationToken)).Where(a => a.Status == statusFilter).ToList();
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
        return await AssignToAsync(course, users, request.Deadline, reason, cancellationToken);
    }

    /// <summary>Enrols the users on the course, skipping those who already hold an open enrolment on it.</summary>
    internal async Task<AssignmentResultDto> AssignToAsync(Course course, IReadOnlyList<AppUser> users, DateOnly? deadline,
        string? reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (deadline is not null && deadline < today)
        {
            throw DomainException.Validation("The deadline is in the past.", new ErrorDetail("deadline", "must not be in the past"));
        }

        var rules = await evaluator.RulesAsync(course.CourseType, today, cancellationToken);
        var ids = users.Select(u => u.Id).ToList();
        var already = await db.Enrollments.IgnoreQueryFilters()
            .Where(e => e.CourseId == course.Id && e.State != EnrollmentState.Closed && ids.Contains(e.UserId))
            .Select(e => e.UserId).ToListAsync(cancellationToken);

        var created = new List<Enrollment>();
        foreach (var user in users.Where(u => !already.Contains(u.Id)))
        {
            var enrollment = Enrollment.Assign(course, user.Id, null, rules, now, deadline);
            db.Enrollments.Add(enrollment);
            created.Add(enrollment);
            db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.Assign,
                new { enrollment.CourseId, enrollment.UserId, enrollment.DueDate, reason });
            db.Notify(user.Id, "Course assigned",
                $"You have been assigned '{course.Title}'. Due {enrollment.DueDate:yyyy-MM-dd}."
                + (reason is null ? "" : $" Reason: {reason}"), now);
        }
        await db.SaveChangesAsync(cancellationToken);
        return new AssignmentResultDto(course.Id, [.. created.Select(e => e.Id)], already);
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
        var userIds = enrollments.Select(e => e.UserId).Distinct().ToList();
        var titles = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, cancellationToken);
        var certified = (await db.Certificates.AsNoTracking()
                .Where(c => courseIds.Contains(c.CourseId) && userIds.Contains(c.UserId)
                            && c.Status != CertificateStatus.Superseded)
                .Select(c => new { c.UserId, c.CourseId }).ToListAsync(cancellationToken))
            .Select(c => (c.UserId, c.CourseId)).ToHashSet();
        var scheduled = (await db.PracticalSchedules.AsNoTracking()
                .Where(s => s.Status == ScheduleStatus.Scheduled).Select(s => s.EnrollmentId).ToListAsync(cancellationToken))
            .ToHashSet();
        var branchIds = enrollments.Select(e => e.User.BranchId).Where(b => b is not null).Distinct().ToList();
        var codes = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return [.. enrollments.Select(e => new AssignmentDto(e.Id, e.UserId, e.User.FullName, e.User.BranchId,
            e.User.BranchId is { } b ? codes.GetValueOrDefault(b) : null, e.CourseId,
            titles.GetValueOrDefault(e.CourseId) ?? "", e.DueDate, e.ProgressPercent,
            StatusOf(e, certified.Contains((e.UserId, e.CourseId)), scheduled.Contains(e.Id), today), e.State))];
    }

    /// <summary>
    /// COMPLETED once a certificate stands; otherwise OVERDUE past the deadline; otherwise the stage of the
    /// enrolment. A passed quiz is QUIZ_PASSED until a practical is booked or has been failed, then PRACTICAL_PENDING.
    /// </summary>
    internal static AssignmentStatus StatusOf(Enrollment e, bool certified, bool practicalBooked, DateOnly today)
    {
        if (certified) return AssignmentStatus.Completed;
        if (e.State == EnrollmentState.Closed) return AssignmentStatus.Closed;
        if (e.State == EnrollmentState.Locked) return AssignmentStatus.Locked;
        if (e.DueDate is { } due && due < today) return AssignmentStatus.Overdue;

        return e.State switch
        {
            EnrollmentState.Assigned => AssignmentStatus.Assigned,
            EnrollmentState.InProgress => AssignmentStatus.InProgress,
            EnrollmentState.Eligible => AssignmentStatus.TheoryCompleted,
            _ => practicalBooked || e.PracticalEvaluations.Any(p => !p.Passed)
                ? AssignmentStatus.PracticalPending
                : AssignmentStatus.QuizPassed,
        };
    }
}
