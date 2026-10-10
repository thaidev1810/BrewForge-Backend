using BrewForge.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

internal static class Recertification
{
    /// <summary>
    /// Which of the given users were ever certified on an earlier version of
    /// the recipe that version belongs to. They are the ones a
    /// recertification course on it is for: they know the drink and need
    /// only what changed. The status of that certificate does not matter;
    /// once its version is superseded it is NEEDS_RECERT anyway.
    /// </summary>
    public static async Task<HashSet<long>> CertifiedOnEarlierVersionAsync(this IBrewForgeDbContext db,
        long recipeVersionId, IReadOnlyCollection<long> userIds, CancellationToken cancellationToken)
    {
        var version = await db.RecipeVersions.AsNoTracking().Where(v => v.Id == recipeVersionId)
            .Select(v => new { v.RecipeId, v.VersionNo }).SingleAsync(cancellationToken);
        var earlier = await db.RecipeVersions.AsNoTracking()
            .Where(v => v.RecipeId == version.RecipeId && v.VersionNo < version.VersionNo)
            .Select(v => v.Id).ToListAsync(cancellationToken);

        return
        [
            .. await db.Certificates.AsNoTracking()
                .Where(c => userIds.Contains(c.UserId) && c.RecipeVersionId != null && earlier.Contains(c.RecipeVersionId.Value))
                .Select(c => c.UserId).Distinct().ToListAsync(cancellationToken),
        ];
    }
}
