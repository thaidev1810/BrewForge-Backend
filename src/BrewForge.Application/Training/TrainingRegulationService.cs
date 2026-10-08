using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>UC-27: the training regulation (SCR-29), and the training needs that follow from it.</summary>
public sealed class TrainingRegulationService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<IReadOnlyList<TrainingRegulationDto>> ListAsync(CancellationToken cancellationToken) =>
    [
        .. (await db.TrainingRegulations.AsNoTracking()
            .OrderBy(r => r.CourseType).ThenBy(r => r.EffectiveFrom).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken)).Select(TrainingRegulationDto.From),
    ];

    public async Task<TrainingRegulationDto> CreateAsync(TrainingRegulationRequest request,
        CancellationToken cancellationToken)
    {
        Require(request);
        await EnsureNotRetroactiveAsync(request.CourseType!.Value, request.EffectiveFrom!.Value, cancellationToken);

        var regulation = TrainingRegulation.Create(request.CourseType.Value, request.MandatoryForRole,
            request.PrerequisiteType, request.DueDays!.Value, request.MaxRetakes ?? TrainingRules.Default.MaxRetakes,
            request.MinAttendancePct ?? TrainingRules.Default.MinAttendancePct, request.EffectiveFrom.Value,
            currentUser.RequireUserId());

        db.TrainingRegulations.Add(regulation);
        db.Audit(AuditEntities.TrainingRegulation, () => regulation.Id, AuditActions.Create,
            TrainingRegulationDto.From(regulation));
        await db.SaveChangesAsync(cancellationToken);
        return TrainingRegulationDto.From(regulation);
    }

    /// <summary>
    /// A regulation can be changed only while no enrolment was created under
    /// it. Once one was, the rule is changed by adding a regulation that takes
    /// effect later, which leaves enrolments in flight on the rule they were
    /// created under.
    /// </summary>
    public async Task<TrainingRegulationDto> UpdateAsync(long id, TrainingRegulationRequest request,
        CancellationToken cancellationToken)
    {
        var regulation = await db.TrainingRegulations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
                         ?? throw DomainException.NotFound("Training regulation", id);
        Require(request);
        new FieldErrors()
            .Check(request.CourseType == regulation.CourseType, "courseType", "cannot be changed")
            .ThrowIfAny();

        // Both the old and the new effective date must lie after every existing enrolment.
        var earliest = request.EffectiveFrom!.Value < regulation.EffectiveFrom
            ? request.EffectiveFrom.Value
            : regulation.EffectiveFrom;
        await EnsureNotRetroactiveAsync(regulation.CourseType, earliest, cancellationToken);

        regulation.Update(request.MandatoryForRole, request.PrerequisiteType, request.DueDays!.Value,
            request.MaxRetakes ?? regulation.MaxRetakes, request.MinAttendancePct ?? regulation.MinAttendancePct,
            request.EffectiveFrom.Value);

        db.Audit(AuditEntities.TrainingRegulation, () => regulation.Id, AuditActions.Update,
            TrainingRegulationDto.From(regulation));
        await db.SaveChangesAsync(cancellationToken);
        return TrainingRegulationDto.From(regulation);
    }

    /// <summary>
    /// Who must be trained under the regulation: every active user holding a
    /// role for which a course type is mandatory, for every published course
    /// of that type they are not yet certified on.
    /// </summary>
    public async Task<IReadOnlyList<TrainingNeedDto>> TrainingNeedsAsync(long? branchId, long? courseId,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var inForce = (await db.TrainingRegulations.AsNoTracking().ToListAsync(cancellationToken))
            .Where(r => r.EffectiveFrom <= today && r.MandatoryForRole is not null)
            .GroupBy(r => r.CourseType)
            .Select(g => g.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).First())
            .ToList();
        if (inForce.Count == 0) return [];

        var types = inForce.Select(r => r.CourseType).ToList();
        var courses = await db.Courses.AsNoTracking()
            .Where(c => c.State == CourseState.Published && types.Contains(c.CourseType)
                        && (courseId == null || c.Id == courseId))
            .ToListAsync(cancellationToken);
        var roles = inForce.Select(r => r.MandatoryForRole!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Include(u => u.Role)
            .Where(u => u.Status == UserStatus.Active && roles.Contains(u.Role.RoleName)
                        && (branchId == null || u.BranchId == branchId))
            .ToListAsync(cancellationToken);

        var courseIds = courses.Select(c => c.Id).ToList();
        var userIds = users.Select(u => u.Id).ToList();
        var certified = (await db.Certificates.AsNoTracking()
                .Where(c => c.Status == CertificateStatus.Valid && courseIds.Contains(c.CourseId) && userIds.Contains(c.UserId))
                .Select(c => new { c.UserId, c.CourseId }).ToListAsync(cancellationToken))
            .Select(c => (c.UserId, c.CourseId)).ToHashSet();
        var enrolled = (await db.Enrollments.AsNoTracking()
                .Where(e => e.State != EnrollmentState.Closed && courseIds.Contains(e.CourseId) && userIds.Contains(e.UserId))
                .Select(e => new { e.UserId, e.CourseId, e.State }).ToListAsync(cancellationToken))
            .GroupBy(e => (e.UserId, e.CourseId)).ToDictionary(g => g.Key, g => g.First().State);

        var needs = new List<TrainingNeedDto>();
        foreach (var regulation in inForce)
        {
            foreach (var course in courses.Where(c => c.CourseType == regulation.CourseType))
            {
                foreach (var user in users.Where(u => u.Role.RoleName == regulation.MandatoryForRole))
                {
                    if (certified.Contains((user.Id, course.Id))) continue;
                    needs.Add(new TrainingNeedDto(user.Id, user.Username, user.FullName, user.BranchId, course.Id,
                        course.Title, course.CourseType, "REGULATION", regulation.Id, regulation.DueDays,
                        regulation.PrerequisiteType,
                        enrolled.TryGetValue((user.Id, course.Id), out var state) ? state : null));
                }
            }
        }
        return [.. needs.OrderBy(n => n.BranchId).ThenBy(n => n.Username).ThenBy(n => n.CourseTitle)];
    }

    private async Task EnsureNotRetroactiveAsync(CourseType courseType, DateOnly effectiveFrom,
        CancellationToken cancellationToken)
    {
        var latest = await db.Enrollments.IgnoreQueryFilters()
            .Join(db.Courses, e => e.CourseId, c => c.Id, (e, c) => new { e.EnrolledAt, c.CourseType })
            .Where(x => x.CourseType == courseType)
            .MaxAsync(x => (DateTimeOffset?)x.EnrolledAt, cancellationToken);

        TrainingRegulation.EnsureNotRetroactive(effectiveFrom,
            latest is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null);
    }

    private static void Require(TrainingRegulationRequest request) =>
        new FieldErrors()
            .Check(request.CourseType is not null, "courseType", "is required")
            .Check(request.DueDays is not null, "dueDays", "is required")
            .Check(request.EffectiveFrom is not null, "effectiveFrom", "is required")
            .ThrowIfAny();
}
