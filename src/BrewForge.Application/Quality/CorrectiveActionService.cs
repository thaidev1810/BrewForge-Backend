using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.Training;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Quality;
using BrewForge.Domain.Recipes;
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
        action.Close(currentUser.RequireUserId(), clock.GetUtcNow(), request.Note);
        db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Close, new { request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(action, cancellationToken);
    }

    /// <summary>
    /// Enrols the staff named on the action on a published course. Those who already hold an open
    /// enrolment on it are reported, not enrolled twice.
    /// </summary>
    public async Task<AssignmentResultDto> AssignRetrainingAsync(long id, RetrainingRequest request,
        CancellationToken cancellationToken)
    {
        var action = await FindAsync(id, cancellationToken);
        if (!action.RetrainingRequired || action.EmployeeIds.Length == 0 || action.Status == CorrectiveActionStatus.Closed)
        {
            throw DomainException.RuleViolation("BR-37",
                "Retraining can be assigned only on an open action that requires it and names employees.");
        }
        new FieldErrors().Check(request.CourseId is not null, "courseId", "is required").ThrowIfAny();

        var course = await courses.FindAsync(request.CourseId!.Value, cancellationToken);
        var ids = action.EmployeeIds;
        var users = await db.Users.Include(u => u.Role)
            .Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active).ToListAsync(cancellationToken);

        var result = await assignments.AssignToAsync(course, users, request.Deadline,
            $"Retraining after audit {action.BranchAuditId}", cancellationToken);
        db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Assign,
            new { course.Id, enrolled = result.EnrollmentIds, skipped = result.AlreadyEnrolled });
        await db.SaveChangesAsync(cancellationToken);
        return result;
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

        return new RetrainingOptionsDto(action.Id, action.BranchAuditId, auditedVersionId, action.EmployeeIds, courses);
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
