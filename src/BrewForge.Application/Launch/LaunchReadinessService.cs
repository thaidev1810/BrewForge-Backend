using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

/// <summary>
/// The launch readiness checker. It recounts, for a branch and a drink, the
/// staff who hold a valid certificate on the version bound there, whenever a
/// certificate is issued, superseded or flagged, and keeps the launch status
/// and the readiness of any active pilot in step with that count. Stored
/// changes only: call it after the certificate change has been saved.
/// </summary>
public sealed class LaunchReadinessService(IBrewForgeDbContext db, LaunchHistory history, TimeProvider clock)
{
    /// <summary>The threshold of a drink that goes live without a pilot (BR-36).</summary>
    public const int DefaultMinCertifiedStaff = 2;

    /// <summary>Recounts every launch status of the user's branch that concerns the recipe of that version.</summary>
    public async Task RecomputeForCertificateAsync(long userId, long? recipeVersionId,
        CancellationToken cancellationToken)
    {
        if (recipeVersionId is null) return;
        var branchId = await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.BranchId)
            .SingleOrDefaultAsync(cancellationToken);
        if (branchId is null) return;
        var recipeId = await db.RecipeVersions.Where(v => v.Id == recipeVersionId).Select(v => (long?)v.RecipeId)
            .SingleOrDefaultAsync(cancellationToken);
        if (recipeId is null) return;

        await RecomputeAsync(branchId.Value, recipeId.Value, cancellationToken);
    }

    public async Task RecomputeAsync(long branchId, long recipeId, CancellationToken cancellationToken)
    {
        var rows = await db.BranchLaunchStatuses.IgnoreQueryFilters()
            .Where(l => l.BranchId == branchId && l.RecipeId == recipeId).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.RecomputeCoverage(await CertifiedCountAsync(branchId, row.RecipeVersionId, cancellationToken));
        }

        // A pilot keeps its own record of how far each branch is through the gate.
        var pilots = await db.PilotPrograms.IgnoreQueryFilters().Include(p => p.Branches)
            .Where(p => (p.State == PilotState.Draft || p.State == PilotState.Running)
                        && p.Branches.Any(b => b.BranchId == branchId)
                        && db.RecipeVersions.Any(v => v.Id == p.RecipeVersionId && v.RecipeId == recipeId))
            .ToListAsync(cancellationToken);
        foreach (var pilot in pilots)
        {
            pilot.RecomputeReadiness(branchId, await CertifiedCountAsync(branchId, pilot.RecipeVersionId, cancellationToken));
        }
    }

    /// <summary>How many staff of the branch hold a VALID certificate on the version.</summary>
    public async Task<int> CertifiedCountAsync(long branchId, long? recipeVersionId, CancellationToken cancellationToken)
    {
        if (recipeVersionId is null) return 0;
        return await db.Certificates
            .Where(c => c.RecipeVersionId == recipeVersionId && c.Status == CertificateStatus.Valid)
            .Join(db.Users.IgnoreQueryFilters(), c => c.UserId, u => u.Id, (c, u) => new { c.UserId, u.BranchId })
            .Where(x => x.BranchId == branchId)
            .Select(x => x.UserId).Distinct().CountAsync(cancellationToken);
    }

    /// <summary>
    /// Makes sure a drink is planned at a branch for a version: a new launch
    /// status, or the existing one planned again. A branch where the drink is
    /// LIVE is left as it is; it changes version through the gate.
    /// </summary>
    public async Task<BranchLaunchStatus> PlanAsync(long branchId, long recipeId, long recipeVersionId,
        int minCertifiedStaff, CancellationToken cancellationToken)
    {
        var launch = await db.BranchLaunchStatuses.IgnoreQueryFilters()
            .SingleOrDefaultAsync(l => l.BranchId == branchId && l.RecipeId == recipeId, cancellationToken);
        if (launch is null)
        {
            launch = BranchLaunchStatus.Plan(branchId, recipeId, recipeVersionId, minCertifiedStaff);
            db.BranchLaunchStatuses.Add(launch);
        }
        else if (!launch.IsLive)
        {
            launch.Replan(recipeVersionId, minCertifiedStaff);
        }
        else
        {
            return launch;
        }

        launch.RecomputeCoverage(await CertifiedCountAsync(branchId, recipeVersionId, cancellationToken));
        db.Audit(AuditEntities.BranchLaunchStatus, () => launch.Id, AuditActions.Plan,
            new { branchId, recipeId, recipeVersionId, minCertifiedStaff, status = launch.Status.Code() });
        return launch;
    }

    /// <summary>
    /// BR-36: a version of a drink the chain already sells is on sale at
    /// every active branch as soon as it is released, without a pilot and
    /// without the gate. Coverage is recounted and may well be false; that is
    /// shown, not blocked. A branch that sold the previous version moves to
    /// this one.
    /// </summary>
    public async Task GoLiveWithoutPilotAsync(Recipe recipe, RecipeVersion version, CancellationToken cancellationToken)
    {
        if (recipe.Origin != RecipeOrigin.Existing) return;

        var now = clock.GetUtcNow();
        var branchIds = await db.Branches.IgnoreQueryFilters().Where(b => b.Status == BranchStatus.Active)
            .Select(b => b.Id).ToListAsync(cancellationToken);
        var rows = await db.BranchLaunchStatuses.IgnoreQueryFilters().Where(l => l.RecipeId == recipe.Id)
            .ToDictionaryAsync(l => l.BranchId, cancellationToken);

        foreach (var branchId in branchIds)
        {
            await SellExistingDrinkAsync(branchId, recipe.Id, version.Id, rows.GetValueOrDefault(branchId), now,
                cancellationToken);
        }
    }

    /// <summary>
    /// BR-36 for a branch that opens, or reopens, after the drinks were
    /// released: every active drink of origin EXISTING is on sale there from
    /// that moment on its released version, exactly as it would be had the
    /// branch been open on the day of the release.
    /// </summary>
    public async Task OpenExistingDrinksAtAsync(long branchId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var drinks = await db.RecipeVersions.Where(v => v.State == VersionState.Released)
            .Join(db.Recipes.Where(r => r.Origin == RecipeOrigin.Existing && r.Status == RecipeStatus.Active),
                v => v.RecipeId, r => r.Id, (v, r) => new { RecipeId = r.Id, VersionId = v.Id })
            .ToListAsync(cancellationToken);
        var rows = await db.BranchLaunchStatuses.IgnoreQueryFilters().Where(l => l.BranchId == branchId)
            .ToDictionaryAsync(l => l.RecipeId, cancellationToken);

        foreach (var drink in drinks)
        {
            await SellExistingDrinkAsync(branchId, drink.RecipeId, drink.VersionId, rows.GetValueOrDefault(drink.RecipeId),
                now, cancellationToken);
        }
    }

    private async Task SellExistingDrinkAsync(long branchId, long recipeId, long recipeVersionId,
        BranchLaunchStatus? launch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var certified = await CertifiedCountAsync(branchId, recipeVersionId, cancellationToken);
        if (launch is null)
        {
            var created = BranchLaunchStatus.LiveForExistingRecipe(branchId, recipeId, recipeVersionId,
                DefaultMinCertifiedStaff, now);
            created.RecomputeCoverage(certified);
            db.BranchLaunchStatuses.Add(created);
            db.Audit(AuditEntities.BranchLaunchStatus, () => created.Id, AuditActions.GoLive,
                new { branchId, recipeId, recipeVersionId, rule = "BR-36", created.CoverageMet });
        }
        else if (launch.IsLive && launch.RecipeVersionId != recipeVersionId)
        {
            var from = launch.RecipeVersionId;
            launch.MoveToVersion(recipeVersionId, certified);
            history.RecordVersionMove(launch, from);
        }
        // A drink that was withdrawn at a branch stays withdrawn there.
    }
}
