using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Sales;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Dashboards;

/// <summary>One branch and one course: where its learners stand, and who is certified.</summary>
public sealed record TrainingProgressRowDto(long? BranchId, string? BranchCode, long CourseId, string CourseTitle,
    long? RecipeVersionId, int Assigned, int InProgress, int Eligible, int Passed, int Locked, int Overdue,
    int CertifiedValid, int NeedsRecertification);

/// <summary>
/// One branch and one drink: what the branch sold in the period, beside
/// whether enough of its staff are certified on the version it sells.
/// </summary>
public sealed record BranchPerformanceRowDto(long BranchId, string? BranchCode, string? BranchName, long RecipeId,
    string? RecipeCode, string? RecipeName, long? RecipeVersionId, int? VersionNo, LaunchStatus Status,
    DateTimeOffset? LiveSince, int MinCertifiedStaff, int CertifiedCount, bool CoverageMet, int CupsTotal,
    int TradingDays, decimal CupsPerDay, DateOnly? LastTradingDate);

public sealed record BranchPerformanceDto(DateOnly From, DateOnly To, IReadOnlyList<BranchPerformanceRowDto> Rows);

/// <summary>
/// UC-18: the dashboards (SCR-22). A BRANCH_MANAGER receives only the rows of
/// its own branch: the restriction is the persistence query filter, applied
/// to every query below, not a condition written here.
/// </summary>
public sealed class DashboardService(IBrewForgeDbContext db, TimeProvider clock)
{
    /// <summary>The period shown when none is asked for: the four weeks up to today.</summary>
    public const int DefaultPeriodDays = 28;

    /// <summary>
    /// Sales beside certificate coverage, for every drink a branch has a
    /// launch status for. A drink that sells well at a branch whose coverage
    /// is not met is the row this screen exists to show.
    /// </summary>
    public async Task<BranchPerformanceDto> BranchPerformanceAsync(long? branchId, long? recipeId, DateOnly? from,
        DateOnly? to, CancellationToken cancellationToken)
    {
        var last = to ?? TradingCalendar.DateOf(clock.GetUtcNow());
        var first = from ?? last.AddDays(1 - DefaultPeriodDays);
        new FieldErrors().Check(first <= last, "from", "must not be after 'to'").ThrowIfAny();

        var launches = await db.BranchLaunchStatuses.AsNoTracking()
            .Where(l => (branchId == null || l.BranchId == branchId) && (recipeId == null || l.RecipeId == recipeId))
            .ToListAsync(cancellationToken);
        var sales = await db.SalesRecords.AsNoTracking()
            .Where(s => (branchId == null || s.BranchId == branchId) && (recipeId == null || s.RecipeId == recipeId)
                        && s.TradingDate >= first && s.TradingDate <= last)
            .GroupBy(s => new { s.BranchId, s.RecipeId })
            .Select(g => new
            {
                g.Key.BranchId, g.Key.RecipeId, Cups = g.Sum(s => s.CupsSold), Days = g.Count(),
                Last = g.Max(s => s.TradingDate),
            })
            .ToDictionaryAsync(g => (g.BranchId, g.RecipeId), cancellationToken);

        var recipeIds = launches.Select(l => l.RecipeId).Distinct().ToList();
        var versionIds = launches.Select(l => l.RecipeVersionId).OfType<long>().Distinct().ToList();
        var branches = await db.Branches.AsNoTracking()
            .ToDictionaryAsync(b => b.Id, b => new { b.BranchCode, b.Name }, cancellationToken);
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => new { r.RecipeCode, r.Name }, cancellationToken);
        var versionNos = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);

        return new BranchPerformanceDto(first, last,
        [
            .. launches.Select(launch =>
                {
                    var branch = branches.GetValueOrDefault(launch.BranchId);
                    var recipe = recipes.GetValueOrDefault(launch.RecipeId);
                    var sold = sales.GetValueOrDefault((launch.BranchId, launch.RecipeId));
                    return new BranchPerformanceRowDto(launch.BranchId, branch?.BranchCode, branch?.Name, launch.RecipeId,
                        recipe?.RecipeCode, recipe?.Name, launch.RecipeVersionId,
                        launch.RecipeVersionId is { } versionId && versionNos.TryGetValue(versionId, out var no) ? no : null,
                        launch.Status, launch.LiveSince, launch.MinCertifiedStaff, launch.CertifiedCount, launch.CoverageMet,
                        sold?.Cups ?? 0, sold?.Days ?? 0, SalesAggregator.Average(sold?.Cups ?? 0, sold?.Days ?? 0),
                        sold?.Last);
                })
                .OrderBy(row => row.BranchCode).ThenBy(row => row.RecipeCode),
        ]);
    }

    public async Task<IReadOnlyList<TrainingProgressRowDto>> TrainingProgressAsync(long? branchId, long? courseId,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var enrollments = await db.Enrollments.AsNoTracking()
            .Where(e => (courseId == null || e.CourseId == courseId) && (branchId == null || e.User.BranchId == branchId))
            .Select(e => new { e.CourseId, e.State, e.DueDate, e.User.BranchId })
            .ToListAsync(cancellationToken);
        var certificates = await db.Certificates.AsNoTracking()
            .Where(c => courseId == null || c.CourseId == courseId)
            .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new { c.CourseId, c.Status, u.BranchId })
            .Where(x => branchId == null || x.BranchId == branchId)
            .ToListAsync(cancellationToken);

        var keys = enrollments.Select(e => (e.BranchId, e.CourseId))
            .Concat(certificates.Select(c => (c.BranchId, c.CourseId))).Distinct().ToList();
        var courseIds = keys.Select(k => k.CourseId).Distinct().ToList();
        var courses = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.RecipeVersionId }).ToDictionaryAsync(c => c.Id, cancellationToken);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);

        return
        [
            .. keys.Select(key =>
                {
                    var learners = enrollments.Where(e => e.BranchId == key.BranchId && e.CourseId == key.CourseId).ToList();
                    var held = certificates.Where(c => c.BranchId == key.BranchId && c.CourseId == key.CourseId).ToList();
                    var course = courses[key.CourseId];
                    int In(EnrollmentState state) => learners.Count(e => e.State == state);
                    return new TrainingProgressRowDto(key.BranchId,
                        key.BranchId is { } id ? branches.GetValueOrDefault(id) : null, key.CourseId, course.Title,
                        course.RecipeVersionId, In(EnrollmentState.Assigned), In(EnrollmentState.InProgress),
                        In(EnrollmentState.Eligible), In(EnrollmentState.Passed), In(EnrollmentState.Locked),
                        learners.Count(e => e.DueDate < today && e.State is EnrollmentState.Assigned
                            or EnrollmentState.InProgress or EnrollmentState.Eligible),
                        held.Count(c => c.Status == CertificateStatus.Valid),
                        held.Count(c => c.Status == CertificateStatus.NeedsRecert));
                })
                .OrderBy(row => row.BranchCode).ThenBy(row => row.CourseTitle),
        ];
    }
}
