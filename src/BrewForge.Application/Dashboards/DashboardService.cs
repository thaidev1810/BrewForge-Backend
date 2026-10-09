using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
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

/// <summary>What the bound version sold at one branch whose staff completed the course.</summary>
public sealed record CourseSalesBranchDto(long BranchId, string? BranchCode, int CertifiedStaff, int Cups,
    int TradingDays, decimal CupsPerDay);

/// <summary>
/// One course: the first-attempt pass rate of the quiz as a whole and per
/// module, retakes, attendance, the time from assignment to certification and
/// the share completed on time, beside the cups sold of the version it
/// teaches at the branches whose staff completed it.
/// </summary>
public sealed record CourseEffectivenessDto(long CourseId, string CourseTitle, CourseType CourseType, CourseState State,
    long? RecipeVersionId, int Enrollments, int Passed, int Locked, int FirstAttempts, decimal? FirstAttemptPassRatePct,
    int RetakeAttempts, int LearnersWhoRetook, decimal? AttendanceRatePct, decimal? AvgDaysToCertification,
    decimal? OnTimeCompletionRatePct, IReadOnlyList<ModuleEffectiveness> Modules, int CupsSold,
    IReadOnlyList<CourseSalesBranchDto> SalesByBranch);

/// <summary>
/// UC-18: the dashboards (SCR-22). A BRANCH_MANAGER receives only the rows of
/// its own branch: the restriction is the persistence query filter, applied
/// to every query below, not a condition written here.
/// </summary>
public sealed class DashboardService(IBrewForgeDbContext db, TimeProvider clock)
{
    /// <summary>
    /// UC-31 (SCR-33): what the training results say about each course,
    /// beside what the drink then sold at the branches whose staff completed
    /// it. A module highlighted here is one that trainees fail at the first
    /// attempt markedly more often than the others.
    /// </summary>
    public async Task<IReadOnlyList<CourseEffectivenessDto>> CourseEffectivenessAsync(long? courseId,
        CancellationToken cancellationToken)
    {
        var enrollments = await db.Enrollments.AsNoTracking().Include(e => e.QuizAttempts).Include(e => e.User)
            .Where(e => courseId == null || e.CourseId == courseId)
            .ToListAsync(cancellationToken);
        var courseIds = courseId is { } only ? [only] : enrollments.Select(e => e.CourseId).Distinct().ToList();
        var courses = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).OrderBy(c => c.Title)
            .ToListAsync(cancellationToken);
        if (courseId is not null && courses.Count == 0) throw DomainException.NotFound("Course", courseId);

        var enrollmentIds = enrollments.Select(e => e.Id).ToList();
        var attendance = (await db.Attendances.AsNoTracking().Where(a => enrollmentIds.Contains(a.EnrollmentId))
                .Select(a => new { a.EnrollmentId, a.Status }).ToListAsync(cancellationToken))
            .ToLookup(a => a.EnrollmentId, a => a.Status);
        var versionIds = courses.Select(c => c.RecipeVersionId).OfType<long>().ToList();
        var sales = await db.SalesRecords.AsNoTracking().Where(s => s.RecipeVersionId != null && versionIds.Contains(s.RecipeVersionId.Value))
            .Select(s => new { VersionId = s.RecipeVersionId!.Value, s.BranchId, s.TradingDate, s.CupsSold })
            .ToListAsync(cancellationToken);
        var branchCodes = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return
        [
            .. courses.Select(course =>
            {
                var own = enrollments.Where(e => e.CourseId == course.Id).ToList();
                var attempts = own.Select(e => e.QuizAttempts.OrderBy(a => a.AttemptNo).ToList()).ToList();
                var first = attempts.Where(list => list.Count > 0).Select(list => list[0]).ToList();
                var recorded = own.SelectMany(e => attendance[e.Id]).ToList();
                var counted = recorded.Count(status => status != AttendanceStatus.Excused);

                // The branches whose staff completed the course, and what the bound version sold there.
                var completedAt = own.Where(e => e.State == EnrollmentState.Passed && e.User.BranchId is not null)
                    .GroupBy(e => e.User.BranchId!.Value).ToDictionary(g => g.Key, g => g.Count());
                var soldThere = sales.Where(s => s.VersionId == course.RecipeVersionId && completedAt.ContainsKey(s.BranchId))
                    .Select(s => new DailyCups(s.BranchId, s.TradingDate, s.CupsSold)).ToList();
                var byBranch = SalesAggregator.Group(soldThere, SalesGrouping.Branch).ToDictionary(b => b.BranchId!.Value);

                return new CourseEffectivenessDto(course.Id, course.Title, course.CourseType, course.State, course.RecipeVersionId,
                    own.Count, own.Count(e => e.State == EnrollmentState.Passed),
                    own.Count(e => e.State == EnrollmentState.Locked),
                    first.Count, CourseEffectiveness.Percent(first.Count(a => a.Passed), first.Count),
                    attempts.Sum(list => Math.Max(0, list.Count - 1)), attempts.Count(list => list.Count > 1),
                    CourseEffectiveness.Percent(recorded.Count(status => status == AttendanceStatus.Present), counted),
                    CourseEffectiveness.AverageDaysToCertification(own.Where(e => e.State == EnrollmentState.Passed)
                        .Select(e => (e.EnrolledAt, e.CompletedAt))),
                    CourseEffectiveness.OnTimeRate(own.Select(e => (e.State, e.DueDate, e.CompletedAt)), today),
                    CourseEffectiveness.PerModule(first.Select(a => a.Sheet().PerModule)),
                    soldThere.Sum(day => day.Cups),
                    [
                        .. completedAt.OrderBy(branch => branchCodes.GetValueOrDefault(branch.Key)).Select(branch =>
                        {
                            var sold = byBranch.GetValueOrDefault(branch.Key);
                            return new CourseSalesBranchDto(branch.Key, branchCodes.GetValueOrDefault(branch.Key), branch.Value,
                                sold?.Cups ?? 0, sold?.TradingDays ?? 0, sold?.CupsPerDay ?? 0);
                        }),
                    ]);
            }),
        ];
    }

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
