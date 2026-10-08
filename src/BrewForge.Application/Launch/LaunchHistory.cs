using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Launch;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

/// <summary>
/// What happened to a launch status after the drink went live there. The row
/// in <c>branch_launch_status</c> keeps only the present version and status,
/// so withdrawals and version moves are read back from their audit entries.
/// Whoever withdraws a drink or moves a branch to another version must write
/// the entry through <see cref="RecordWithdrawal"/> or <see cref="RecordVersionMove"/>.
/// </summary>
public sealed class LaunchHistory(IBrewForgeDbContext db)
{
    private static readonly IReadOnlyCollection<LaunchChange> Nothing = [];

    public async Task<IReadOnlyDictionary<long, IReadOnlyCollection<LaunchChange>>> LoadAsync(
        IReadOnlyCollection<long> launchStatusIds, CancellationToken cancellationToken)
    {
        if (launchStatusIds.Count == 0) return new Dictionary<long, IReadOnlyCollection<LaunchChange>>();

        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.BranchLaunchStatus && launchStatusIds.Contains(a.EntityId)
                        && (a.Action == AuditActions.MoveVersion || a.Action == AuditActions.Withdraw))
            .Select(a => new { a.EntityId, a.Action, a.CreatedAt, a.PayloadJson })
            .ToListAsync(cancellationToken);

        return entries.GroupBy(entry => entry.EntityId).ToDictionary(group => group.Key,
            group => (IReadOnlyCollection<LaunchChange>)
            [
                .. group.Select(entry => entry.Action == AuditActions.Withdraw
                    ? new LaunchChange(LaunchChangeKind.Withdrawn, entry.CreatedAt)
                    : new LaunchChange(LaunchChangeKind.VersionMoved, entry.CreatedAt, PreviousVersionOf(entry.PayloadJson))),
            ]);
    }

    public static IReadOnlyCollection<LaunchChange> Of(
        IReadOnlyDictionary<long, IReadOnlyCollection<LaunchChange>> history, long launchStatusId) =>
        history.GetValueOrDefault(launchStatusId, Nothing);

    public void RecordWithdrawal(BranchLaunchStatus launch) =>
        db.Audit(AuditEntities.BranchLaunchStatus, () => launch.Id, AuditActions.Withdraw,
            new { launch.BranchId, launch.RecipeId, launch.RecipeVersionId });

    public void RecordVersionMove(BranchLaunchStatus launch, long? fromVersionId) =>
        db.Audit(AuditEntities.BranchLaunchStatus, () => launch.Id, AuditActions.MoveVersion,
            new { launch.BranchId, launch.RecipeId, fromVersionId, toVersionId = launch.RecipeVersionId });

    private static long? PreviousVersionOf(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("fromVersionId", out var from) && from.ValueKind == JsonValueKind.Number
            ? from.GetInt64()
            : null;
    }
}
