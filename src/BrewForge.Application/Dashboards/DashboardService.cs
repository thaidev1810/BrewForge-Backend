using BrewForge.Application.Abstractions;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Dashboards;

/// <summary>One branch and one course: where its learners stand, and who is certified.</summary>
public sealed record TrainingProgressRowDto(long? BranchId, string? BranchCode, long CourseId, string CourseTitle,
    long? RecipeVersionId, int Assigned, int InProgress, int Eligible, int Passed, int Locked, int Overdue,
    int CertifiedValid, int NeedsRecertification);

/// <summary>
/// UC-18: the dashboards (SCR-22). A BRANCH_MANAGER receives only the rows of
/// its own branch: the restriction is the persistence query filter, applied
/// to every query below, not a condition written here.
/// </summary>
public sealed class DashboardService(IBrewForgeDbContext db, TimeProvider clock)
{
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
