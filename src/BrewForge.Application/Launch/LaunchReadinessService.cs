using BrewForge.Application.Abstractions;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

/// <summary>
/// The launch readiness checker. It recounts, for a branch and a drink, the
/// staff who hold a valid certificate on the version bound there, whenever a
/// certificate is issued, superseded or flagged. Stored changes only: call it
/// after the certificate change has been saved.
/// </summary>
public sealed class LaunchReadinessService(IBrewForgeDbContext db)
{
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
}
