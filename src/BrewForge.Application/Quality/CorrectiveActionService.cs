using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.Training;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Quality;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Quality;

public sealed record CorrectiveActionDto(long Id, long BranchAuditId, long BranchId, string? BranchCode,
    string Description, bool RetrainingRequired, IReadOnlyList<long> EmployeeIds, CorrectiveActionStatus Status,
    DateOnly? DueDate, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, long? ClosedBy, string? ClosureNote);

public sealed record CloseCorrectiveActionRequest(string? Note);

/// <summary>The course to retrain on (a re-certification course, or one the staff do not yet hold) and an optional deadline.</summary>
public sealed record RetrainingRequest(long? CourseId, DateOnly? Deadline);

/// <summary>A published course tied to the audited SOP, offered as a retraining option.</summary>
public sealed record RetrainingCourseOption(long CourseId, string Title, CourseType CourseType, long RecipeVersionId,
    bool CurrentVersion);

/// <summary>What became of one named employee when retraining was assigned.</summary>
public enum RetrainingOutcome
{
    /// <summary>A fresh enrolment on the retraining course was created.</summary>
    Assigned,
    /// <summary>The employee already holds an open enrolment on the course; a second is not created.</summary>
    AlreadyEnrolled,
    /// <summary>Left out, for the reason in <c>ReasonCode</c> (inactive, moved branch, ...).</summary>
    Skipped,
}

/// <summary>The result for one employee named on the action.</summary>
public sealed record RetrainingResultItem(long EmployeeId, RetrainingOutcome Outcome, long? EnrollmentId,
    string? ReasonCode);

/// <summary>
/// The result of assigning retraining, reported per employee so a partial outcome is never shown as a
/// whole success. <c>EnrollmentIds</c> and <c>AlreadyEnrolled</c> are kept for the old contract.
/// </summary>
public sealed record RetrainingResultDto(long CorrectiveActionId, long CourseId,
    IReadOnlyList<RetrainingResultItem> Results, IReadOnlyList<long> EnrollmentIds, IReadOnlyList<long> AlreadyEnrolled);

/// <summary>
/// What an auditor needs to assign retraining after a failed audit: the staff the action already names,
/// and the published courses tied to the SOP of that audit (its recipe version, or the recipe's current
/// released version if the audited one was superseded). Unrelated courses are not offered.
/// </summary>
public sealed record RetrainingOptionsDto(long CorrectiveActionId, long BranchAuditId, long AuditedRecipeVersionId,
    IReadOnlyList<long> EmployeeIds, IReadOnlyList<RetrainingCourseOption> Courses);

/// <summary>
/// Follows a failed audit through to its close. A store-level caller sees only
/// the actions of its own branch (persistence query filter).
/// </summary>
public sealed class CorrectiveActionService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock,
    AssignmentService assignments, CourseService courses)
{
    private static readonly SortMap<CorrectiveAction> Sorting = new SortMap<CorrectiveAction>("createdAt,desc", a => a.Id)
        .Add("id", a => a.Id)
        .Add("createdAt", a => a.CreatedAt)
        .Add("dueDate", a => a.DueDate)
        .Add("status", a => a.Status);

    public async Task<PagedResult<CorrectiveActionDto>> ListAsync(PageQuery paging, long? branchId, string? status,
        CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<CorrectiveActionStatus>(status, "status");

        var query = db.CorrectiveActions.AsNoTracking().AsQueryable();
        if (branchId is not null) query = query.Where(a => a.BranchId == branchId);
        if (statusFilter is not null) query = query.Where(a => a.Status == statusFilter);

        var page = await query.ToPagedAsync(paging, Sorting, a => a, cancellationToken);
        var codes = await BranchCodesAsync(page.Items.Select(a => a.BranchId), cancellationToken);
        return new PagedResult<CorrectiveActionDto>([.. page.Items.Select(a => ToDto(a, codes))], page.Page,
            page.Size, page.Total);
    }

    public async Task<CorrectiveActionDto> GetAsync(long id, CancellationToken cancellationToken) =>
        await ToDtoAsync(await FindAsync(id, cancellationToken), cancellationToken);

    public async Task<CorrectiveActionDto> StartAsync(long id, CancellationToken cancellationToken)
    {
        var action = await FindAsync(id, cancellationToken);
        action.Start();
        db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Start);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(action, cancellationToken);
    }

    public async Task<CorrectiveActionDto> CloseAsync(long id, CloseCorrectiveActionRequest request,
        CancellationToken cancellationToken)
    {
        var action = await FindAsync(id, cancellationToken);
        await EnsureRetrainingPassedAsync(action, cancellationToken);
        action.Close(currentUser.RequireUserId(), clock.GetUtcNow(), request.Note);
        db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Close, new { request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(action, cancellationToken);
    }

    /// <summary>
    /// Enrols the staff named on the action on a published course. Those who already hold an open
    /// enrolment on it are reported, not enrolled twice.
    /// </summary>
    public async Task<RetrainingResultDto> AssignRetrainingAsync(long id, RetrainingRequest request,
        CancellationToken cancellationToken)
    {
        var action = await FindAsync(id, cancellationToken);
        if (!action.RetrainingRequired || action.EmployeeIds.Length == 0 || action.Status == CorrectiveActionStatus.Closed)
        {
            throw DomainException.RuleViolation("BR-37",
                "Retraining can be assigned only on an open action that requires it and names employees.");
        }
        new FieldErrors().Check(request.CourseId is not null, "courseId", "is required").ThrowIfAny();

        // Enforce the same rule the GET options uses, server-side: the course must be a published one
        // tied to the audited SOP. The client cannot substitute an unrelated course, and stale options
        // (the course was archived or the SOP moved on) are refused with a clear conflict.
        var (_, _, validCourses) = await RetrainingCoursesAsync(action, cancellationToken);
        if (validCourses.All(c => c.CourseId != request.CourseId!.Value))
        {
            throw DomainException.RuleViolation("RETRAINING_COURSE_NOT_ALLOWED",
                "That course is not a retraining option for this action: it must be published and tied to the audited SOP.",
                details: new ErrorDetail("courseId",
                    $"{request.CourseId} is not among the current retraining options; reload them"));
        }

        var course = await courses.FindAsync(request.CourseId!.Value, cancellationToken);
        var named = await db.Users.IgnoreQueryFilters().Include(u => u.Role)
            .Where(u => action.EmployeeIds.Contains(u.Id)).ToListAsync(cancellationToken);
        var byId = named.ToDictionary(u => u.Id);

        // Sort each named employee into "retrain" or "skipped with a reason": an inactive one or one who
        // moved to another branch is reported, never silently dropped.
        var skipped = new Dictionary<long, string>();
        var eligible = new List<AppUser>();
        foreach (var employeeId in action.EmployeeIds)
        {
            if (!byId.TryGetValue(employeeId, out var user)) skipped[employeeId] = "NOT_FOUND";
            else if (user.Status != UserStatus.Active) skipped[employeeId] = "INACTIVE";
            else if (user.BranchId != action.BranchId) skipped[employeeId] = "WRONG_BRANCH";
            else eligible.Add(user);
        }

        var outcomes = eligible.Count > 0
            ? await assignments.AssignToAsync(course, eligible, request.Deadline,
                $"Retraining after audit {action.BranchAuditId}", cancellationToken)
            : [];
        var byUser = outcomes.ToDictionary(o => o.UserId);

        var results = action.EmployeeIds.Select(employeeId =>
            skipped.TryGetValue(employeeId, out var reason)
                ? new RetrainingResultItem(employeeId, RetrainingOutcome.Skipped, null, reason)
                : new RetrainingResultItem(employeeId,
                    byUser[employeeId].AlreadyEnrolled ? RetrainingOutcome.AlreadyEnrolled : RetrainingOutcome.Assigned,
                    byUser[employeeId].EnrollmentId, null)).ToList();

        db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Assign, new
        {
            course.Id,
            results = results.Select(r => new { r.EmployeeId, outcome = r.Outcome.ToString(), r.EnrollmentId, r.ReasonCode }),
        });
        await db.SaveChangesAsync(cancellationToken);

        return new RetrainingResultDto(action.Id, course.Id, results,
            [.. results.Where(r => r.Outcome == RetrainingOutcome.Assigned).Select(r => r.EnrollmentId!.Value)],
            [.. results.Where(r => r.Outcome == RetrainingOutcome.AlreadyEnrolled).Select(r => r.EmployeeId)]);
    }

    /// <summary>
    /// G-19: an action that required retraining is closed only once every employee it still obliges has
    /// passed it. Staff who left the branch or were deactivated — the ones assign-retraining skips — are
    /// not counted, so a departure cannot hold an action open for ever. An already-closed action is left
    /// to <see cref="CorrectiveAction.Close"/> to refuse (BR-37), not gated here.
    /// </summary>
    private async Task EnsureRetrainingPassedAsync(CorrectiveAction action, CancellationToken cancellationToken)
    {
        if (action.Status == CorrectiveActionStatus.Closed || !action.RetrainingRequired || action.EmployeeIds.Length == 0)
        {
            return;
        }

        // The employees the action still obliges: named, active, and at its branch. The rest are the ones
        // assign-retraining would skip, so they do not hold the close open.
        var obliged = await db.Users.IgnoreQueryFilters()
            .Where(u => action.EmployeeIds.Contains(u.Id) && u.Status == UserStatus.Active && u.BranchId == action.BranchId)
            .Select(u => u.Id).ToListAsync(cancellationToken);
        if (obliged.Count == 0) return;

        var (_, _, courses) = await RetrainingCoursesAsync(action, cancellationToken);
        var courseIds = courses.Select(c => c.CourseId).ToList();
        var passed = await db.Enrollments.IgnoreQueryFilters()
            .Where(e => obliged.Contains(e.UserId) && courseIds.Contains(e.CourseId) && e.State == EnrollmentState.Passed)
            .Select(e => e.UserId).Distinct().ToListAsync(cancellationToken);

        var pending = obliged.Where(userId => !passed.Contains(userId)).ToList();
        if (pending.Count > 0)
        {
            throw DomainException.RuleViolation("RETRAINING_INCOMPLETE",
                "The action requires retraining: close it only after every named employee still at the branch has passed it.",
                details: [.. pending.Select(userId => new ErrorDetail("employeeIds",
                    $"employee {userId} has not passed the retraining"))]);
        }
    }

    /// <summary>
    /// The courses an auditor may assign for retraining on this action: those published and bound to the
    /// SOP of the audit (its recipe version, or the recipe's current released version if that one was
    /// superseded, per G-19), plus the staff the action already names. Nothing unrelated is offered, and
    /// the staff come from the action, not from a certified-staff history.
    /// </summary>
    public async Task<RetrainingOptionsDto> RetrainingOptionsAsync(long id, CancellationToken cancellationToken)
    {
        var action = await FindAsync(id, cancellationToken);
        var (auditedVersionId, _, courses) = await RetrainingCoursesAsync(action, cancellationToken);
        return new RetrainingOptionsDto(action.Id, action.BranchAuditId, auditedVersionId, action.EmployeeIds, courses);
    }

    /// <summary>
    /// The one rule for which courses an action may retrain on, used by both the GET options and the POST
    /// assignment so they can never diverge: published courses bound to the audited SOP version, or the
    /// recipe's current released version if the audited one was superseded (G-19).
    /// </summary>
    private async Task<(long AuditedVersionId, long? CurrentVersionId, List<RetrainingCourseOption> Courses)>
        RetrainingCoursesAsync(CorrectiveAction action, CancellationToken cancellationToken)
    {
        var auditedVersionId = await db.BranchAudits.AsNoTracking().Where(a => a.Id == action.BranchAuditId)
            .Select(a => a.RecipeVersionId).SingleAsync(cancellationToken);
        var recipeId = await db.RecipeVersions.AsNoTracking().Where(v => v.Id == auditedVersionId)
            .Select(v => v.RecipeId).SingleAsync(cancellationToken);
        var currentVersionId = await db.RecipeVersions.AsNoTracking()
            .Where(v => v.RecipeId == recipeId && v.State == VersionState.Released)
            .Select(v => (long?)v.Id).SingleOrDefaultAsync(cancellationToken);

        var versionIds = new List<long> { auditedVersionId };
        if (currentVersionId is { } current && current != auditedVersionId) versionIds.Add(current);

        var courses = await db.Courses.AsNoTracking()
            .Where(c => c.State == CourseState.Published && c.RecipeVersionId != null
                        && versionIds.Contains(c.RecipeVersionId.Value))
            .OrderBy(c => c.Id)
            .Select(c => new RetrainingCourseOption(c.Id, c.Title, c.CourseType, c.RecipeVersionId!.Value,
                c.RecipeVersionId == currentVersionId))
            .ToListAsync(cancellationToken);

        return (auditedVersionId, currentVersionId, courses);
    }

    private async Task<CorrectiveAction> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.CorrectiveActions.SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("CorrectiveAction", id);

    private async Task<CorrectiveActionDto> ToDtoAsync(CorrectiveAction action, CancellationToken cancellationToken) =>
        ToDto(action, await BranchCodesAsync([action.BranchId], cancellationToken));

    private async Task<Dictionary<long, string>> BranchCodesAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Branches.AsNoTracking().Where(b => wanted.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
    }

    private static CorrectiveActionDto ToDto(CorrectiveAction a, Dictionary<long, string> codes) =>
        new(a.Id, a.BranchAuditId, a.BranchId, codes.GetValueOrDefault(a.BranchId), a.Description, a.RetrainingRequired,
            a.EmployeeIds, a.Status, a.DueDate, a.CreatedAt, a.ClosedAt, a.ClosedBy, a.ClosureNote);
}
