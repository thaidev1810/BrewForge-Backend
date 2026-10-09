using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

/// <summary>
/// Coverage of one drink at one branch. <c>Shortfall</c> is how many more
/// certified staff the branch needs; on a LIVE drink of origin EXISTING it is
/// a warning, not a block (BR-36).
/// </summary>
public sealed record BranchLaunchStatusDto(long Id, long BranchId, string? BranchCode, string? BranchName, long RecipeId,
    string? RecipeCode, string? RecipeName, RecipeOrigin? Origin, long? RecipeVersionId, int? VersionNo,
    LaunchStatus Status, int MinCertifiedStaff, int CertifiedCount, bool CoverageMet, int Shortfall,
    DateTimeOffset? LiveSince);

/// <summary>Where each drink stands at each branch (API contract section 10).</summary>
public sealed class LaunchStatusService(IBrewForgeDbContext db, LaunchHistory history)
{
    /// <summary>Coverage per branch and drink. A branch manager sees their own branch.</summary>
    public async Task<IReadOnlyList<BranchLaunchStatusDto>> ListAsync(long? branchId, long? recipeId, string? status,
        CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<LaunchStatus>(status, "status");
        var rows = await db.BranchLaunchStatuses.AsNoTracking()
            .Where(l => (branchId == null || l.BranchId == branchId) && (recipeId == null || l.RecipeId == recipeId)
                        && (statusFilter == null || l.Status == statusFilter))
            .ToListAsync(cancellationToken);
        return await ToDtosAsync(rows, cancellationToken);
    }

    /// <summary>
    /// LIVE to WITHDRAWN: the drink is discontinued at the branch. Not in the
    /// contract table; the state model gives the transition to the R&amp;D
    /// Manager. The day is recorded so that sales of the days before it can
    /// still be entered, and none after it (BR-24).
    /// </summary>
    public async Task<BranchLaunchStatusDto> WithdrawAsync(long id, CancellationToken cancellationToken)
    {
        var launch = await db.BranchLaunchStatuses.SingleOrDefaultAsync(l => l.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Launch status", id);
        launch.Withdraw();
        history.RecordWithdrawal(launch);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([launch], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<BranchLaunchStatusDto>> ToDtosAsync(IReadOnlyList<BranchLaunchStatus> rows,
        CancellationToken cancellationToken)
    {
        var recipeIds = rows.Select(l => l.RecipeId).Distinct().ToList();
        var versionIds = rows.Select(l => l.RecipeVersionId).OfType<long>().Distinct().ToList();
        var branches = await db.Branches.AsNoTracking()
            .ToDictionaryAsync(b => b.Id, b => new { b.BranchCode, b.Name }, cancellationToken);
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => new { r.RecipeCode, r.Name, r.Origin }, cancellationToken);
        var versionNos = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);

        return
        [
            .. rows.Select(launch =>
                {
                    var branch = branches.GetValueOrDefault(launch.BranchId);
                    var recipe = recipes.GetValueOrDefault(launch.RecipeId);
                    return new BranchLaunchStatusDto(launch.Id, launch.BranchId, branch?.BranchCode, branch?.Name,
                        launch.RecipeId, recipe?.RecipeCode, recipe?.Name, recipe?.Origin, launch.RecipeVersionId,
                        launch.RecipeVersionId is { } versionId && versionNos.TryGetValue(versionId, out var no) ? no : null,
                        launch.Status, launch.MinCertifiedStaff, launch.CertifiedCount, launch.CoverageMet,
                        Math.Max(0, launch.MinCertifiedStaff - launch.CertifiedCount), launch.LiveSince);
                })
                .OrderBy(row => row.BranchCode).ThenBy(row => row.RecipeCode),
        ];
    }
}
