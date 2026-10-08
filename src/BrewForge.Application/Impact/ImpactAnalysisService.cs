using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Launch;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Impact;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Impact;

/// <summary><c>{ entityType, entityId }</c>: Ingredient, StandardEquipment or RecipeVersion.</summary>
public sealed record ImpactRequest(string? EntityType, long? EntityId);

public sealed record ImpactTriggerDto(string EntityType, long EntityId);

public sealed record AffectedCourseDto(long Id, ImpactType Impact, string? Title, long? RecipeVersionId, CourseState? State);

public sealed record AffectedCertificateDto(long Id, ImpactType Impact, long? UserId, string? FullName, long? BranchId,
    long? RecipeVersionId, CertificateStatus? Status);

public sealed record AffectedBranchDto(long BranchId, long RecipeId, ImpactType Impact, string? BranchCode,
    long? RecipeVersionId);

public sealed record AffectedDto(IReadOnlyList<long> RecipeVersions, IReadOnlyList<AffectedCourseDto> Courses,
    IReadOnlyList<AffectedCertificateDto> Certificates, IReadOnlyList<AffectedBranchDto> LiveBranches);

/// <summary>MSG-W03: what committing would cost, said before it is done.</summary>
public sealed record ImpactWarningDto(string Code, string Message);

/// <summary>The impact analysis of the API contract.</summary>
public sealed record ImpactAnalysisDto(string RunId, ImpactTriggerDto Trigger, AffectedDto Affected, bool Committed,
    ImpactWarningDto? Warning);

/// <summary>
/// UC-19: change propagation over the reverse dependency graph. A what-if
/// writes its <c>change_impact</c> rows and nothing else, not even an audit
/// entry; committing applies the flags and deletes nothing (BR-15).
/// </summary>
public sealed class ImpactAnalysisService(IBrewForgeDbContext db, LaunchReadinessService readiness, TimeProvider clock)
{
    public const string WarningCode = "MSG-W03";

    /// <summary>What-if. Computes the affected set and stores it uncommitted.</summary>
    public async Task<ImpactAnalysisDto> AnalyzeAsync(ImpactRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(ImpactTrigger.TryParseKind(request.EntityType, out _), "entityType",
                $"must be one of: {string.Join(", ", Enum.GetNames<TriggerKind>())}")
            .Check(request.EntityId is not null, "entityId", "is required")
            .ThrowIfAny();
        ImpactTrigger.TryParseKind(request.EntityType, out var kind);

        var (trigger, affected) = await ComputeAsync(kind, request.EntityId!.Value, tracking: false, cancellationToken);
        var run = ImpactRun.WhatIf(Guid.NewGuid(), trigger, affected, clock.GetUtcNow());

        db.ChangeImpacts.AddRange(run.NewRows);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(run.RunId, trigger, affected, committed: false, cancellationToken);
    }

    /// <summary>A run as it was computed: the entities its rows name, whatever has happened to them since.</summary>
    public async Task<ImpactAnalysisDto> GetAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(runId, tracking: false, cancellationToken);
        var trigger = await TriggerOfAsync(rows[0], cancellationToken);
        var versions = DependencyGraph.VersionsDependingOn(trigger, await CandidateVersionsAsync(trigger, cancellationToken));

        IEnumerable<long> Ids(string entity) => rows.Where(row => row.AffectedEntity == entity).Select(row => row.AffectedId);
        var courseIds = Ids(ChangeImpact.CourseEntity).ToList();
        var certificateIds = Ids(ChangeImpact.CertificateEntity).ToList();
        var launchIds = Ids(ChangeImpact.LaunchEntity).ToList();
        var affected = new ImpactSet([.. versions.Select(v => v.Id)],
            await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).OrderBy(c => c.Id).ToListAsync(cancellationToken),
            await db.Certificates.AsNoTracking().Where(c => certificateIds.Contains(c.Id)).OrderBy(c => c.Id).ToListAsync(cancellationToken),
            await db.BranchLaunchStatuses.AsNoTracking().Where(l => launchIds.Contains(l.Id)).OrderBy(l => l.Id).ToListAsync(cancellationToken));

        return await ToDtoAsync(rows[0].AnalysisRunId, trigger, affected, rows.All(row => row.Committed), cancellationToken);
    }

    /// <summary>
    /// Applies the flags of a run (BR-15). The affected set is computed again
    /// at this moment, so that a certificate issued since the what-if is not
    /// left valid on a procedure that no longer stands.
    /// </summary>
    public async Task<ImpactAnalysisDto> CommitAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(runId, tracking: true, cancellationToken);
        var stored = await TriggerOfAsync(rows[0], cancellationToken);
        var (trigger, affected) = await ComputeAsync(stored.Kind, stored.EntityId, tracking: true, cancellationToken);

        var run = ImpactRun.Resume(rows[0].AnalysisRunId, trigger, affected, rows, clock.GetUtcNow());
        run.Commit(rows);
        await ApplyAsync(run, cancellationToken);
        return await ToDtoAsync(run.RunId, trigger, affected, committed: true, cancellationToken);
    }

    /// <summary>
    /// The propagation the system makes by itself when a release supersedes a
    /// version: its published courses become OUT_OF_DATE and the certificates
    /// bound to it NEEDS_RECERT, at once and committed.
    /// </summary>
    public async Task PropagateSupersededAsync(long supersededVersionId, CancellationToken cancellationToken)
    {
        var (trigger, affected) = await ComputeAsync(TriggerKind.RecipeVersion, supersededVersionId, tracking: true,
            cancellationToken);
        if (affected.IsEmpty) return;

        var run = ImpactRun.WhatIf(Guid.NewGuid(), trigger, affected, clock.GetUtcNow());
        run.Commit([]);
        await ApplyAsync(run, cancellationToken);
    }

    /// <summary>Stores a committed run with everything that follows from it: the trail, the notices, the recount.</summary>
    private async Task ApplyAsync(ImpactRun run, CancellationToken cancellationToken)
    {
        db.ChangeImpacts.AddRange(run.NewRows);
        var (trigger, affected) = (run.Trigger, run.Affected);
        var runId = run.RunId.ToString("N");

        foreach (var course in affected.Courses)
        {
            db.Audit(AuditEntities.Course, () => course.Id, AuditActions.MarkOutOfDate,
                new { runId, trigger = trigger.EntityType, triggerId = trigger.EntityId, course.RecipeVersionId });
            // The trainer who built the course is the one who rebuilds it.
            db.Audit(AuditEntities.User, () => course.CreatedBy, AuditActions.Notify, new
            {
                subject = "Course out of date",
                message = $"'{course.Title}' is out of date: the recipe version it was built on changed. Rebuild it on the current version.",
                courseId = course.Id,
            });
        }
        foreach (var certificate in affected.Certificates)
        {
            db.Audit(AuditEntities.Certificate, () => certificate.Id, AuditActions.FlagRecertification,
                new { runId, trigger = trigger.EntityType, triggerId = trigger.EntityId, certificate.UserId, certificate.RecipeVersionId });
            db.Audit(AuditEntities.User, () => certificate.UserId, AuditActions.Notify, new
            {
                subject = "Re-training required",
                message = "The recipe you are certified on has changed. Your certificate is kept, and you need to be trained again.",
                certificateId = certificate.Id, courseId = certificate.CourseId,
            });
        }
        db.Audit(trigger.EntityType, () => trigger.EntityId, AuditActions.CommitImpact, new
        {
            runId, recipeVersions = affected.RecipeVersionIds, courses = affected.Courses.Count,
            certificates = affected.Certificates.Count, liveBranches = affected.LiveBranches.Count,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Fewer valid certificates means fewer certified staff: the launch gate counts again.
        var holders = affected.Certificates.Select(c => c.UserId).Distinct().ToList();
        var branchOf = await db.Users.IgnoreQueryFilters().Where(u => holders.Contains(u.Id) && u.BranchId != null)
            .ToDictionaryAsync(u => u.Id, u => u.BranchId!.Value, cancellationToken);
        var recipeOf = await db.RecipeVersions.Where(v => affected.RecipeVersionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.RecipeId, cancellationToken);
        var recount = affected.Certificates
            .Where(c => branchOf.ContainsKey(c.UserId) && c.RecipeVersionId is { } versionId && recipeOf.ContainsKey(versionId))
            .Select(c => (BranchId: branchOf[c.UserId], RecipeId: recipeOf[c.RecipeVersionId!.Value])).Distinct();
        foreach (var (branchId, recipeId) in recount)
        {
            await readiness.RecomputeAsync(branchId, recipeId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The trigger, the versions that depend on it, and what depends on those.</summary>
    private async Task<(ImpactTrigger Trigger, ImpactSet Affected)> ComputeAsync(TriggerKind kind, long entityId,
        bool tracking, CancellationToken cancellationToken)
    {
        var trigger = await ResolveTriggerAsync(kind, entityId, cancellationToken);
        var versionIds = DependencyGraph.VersionsDependingOn(trigger, await CandidateVersionsAsync(trigger, cancellationToken))
            .Select(version => version.Id).ToList();

        var courses = db.Courses.Where(c => c.RecipeVersionId != null && versionIds.Contains(c.RecipeVersionId.Value));
        var certificates = db.Certificates.Where(c => c.RecipeVersionId != null && versionIds.Contains(c.RecipeVersionId.Value));
        var launches = db.BranchLaunchStatuses.IgnoreQueryFilters()
            .Where(l => l.RecipeVersionId != null && versionIds.Contains(l.RecipeVersionId.Value));
        if (!tracking) (courses, certificates, launches) = (courses.AsNoTracking(), certificates.AsNoTracking(), launches.AsNoTracking());

        return (trigger, DependencyGraph.Downstream(versionIds, await courses.ToListAsync(cancellationToken),
            await certificates.ToListAsync(cancellationToken), await launches.ToListAsync(cancellationToken)));
    }

    /// <summary>The versions the database can already say refer to the trigger; the graph has the last word.</summary>
    private async Task<List<RecipeVersion>> CandidateVersionsAsync(ImpactTrigger trigger, CancellationToken cancellationToken)
    {
        var versions = db.RecipeVersions.AsNoTracking().Include(v => v.Steps).ThenInclude(s => s.Ingredients)
            .Where(v => v.State == VersionState.Released || v.State == VersionState.Superseded);
        versions = trigger.Kind switch
        {
            TriggerKind.Ingredient => versions.Where(v => v.Steps.Any(s => s.Ingredients.Any(i => i.IngredientId == trigger.EntityId))),
            TriggerKind.StandardEquipment => versions.Where(v => v.Steps.Any(s => s.EquipmentClass == trigger.EquipmentClass)),
            _ => versions.Where(v => v.Id == trigger.EntityId),
        };
        return await versions.ToListAsync(cancellationToken);
    }

    private async Task<ImpactTrigger> ResolveTriggerAsync(TriggerKind kind, long entityId, CancellationToken cancellationToken)
    {
        if (kind == TriggerKind.StandardEquipment)
        {
            var equipmentClass = await db.StandardEquipment.Where(e => e.Id == entityId).Select(e => e.EquipmentClass)
                .SingleOrDefaultAsync(cancellationToken);
            return equipmentClass is null
                ? throw DomainException.NotFound(kind.ToString(), entityId)
                : new ImpactTrigger(kind, entityId, equipmentClass);
        }

        var exists = kind == TriggerKind.Ingredient
            ? await db.Ingredients.AnyAsync(i => i.Id == entityId, cancellationToken)
            : await db.RecipeVersions.AnyAsync(v => v.Id == entityId, cancellationToken);
        return exists ? new ImpactTrigger(kind, entityId) : throw DomainException.NotFound(kind.ToString(), entityId);
    }

    private async Task<ImpactTrigger> TriggerOfAsync(ChangeImpact row, CancellationToken cancellationToken)
    {
        ImpactTrigger.TryParseKind(row.TriggerEntity, out var kind);
        return await ResolveTriggerAsync(kind, row.TriggerId, cancellationToken);
    }

    private async Task<List<ChangeImpact>> RowsAsync(string runId, bool tracking, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runId, out var id)) throw DomainException.NotFound("Impact analysis", runId);
        var query = db.ChangeImpacts.Where(row => row.AnalysisRunId == id);
        if (!tracking) query = query.AsNoTracking();

        var rows = await query.OrderBy(row => row.Id).ToListAsync(cancellationToken);
        return rows.Count > 0 ? rows : throw DomainException.NotFound("Impact analysis", runId);
    }

    private async Task<ImpactAnalysisDto> ToDtoAsync(Guid runId, ImpactTrigger trigger, ImpactSet affected, bool committed,
        CancellationToken cancellationToken)
    {
        var userIds = affected.Certificates.Select(c => c.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.FullName, u.BranchId }, cancellationToken);
        var branchIds = affected.LiveBranches.Select(l => l.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().IgnoreQueryFilters().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);

        var dto = new AffectedDto(affected.RecipeVersionIds,
            [.. affected.Courses.Select(c => new AffectedCourseDto(c.Id, ImpactType.OutOfDate, c.Title, c.RecipeVersionId, c.State))],
            [
                .. affected.Certificates.Select(c =>
                {
                    var user = users.GetValueOrDefault(c.UserId);
                    return new AffectedCertificateDto(c.Id, ImpactType.NeedsRecert, c.UserId, user?.FullName, user?.BranchId,
                        c.RecipeVersionId, c.Status);
                }),
            ],
            [
                .. affected.LiveBranches.Select(l => new AffectedBranchDto(l.BranchId, l.RecipeId, ImpactType.BranchAffected,
                    branches.GetValueOrDefault(l.BranchId), l.RecipeVersionId)),
            ]);

        var warning = committed || affected.IsEmpty
            ? null
            : new ImpactWarningDto(WarningCode,
                $"This change will affect {affected.Courses.Count} courses and {affected.Certificates.Count} certificates. " +
                "Review the impact analysis before committing.");
        return new ImpactAnalysisDto(runId.ToString("N"), new ImpactTriggerDto(trigger.EntityType, trigger.EntityId), dto,
            committed, warning);
    }
}
