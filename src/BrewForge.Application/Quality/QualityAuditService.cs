using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.MasterData;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Quality;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Quality;

public sealed record AuditItemDto(long Id, string Criterion, string? Expected, string? Actual, bool Passed,
    bool Critical, string? Comment, string? EvidenceUrl);

public sealed record BranchAuditDto(long Id, long BranchId, string? BranchCode, string? BranchName,
    long RecipeVersionId, long RecipeId, string? RecipeCode, string? RecipeName, int VersionNo, long AuditorId,
    string? AuditorName, DateTimeOffset AuditedAt, int Score, AuditResult Result, string? Comment,
    IReadOnlyList<AuditItemDto> Items, long? CorrectiveActionId);

/// <summary>
/// <c>RetrainingRequired</c>, <c>EmployeeIds</c> and <c>DueDate</c> describe the
/// corrective action that a failed audit raises; they are ignored when it passes.
/// </summary>
public sealed record BranchAuditRequest(long? BranchId, long? RecipeVersionId, string? Comment,
    IReadOnlyList<AuditItemRequest>? Items, bool? RetrainingRequired, IReadOnlyList<long>? EmployeeIds,
    DateOnly? DueDate);

public sealed record AuditItemRequest(string? Criterion, string? Expected, string? Actual, bool? Passed,
    bool? Critical, string? Comment, string? EvidenceUrl);

public sealed record BranchComplianceDto(long BranchId, string? BranchCode, string? BranchName, int Audits,
    int AverageScore);

/// <summary>The auditor dashboard: the audits of a month and the branch with the lowest compliance.</summary>
public sealed record AuditSummaryDto(DateOnly From, DateOnly To, int Audits, int Passed, int Failed,
    int OpenCorrectiveActions, BranchComplianceDto? LowestCompliance, IReadOnlyList<BranchComplianceDto> Branches);

/// <summary>
/// The Quality Auditor's work: audit a branch against the released SOP, read the
/// result, and see the compliance of the chain. An audit is recorded once and
/// never changed; a failed one raises a corrective action.
/// </summary>
public sealed class QualityAuditService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock,
    SettingsService settings)
{
    private static readonly SortMap<BranchAudit> Sorting = new SortMap<BranchAudit>("auditedAt,desc", a => a.Id)
        .Add("id", a => a.Id)
        .Add("auditedAt", a => a.AuditedAt)
        .Add("score", a => a.Score)
        .Add("result", a => a.Result);

    public async Task<PagedResult<BranchAuditDto>> ListAsync(PageQuery paging, long? branchId, long? recipeVersionId,
        string? result, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var resultFilter = PagingExtensions.ParseFilter<AuditResult>(result, "result");

        var query = db.BranchAudits.AsNoTracking().Include(a => a.Items).AsQueryable();
        if (branchId is not null) query = query.Where(a => a.BranchId == branchId);
        if (recipeVersionId is not null) query = query.Where(a => a.RecipeVersionId == recipeVersionId);
        if (resultFilter is not null) query = query.Where(a => a.Result == resultFilter);
        if (from is not null) query = query.Where(a => a.AuditedAt >= StartOf(from.Value));
        if (to is not null) query = query.Where(a => a.AuditedAt < StartOf(to.Value.AddDays(1)));

        var page = await query.ToPagedAsync(paging, Sorting, a => a, cancellationToken);
        return new PagedResult<BranchAuditDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size,
            page.Total);
    }

    public async Task<BranchAuditDto> GetAsync(long id, CancellationToken cancellationToken)
    {
        var audit = await db.BranchAudits.AsNoTracking().Include(a => a.Items)
                .SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("BranchAudit", id);
        return (await ToDtosAsync([audit], cancellationToken))[0];
    }

    public async Task<BranchAuditDto> CreateAsync(BranchAuditRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(request.BranchId is not null, "branchId", "is required")
            .Check(request.RecipeVersionId is not null, "recipeVersionId", "is required")
            .Check(request.Items is { Count: > 0 }, "items", "is required")
            .Check(request.Items is null || request.Items.All(i => i.Passed is not null), "items", "every item needs 'passed'")
            .ThrowIfAny();

        var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == request.BranchId, cancellationToken)
            ?? throw DomainException.NotFound("Branch", request.BranchId!);
        var version = await db.RecipeVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == request.RecipeVersionId, cancellationToken)
            ?? throw DomainException.NotFound("RecipeVersion", request.RecipeVersionId!);
        if (version.State != VersionState.Released)
        {
            throw DomainException.RuleViolation("BR-38",
                "A branch is audited against the released version of a drink only.",
                details: new ErrorDetail("recipeVersionId", "is not the released version"));
        }

        var items = request.Items!.Select(i => new AuditItemInput(i.Criterion, i.Expected, i.Actual, i.Passed!.Value,
            i.Critical ?? false, i.Comment, i.EvidenceUrl));
        var now = clock.GetUtcNow();
        var audit = BranchAudit.Record(branch.Id, version.Id, currentUser.RequireUserId(), now, request.Comment, items,
            await settings.AuditPassMarkAsync(cancellationToken));

        var retrain = request.RetrainingRequired ?? false;
        var employees = (request.EmployeeIds ?? []).Distinct().ToList();
        if (audit.Result == AuditResult.Failed && retrain && employees.Count > 0)
        {
            var known = await db.Users.Where(u => u.BranchId == branch.Id && u.Status == UserStatus.Active
                    && employees.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken);
            var unknown = employees.Except(known).ToList();
            if (unknown.Count > 0)
            {
                throw DomainException.Validation("Employees must be active staff of the audited branch.",
                    new ErrorDetail("employeeIds", $"not active staff of branch {branch.BranchCode}: {string.Join(", ", unknown)}"));
            }
        }

        return await db.InTransactionAsync(async () =>
        {
            db.BranchAudits.Add(audit);
            db.Audit(AuditEntities.BranchAudit, () => audit.Id, AuditActions.Create,
                new { audit.BranchId, audit.RecipeVersionId, audit.Score, audit.Result });
            await db.SaveChangesAsync(cancellationToken);

            CorrectiveAction? action = null;
            if (audit.Result == AuditResult.Failed)
            {
                action = CorrectiveAction.Raise(audit, now, retrain, employees, request.DueDate);
                db.CorrectiveActions.Add(action);
                db.Audit(AuditEntities.CorrectiveAction, () => action.Id, AuditActions.Create,
                    new { action.BranchAuditId, action.RetrainingRequired, action.EmployeeIds });
                await NotifyAsync(audit, action, branch.BranchCode, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return (await ToDtosAsync([audit], cancellationToken, action?.Id))[0];
        }, cancellationToken);
    }

    /// <summary>The month asked for (the current one by default): counts, open actions, compliance per branch.</summary>
    public async Task<AuditSummaryDto> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);

        var audits = await db.BranchAudits.AsNoTracking()
            .Where(a => a.AuditedAt >= StartOf(start) && a.AuditedAt < StartOf(end.AddDays(1)))
            .Select(a => new { a.BranchId, a.Score, a.Result })
            .ToListAsync(cancellationToken);
        var open = await db.CorrectiveActions.CountAsync(a => a.Status != CorrectiveActionStatus.Closed, cancellationToken);

        var names = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, cancellationToken);
        var branches = audits.GroupBy(a => a.BranchId)
            .Select(g => new BranchComplianceDto(g.Key, names.GetValueOrDefault(g.Key)?.BranchCode,
                names.GetValueOrDefault(g.Key)?.Name, g.Count(),
                (int)Math.Round(g.Average(a => a.Score), MidpointRounding.AwayFromZero)))
            .OrderBy(b => b.AverageScore).ThenBy(b => b.BranchId)
            .ToList();

        return new AuditSummaryDto(start, end, audits.Count, audits.Count(a => a.Result == AuditResult.Passed),
            audits.Count(a => a.Result == AuditResult.Failed), open, branches.FirstOrDefault(), branches);
    }

    private async Task NotifyAsync(BranchAudit audit, CorrectiveAction action, string branchCode, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var managers = await db.Users.Where(u => u.BranchId == audit.BranchId && u.Status == UserStatus.Active
                && u.Role.RoleName == RoleName.BranchManager).Select(u => u.Id).ToListAsync(cancellationToken);
        foreach (var id in managers)
        {
            db.Notify(id, "Audit failed",
                $"Branch {branchCode} scored {audit.Score}% and needs a corrective action.", now,
                new { branchAuditId = audit.Id });
        }
        foreach (var id in action.EmployeeIds.Except(managers))
        {
            db.Notify(id, "Retraining required",
                $"An audit of branch {branchCode} requires you to be retrained.", now,
                new { branchAuditId = audit.Id });
        }
    }

    private async Task<IReadOnlyList<BranchAuditDto>> ToDtosAsync(IReadOnlyList<BranchAudit> audits,
        CancellationToken cancellationToken, long? knownActionId = null)
    {
        var branchIds = audits.Select(a => a.BranchId).Distinct().ToList();
        var versionIds = audits.Select(a => a.RecipeVersionId).Distinct().ToList();
        var auditorIds = audits.Select(a => a.AuditorId).Distinct().ToList();
        var auditIds = audits.Select(a => a.Id).ToList();

        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var versions = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.RecipeId, v.VersionNo }).ToDictionaryAsync(v => v.Id, cancellationToken);
        var recipeIds = versions.Values.Select(v => v.RecipeId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        var auditors = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => auditorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        var actions = await db.CorrectiveActions.AsNoTracking().Where(a => auditIds.Contains(a.BranchAuditId))
            .ToDictionaryAsync(a => a.BranchAuditId, a => a.Id, cancellationToken);

        return [.. audits.Select(a =>
        {
            var version = versions[a.RecipeVersionId];
            var recipe = recipes.GetValueOrDefault(version.RecipeId);
            var branch = branches.GetValueOrDefault(a.BranchId);
            long? actionId = knownActionId ?? (actions.TryGetValue(a.Id, out var id) ? id : null);
            return new BranchAuditDto(a.Id, a.BranchId, branch?.BranchCode, branch?.Name, a.RecipeVersionId,
                version.RecipeId, recipe?.RecipeCode, recipe?.Name, version.VersionNo, a.AuditorId,
                auditors.GetValueOrDefault(a.AuditorId), a.AuditedAt, a.Score, a.Result, a.Comment,
                [.. a.Items.Select(i => new AuditItemDto(i.Id, i.Criterion, i.Expected, i.Actual, i.Passed, i.Critical,
                    i.Comment, i.EvidenceUrl))],
                actionId);
        })];
    }

    private static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
