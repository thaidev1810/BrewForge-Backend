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

/// <summary>
/// One employee's retraining duty under the action (G-19): the course/SOP version and the enrolment cycle linked
/// to it, the cycle's state, and whether the duty is met (that cycle passed after it was linked, or exempted).
/// <c>Legacy</c> marks a duty made for an action raised before duties were recorded, awaiting reconciliation.
/// </summary>
public sealed record RetrainingObligationDto(long Id, long EmployeeId, ObligationStatus Status, long? CourseId,
    long? RecipeVersionId, long? EnrollmentId, EnrollmentState? EnrollmentState, DateTimeOffset? AssignedAt,
    bool Completed, bool Legacy, string? ExemptReason, long? ExemptedBy, DateTimeOffset? ExemptedAt);

public sealed record CorrectiveActionDto(long Id, long BranchAuditId, long BranchId, string? BranchCode,
    string Description, bool RetrainingRequired, IReadOnlyList<long> EmployeeIds, CorrectiveActionStatus Status,
    DateOnly? DueDate, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, long? ClosedBy, string? ClosureNote,
    IReadOnlyList<RetrainingObligationDto> Obligations);

public sealed record CloseCorrectiveActionRequest(string? Note);

/// <summary><c>{ reason }</c>: why the employee is released from retraining (left, moved, ...). Required.</summary>
public sealed record ExemptObligationRequest(string? Reason);

/// <summary>The course to retrain on (a re-certification course, or one the staff do not yet hold) and an optional deadline.</summary>
public sealed record RetrainingRequest(long? CourseId, DateOnly? Deadline);

/// <summary>A published course tied to the audited SOP, offered as a retraining option.</summary>
public sealed record RetrainingCourseOption(long CourseId, string Title, CourseType CourseType, long RecipeVersionId,
    bool CurrentVersion);

/// <summary>What became of one named employee when retraining was assigned.</summary>
public enum RetrainingOutcome
{
    /// <summary>A fresh enrolment cycle on the retraining course was created and linked to the duty.</summary>
    Assigned,
    /// <summary>
    /// An enrolment cycle already in progress on the course (or the one already linked) is linked to the duty;
    /// a second is not created. Only a pass of it earned after the link counts.
    /// </summary>
    AlreadyEnrolled,
    /// <summary>
    /// Not assigned, for the reason in <c>ReasonCode</c> (inactive, moved branch, exempted). The duty stays open
    /// unless exempted: skipping someone never counts as their retraining.
    /// </summary>
    Skipped,
}

/// <summary>The result for one employee named on the action; <c>ObligationId</c> is their retraining duty.</summary>
public sealed record RetrainingResultItem(long EmployeeId, RetrainingOutcome Outcome, long? EnrollmentId,
    string? ReasonCode, long? ObligationId = null);

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
        var duties = await ObligationDtosAsync([.. page.Items.Select(a => a.Id)], cancellationToken);
        return new PagedResult<CorrectiveActionDto>([.. page.Items.Select(a => ToDto(a, codes, duties))], page.Page,
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

    public Task<CorrectiveActionDto> CloseAsync(long id, CloseCorrectiveActionRequest request,
        CancellationToken cancellationToken) =>
        // Locked, then read: an assignment or exemption running at the same moment waits, so the gate is
        // judged on the duties as they stand when the action closes.
        db.InTransactionAsync(async () =>
        {
            await db.LockCorrectiveActionAsync(id, cancellationToken);
            var action = await FindAsync(id, cancellationToken);
            await EnsureObligationsMetAsync(action, cancellationToken);
            action.Close(currentUser.RequireUserId(), clock.GetUtcNow(), request.Note);
            db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Close, new { request.Note });
            await db.SaveChangesAsync(cancellationToken);
            return await ToDtoAsync(action, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Gives the employees named on the action a retraining cycle on a published course of the audited SOP and
    /// links it to each one's duty. Someone who already passed the course gets a new cycle (the old one stays
    /// as history); a cycle still in progress, or the one already linked, is reused, never duplicated. An
    /// inactive or moved employee is reported as skipped and their duty stays open until exempted.
    /// </summary>
    public Task<RetrainingResultDto> AssignRetrainingAsync(long id, RetrainingRequest request,
        CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await db.LockCorrectiveActionAsync(id, cancellationToken);
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
            var now = clock.GetUtcNow();
            var duties = await DutiesOfAsync(action, now, cancellationToken);
            var named = await db.Users.IgnoreQueryFilters().Include(u => u.Role)
                .Where(u => action.EmployeeIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var byId = named.ToDictionary(u => u.Id);

            // The cycles already linked to a duty on this course: one still running, or passed since it was
            // linked, is kept as it is; asking again does not start another.
            var linkedIds = duties.Values.Where(d => d.Status == ObligationStatus.Assigned && d.CourseId == course.Id)
                .Select(d => d.EnrollmentId!.Value).ToList();
            var linked = await db.Enrollments.IgnoreQueryFilters().AsNoTracking().Where(e => linkedIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => new { e.State, e.CompletedAt }, cancellationToken);

            var skipped = new Dictionary<long, string>();
            var kept = new Dictionary<long, long>();
            var toAssign = new List<AppUser>();
            foreach (var employeeId in action.EmployeeIds)
            {
                var duty = duties[employeeId];
                if (duty.Status == ObligationStatus.Exempted) skipped[employeeId] = "EXEMPTED";
                else if (!byId.TryGetValue(employeeId, out var user)) skipped[employeeId] = "NOT_FOUND";
                else if (user.Status != UserStatus.Active) skipped[employeeId] = "INACTIVE";
                else if (user.BranchId != action.BranchId) skipped[employeeId] = "WRONG_BRANCH";
                else if (duty.Status == ObligationStatus.Assigned && duty.CourseId == course.Id
                         && linked.TryGetValue(duty.EnrollmentId!.Value, out var cycle)
                         && (cycle.State != EnrollmentState.Passed || duty.IsMetBy(true, cycle.CompletedAt))
                         && cycle.State != EnrollmentState.Closed)
                {
                    kept[employeeId] = duty.EnrollmentId!.Value;
                }
                else toAssign.Add(user);
            }

            var outcomes = toAssign.Count > 0
                ? await assignments.AssignToAsync(course, toAssign, request.Deadline,
                    $"Retraining after audit {action.BranchAuditId}", cancellationToken, retakePassed: true)
                : [];
            foreach (var outcome in outcomes)
            {
                duties[outcome.UserId].LinkCycle(course.Id, course.RecipeVersionId, outcome.EnrollmentId, now);
            }
            var byUser = outcomes.ToDictionary(o => o.UserId);

            var results = action.EmployeeIds.Select(employeeId =>
            {
                var dutyId = duties[employeeId].Id;
                if (skipped.TryGetValue(employeeId, out var reason))
                    return new RetrainingResultItem(employeeId, RetrainingOutcome.Skipped, null, reason, dutyId);
                if (kept.TryGetValue(employeeId, out var enrollmentId))
                    return new RetrainingResultItem(employeeId, RetrainingOutcome.AlreadyEnrolled, enrollmentId, null, dutyId);
                var assigned = byUser[employeeId];
                return new RetrainingResultItem(employeeId,
                    assigned.AlreadyEnrolled ? RetrainingOutcome.AlreadyEnrolled : RetrainingOutcome.Assigned,
                    assigned.EnrollmentId, null, dutyId);
            }).ToList();

            db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Assign, new
            {
                course.Id,
                results = results.Select(r => new
                {
                    r.EmployeeId, outcome = r.Outcome.ToString(), r.EnrollmentId, r.ReasonCode, r.ObligationId,
                }),
            });
            await db.SaveChangesAsync(cancellationToken);

            return new RetrainingResultDto(action.Id, course.Id, results,
                [.. results.Where(r => r.Outcome == RetrainingOutcome.Assigned).Select(r => r.EnrollmentId!.Value)],
                [.. results.Where(r => r.Outcome == RetrainingOutcome.AlreadyEnrolled).Select(r => r.EmployeeId)]);
        }, cancellationToken);

    /// <summary>
    /// Releases one named employee from the action's retraining (they left, moved, ...). Only an auditor does
    /// it, with a reason, on an open action; the exemption is recorded and final.
    /// </summary>
    public Task<CorrectiveActionDto> ExemptAsync(long id, long employeeId, ExemptObligationRequest request,
        CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await db.LockCorrectiveActionAsync(id, cancellationToken);
            var action = await FindAsync(id, cancellationToken);
            if (!action.RetrainingRequired || action.Status == CorrectiveActionStatus.Closed)
            {
                throw DomainException.RuleViolation("BR-37",
                    "An employee can be exempted only on an open action that requires retraining.");
            }
            if (!action.EmployeeIds.Contains(employeeId))
            {
                throw DomainException.NotFound("Retraining obligation", $"{id}/{employeeId}");
            }

            var now = clock.GetUtcNow();
            var duty = (await DutiesOfAsync(action, now, cancellationToken))[employeeId];
            duty.Exempt(currentUser.RequireUserId(), request.Reason, now);
            db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Exempt,
                new { employeeId, reason = duty.ExemptReason, obligationId = duty.Id });
            await db.SaveChangesAsync(cancellationToken);
            return await ToDtoAsync(action, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// G-19: an action that requires retraining closes only when every named employee's duty is met - the cycle
    /// linked to it passed at or after the link, or the employee was exempted with a reason. A pass from before
    /// the link (the training that did not hold) never counts, and someone who left or moved is not dropped
    /// silently: they are named in the refusal until exempted. An already-closed action is left to
    /// <see cref="CorrectiveAction.Close"/> to refuse (BR-37), not gated here.
    /// </summary>
    private async Task EnsureObligationsMetAsync(CorrectiveAction action, CancellationToken cancellationToken)
    {
        if (action.Status == CorrectiveActionStatus.Closed || !action.RetrainingRequired || action.EmployeeIds.Length == 0)
        {
            return;
        }

        var duties = await db.RetrainingObligations.AsNoTracking().Where(o => o.CorrectiveActionId == action.Id)
            .ToDictionaryAsync(o => o.EmployeeId, cancellationToken);
        var cycles = await CyclesOfAsync(duties.Values, cancellationToken);

        var missing = action.EmployeeIds
            .Select(employeeId => (EmployeeId: employeeId,
                Reason: UnmetReason(duties.GetValueOrDefault(employeeId), cycles)))
            .Where(x => x.Reason is not null)
            .ToList();
        if (missing.Count > 0)
        {
            throw DomainException.RuleViolation("RETRAINING_INCOMPLETE",
                "The action requires retraining: every named employee must pass the retraining assigned under it, or be exempted.",
                details: [.. missing.Select(x => new ErrorDetail("employeeIds", $"employee {x.EmployeeId}: {x.Reason}"))]);
        }
    }

    /// <summary>Why a duty is not met yet, or null when it is.</summary>
    private static string? UnmetReason(RetrainingObligation? duty,
        IReadOnlyDictionary<long, (EnrollmentState State, DateTimeOffset? CompletedAt)> cycles)
    {
        if (duty is null) return "NOT_ASSIGNED";
        switch (duty.Status)
        {
            case ObligationStatus.Exempted:
                return null;
            case ObligationStatus.Pending:
                return duty.Legacy ? "LEGACY_PENDING" : "NOT_ASSIGNED";
            default:
                var cycle = cycles.GetValueOrDefault(duty.EnrollmentId!.Value);
                return duty.IsMetBy(cycle.State == EnrollmentState.Passed, cycle.CompletedAt) ? null : "NOT_PASSED";
        }
    }

    /// <summary>The state and completion of each cycle linked to the given duties.</summary>
    private async Task<Dictionary<long, (EnrollmentState State, DateTimeOffset? CompletedAt)>> CyclesOfAsync(
        IEnumerable<RetrainingObligation> duties, CancellationToken cancellationToken)
    {
        var ids = duties.Where(d => d.EnrollmentId is not null).Select(d => d.EnrollmentId!.Value).Distinct().ToList();
        return (await db.Enrollments.IgnoreQueryFilters().AsNoTracking().Where(e => ids.Contains(e.Id))
                .Select(e => new { e.Id, e.State, e.CompletedAt }).ToListAsync(cancellationToken))
            .ToDictionary(e => e.Id, e => (e.State, e.CompletedAt));
    }

    /// <summary>
    /// The action's duties, tracked and keyed by employee. One is made for any named employee who has none yet
    /// (an action raised before duties existed and not covered by the migration), so the action can be worked on.
    /// </summary>
    private async Task<Dictionary<long, RetrainingObligation>> DutiesOfAsync(CorrectiveAction action, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var duties = await db.RetrainingObligations.Where(o => o.CorrectiveActionId == action.Id)
            .ToDictionaryAsync(o => o.EmployeeId, cancellationToken);
        var missing = action.EmployeeIds.Where(id => !duties.ContainsKey(id)).ToList();
        foreach (var employeeId in missing)
        {
            var duty = new RetrainingObligation(action.Id, employeeId, legacy: true, now);
            db.RetrainingObligations.Add(duty);
            duties[employeeId] = duty;
        }
        if (missing.Count > 0) await db.SaveChangesAsync(cancellationToken);   // ids for the results
        return duties;
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
        ToDto(action, await BranchCodesAsync([action.BranchId], cancellationToken),
            await ObligationDtosAsync([action.Id], cancellationToken));

    /// <summary>The duties of the given actions, each with its linked cycle's state and whether it is met.</summary>
    private async Task<Dictionary<long, List<RetrainingObligationDto>>> ObligationDtosAsync(IReadOnlyList<long> actionIds,
        CancellationToken cancellationToken)
    {
        var duties = await db.RetrainingObligations.AsNoTracking().Where(o => actionIds.Contains(o.CorrectiveActionId))
            .OrderBy(o => o.Id).ToListAsync(cancellationToken);
        var cycles = await CyclesOfAsync(duties, cancellationToken);
        return duties.GroupBy(o => o.CorrectiveActionId).ToDictionary(g => g.Key, g => g.Select(o =>
        {
            (EnrollmentState State, DateTimeOffset? CompletedAt)? cycle =
                o.EnrollmentId is { } e && cycles.TryGetValue(e, out var c) ? c : null;
            return new RetrainingObligationDto(o.Id, o.EmployeeId, o.Status, o.CourseId, o.RecipeVersionId,
                o.EnrollmentId, cycle?.State, o.AssignedAt,
                o.IsMetBy(cycle?.State == EnrollmentState.Passed, cycle?.CompletedAt), o.Legacy, o.ExemptReason,
                o.ExemptedBy, o.ExemptedAt);
        }).ToList());
    }

    private async Task<Dictionary<long, string>> BranchCodesAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Branches.AsNoTracking().Where(b => wanted.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
    }

    private static CorrectiveActionDto ToDto(CorrectiveAction a, Dictionary<long, string> codes,
        Dictionary<long, List<RetrainingObligationDto>> duties) =>
        new(a.Id, a.BranchAuditId, a.BranchId, codes.GetValueOrDefault(a.BranchId), a.Description, a.RetrainingRequired,
            a.EmployeeIds, a.Status, a.DueDate, a.CreatedAt, a.ClosedAt, a.ClosedBy, a.ClosureNote,
            duties.GetValueOrDefault(a.Id) ?? []);
}
