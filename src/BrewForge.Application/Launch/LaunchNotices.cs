using BrewForge.Application.Abstractions;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

/// <summary>
/// Who is told what as a drink goes through its launch: the people who have to act next, so that a
/// branch is not left waiting on someone who does not know it is their turn. Recipients are read past
/// the branch scope, as the change that tells them may be made by someone of another branch.
/// </summary>
internal static class LaunchNotices
{
    public static Task<List<long>> HoldersOfAsync(IBrewForgeDbContext db, RoleName role,
        CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters()
            .Where(u => u.Role.RoleName == role && u.Status == UserStatus.Active)
            .Select(u => u.Id).ToListAsync(cancellationToken);

    public static Task<List<long>> BranchManagersOfAsync(IBrewForgeDbContext db, long branchId,
        CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters()
            .Where(u => u.Role.RoleName == RoleName.BranchManager && u.Status == UserStatus.Active
                        && u.BranchId == branchId)
            .Select(u => u.Id).ToListAsync(cancellationToken);

    /// <summary>The drink as people say it: its name, and the version when there is one.</summary>
    public static async Task<string> DrinkAsync(IBrewForgeDbContext db, long recipeId, long? recipeVersionId,
        CancellationToken cancellationToken)
    {
        var name = await db.Recipes.IgnoreQueryFilters().Where(r => r.Id == recipeId).Select(r => r.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? $"recipe {recipeId}";
        if (recipeVersionId is null) return name;
        var versionNo = await db.RecipeVersions.IgnoreQueryFilters().Where(v => v.Id == recipeVersionId)
            .Select(v => (int?)v.VersionNo).SingleOrDefaultAsync(cancellationToken);
        return versionNo is null ? name : $"{name} v{versionNo}";
    }

    public static async Task<string> BranchAsync(IBrewForgeDbContext db, long branchId,
        CancellationToken cancellationToken) =>
        await db.Branches.IgnoreQueryFilters().Where(b => b.Id == branchId).Select(b => b.BranchCode)
            .SingleOrDefaultAsync(cancellationToken) ?? $"branch {branchId}";

    /// <summary>A drink stops being sold at a branch: its managers stop entering sales for it.</summary>
    public static async Task AnnounceWithdrawalAsync(IBrewForgeDbContext db, BranchLaunchStatus launch,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var drink = await DrinkAsync(db, launch.RecipeId, launch.RecipeVersionId, cancellationToken);
        Tell(db, await BranchManagersOfAsync(db, launch.BranchId, cancellationToken), "Drink withdrawn",
            $"'{drink}' is no longer on sale at your branch from {TradingCalendar.DateOf(now):yyyy-MM-dd}. "
            + "Sales after that day cannot be entered for it.",
            now, new { branchId = launch.BranchId, launch.RecipeId, launch.RecipeVersionId });
    }

    public static void Tell(IBrewForgeDbContext db, IEnumerable<long> userIds, string subject, string message,
        DateTimeOffset now, object context)
    {
        foreach (var userId in userIds.Distinct()) db.Notify(userId, subject, message, now, context);
    }
}
