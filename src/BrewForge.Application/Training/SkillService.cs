using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

public enum SkillStatus
{
    Certified,
    NeedsRecert,
    Training,
    NotCertified,
}

public sealed record SkillDto(long CourseId, string CourseTitle, long? RecipeVersionId, SkillStatus Status,
    long? CertificateId, DateTimeOffset? IssuedAt);

public sealed record EmployeeSkillSummaryDto(long UserId, string FullName, long? BranchId, string? BranchCode,
    int CertifiedSkills, int RequiredSkills, int InTraining, int Overdue, string Status);

/// <summary>
/// The Skill Passport: for each published product course, whether a person is certified, being trained,
/// or not yet certified. The branch view lists the same for every employee of a branch.
/// </summary>
public sealed class SkillService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<IReadOnlyList<SkillDto>> MySkillsAsync(CancellationToken cancellationToken) =>
        (await SkillsAsync([currentUser.RequireUserId()], cancellationToken))[currentUser.RequireUserId()];

    /// <summary>One row per active trainee of the branch (all branches for head office), with a summary of their skills.</summary>
    public async Task<IReadOnlyList<EmployeeSkillSummaryDto>> EmployeesAsync(long? branchId,
        CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking().Include(u => u.Role)
            .Where(u => u.Role.RoleName == RoleName.Trainee && u.Status == UserStatus.Active);
        if (branchId is not null) query = query.Where(u => u.BranchId == branchId);
        var users = await query.OrderBy(u => u.FullName).ToListAsync(cancellationToken);

        var skills = await SkillsAsync(users.Select(u => u.Id).ToList(), cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var overdue = (await db.Enrollments.AsNoTracking()
                .Where(e => e.DueDate < today && e.State != EnrollmentState.Closed && e.State != EnrollmentState.Passed)
                .Select(e => e.UserId).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var branchIds = users.Select(u => u.BranchId).Where(b => b is not null).Distinct().ToList();
        var codes = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);

        return [.. users.Select(u =>
        {
            var mine = skills[u.Id];
            var certified = mine.Count(s => s.Status == SkillStatus.Certified);
            var training = mine.Count(s => s.Status == SkillStatus.Training);
            return new EmployeeSkillSummaryDto(u.Id, u.FullName, u.BranchId,
                u.BranchId is { } b ? codes.GetValueOrDefault(b) : null, certified, mine.Count, training,
                overdue.GetValueOrDefault(u.Id), certified == mine.Count ? "READY" : training > 0 ? "TRAINING" : "NOT_READY");
        })];
    }

    private async Task<Dictionary<long, List<SkillDto>>> SkillsAsync(IReadOnlyList<long> userIds,
        CancellationToken cancellationToken)
    {
        var courses = await db.Courses.AsNoTracking()
            .Where(c => c.CourseType == CourseType.Product && c.State == CourseState.Published)
            .OrderBy(c => c.Title).Select(c => new { c.Id, c.Title, c.RecipeVersionId }).ToListAsync(cancellationToken);
        var certificates = await db.Certificates.AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && c.Status != CertificateStatus.Superseded)
            .ToListAsync(cancellationToken);
        var open = (await db.Enrollments.IgnoreQueryFilters().AsNoTracking()
                .Where(e => userIds.Contains(e.UserId) && e.State != EnrollmentState.Closed)
                .Select(e => new { e.UserId, e.CourseId }).ToListAsync(cancellationToken))
            .Select(e => (e.UserId, e.CourseId)).ToHashSet();

        return userIds.ToDictionary(id => id, id => courses.Select(c =>
        {
            var cert = certificates.FirstOrDefault(x => x.UserId == id && x.CourseId == c.Id);
            var status = cert is null
                ? open.Contains((id, c.Id)) ? SkillStatus.Training : SkillStatus.NotCertified
                : cert.Status == CertificateStatus.NeedsRecert ? SkillStatus.NeedsRecert : SkillStatus.Certified;
            return new SkillDto(c.Id, c.Title, c.RecipeVersionId, status, cert?.Id, cert?.IssuedAt);
        }).ToList());
    }
}
