using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
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

    public const string RegulationReason = "REGULATION";
    public const string ShortfallReason = "BRANCH_SHORTFALL";
    public const string RecertificationReason = "RECERTIFICATION";

    /// <summary>
    /// Who must be trained, from two sources combined. The regulation: every
    /// active user holding a role for which a course type is mandatory, for
    /// every published course of that type they are not yet certified on. And
    /// the operational need: the staff of a branch that is short of certified
    /// people on a drink it is preparing for or already sells. Someone both
    /// apply to is listed once, for the operational need.
    /// </summary>
    public async Task<IReadOnlyList<TrainingNeedDto>> TrainingNeedsAsync(long? branchId, long? courseId,
        CancellationToken cancellationToken)
    {
        var byRegulation = await RegulationNeedsAsync(branchId, courseId, cancellationToken);
        var byShortfall = await ShortfallNeedsAsync(branchId, courseId, cancellationToken);
        var toRecertify = await RecertificationNeedsAsync(branchId, courseId, cancellationToken);

        // Somebody who only needs what changed is not also sent to the whole course of that version.
        var recertifying = toRecertify.Select(n => (n.Need.UserId, n.VersionId)).ToHashSet();
        var fullCourseIds = byRegulation.Concat(byShortfall).Select(n => n.CourseId).Distinct().ToList();
        var versionOf = await db.Courses.AsNoTracking().Where(c => fullCourseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.RecipeVersionId, cancellationToken);
        bool Recertifies(TrainingNeedDto need) =>
            versionOf.GetValueOrDefault(need.CourseId) is { } versionId && recertifying.Contains((need.UserId, versionId));

        var covered = byShortfall.Select(n => (n.UserId, n.CourseId)).ToHashSet();
        return
        [
            .. toRecertify.Select(n => n.Need)
                .Concat(byShortfall.Where(n => !Recertifies(n)))
                .Concat(byRegulation.Where(n => !covered.Contains((n.UserId, n.CourseId)) && !Recertifies(n)))
                .OrderBy(n => n.BranchId).ThenBy(n => n.Username).ThenBy(n => n.CourseTitle),
        ];
    }

    /// <summary>
    /// Re-training after a recipe change: for every published
    /// recertification course, the staff who were certified on an earlier
    /// version of its drink and hold no valid certificate on the version it
    /// is bound to.
    /// </summary>
    private async Task<List<(TrainingNeedDto Need, long VersionId)>> RecertificationNeedsAsync(long? branchId,
        long? courseId, CancellationToken cancellationToken)
    {
        var courses = await db.Courses.AsNoTracking()
            .Where(c => c.State == CourseState.Published && c.CourseType == CourseType.Recertification
                        && c.RecipeVersionId != null && (courseId == null || c.Id == courseId))
            .ToListAsync(cancellationToken);
        if (courses.Count == 0) return [];

        var staff = await db.Users.AsNoTracking().Include(u => u.Role)
            .Where(u => u.Status == UserStatus.Active
                        && (u.Role.RoleName == RoleName.Trainee || u.Role.RoleName == RoleName.Trainer)
                        && (branchId == null || u.BranchId == branchId))
            .ToListAsync(cancellationToken);
        var staffIds = staff.Select(u => u.Id).ToList();
        var courseIds = courses.Select(c => c.Id).ToList();
        var versionIds = courses.Select(c => c.RecipeVersionId!.Value).Distinct().ToList();
        var certified = (await db.Certificates.AsNoTracking()
                .Where(c => c.Status == CertificateStatus.Valid && c.RecipeVersionId != null
                            && versionIds.Contains(c.RecipeVersionId.Value) && staffIds.Contains(c.UserId))
                .Select(c => new { c.UserId, VersionId = c.RecipeVersionId!.Value }).ToListAsync(cancellationToken))
            .Select(c => (c.UserId, c.VersionId)).ToHashSet();
        var enrolled = (await db.Enrollments.AsNoTracking()
                .Where(e => e.State != EnrollmentState.Closed && courseIds.Contains(e.CourseId) && staffIds.Contains(e.UserId))
                .Select(e => new { e.UserId, e.CourseId, e.State }).ToListAsync(cancellationToken))
            .GroupBy(e => (e.UserId, e.CourseId)).ToDictionary(g => g.Key, g => g.First().State);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var rules = TrainingRegulation.Resolve(await db.TrainingRegulations.AsNoTracking().ToListAsync(cancellationToken),
            CourseType.Recertification, today);

        var needs = new List<(TrainingNeedDto, long)>();
        foreach (var course in courses)
        {
            var versionId = course.RecipeVersionId!.Value;
            var certifiedBefore = await db.CertifiedOnEarlierVersionAsync(versionId, staffIds, cancellationToken);
            foreach (var user in staff.Where(u => certifiedBefore.Contains(u.Id) && !certified.Contains((u.Id, versionId))))
            {
                needs.Add((new TrainingNeedDto(user.Id, user.Username, user.FullName, user.BranchId, course.Id,
                    course.Title, course.CourseType, RecertificationReason, rules.RegulationId, rules.DueDays, null,
                    enrolled.TryGetValue((user.Id, course.Id), out var state) ? state : null), versionId));
            }
        }
        return needs;
    }

    /// <summary>
    /// The branch shortfall: for every launch status whose coverage is not
    /// met, the store staff of that branch who hold no valid certificate on
    /// the version bound there, against the published course of that version.
    /// </summary>
    private async Task<List<TrainingNeedDto>> ShortfallNeedsAsync(long? branchId, long? courseId,
        CancellationToken cancellationToken)
    {
        var shortOf = await db.BranchLaunchStatuses.AsNoTracking()
            .Where(l => !l.CoverageMet && l.RecipeVersionId != null && l.Status != LaunchStatus.Withdrawn
                        && (branchId == null || l.BranchId == branchId))
            .Select(l => new { l.BranchId, VersionId = l.RecipeVersionId!.Value })
            .ToListAsync(cancellationToken);
        if (shortOf.Count == 0) return [];

        var versionIds = shortOf.Select(s => s.VersionId).Distinct().ToList();
        var courses = (await db.Courses.AsNoTracking()
                .Where(c => c.State == CourseState.Published && c.RecipeVersionId != null && c.CourseType != CourseType.Recertification
                            && versionIds.Contains(c.RecipeVersionId.Value) && (courseId == null || c.Id == courseId))
                .ToListAsync(cancellationToken))
            .ToDictionary(c => c.RecipeVersionId!.Value);
        if (courses.Count == 0) return [];

        var branchIds = shortOf.Select(s => s.BranchId).Distinct().ToList();
        var staff = await db.Users.AsNoTracking().Include(u => u.Role)
            .Where(u => u.Status == UserStatus.Active && u.Role.RoleName == RoleName.Trainee && u.BranchId != null
                        && branchIds.Contains(u.BranchId.Value))
            .ToListAsync(cancellationToken);
        var staffIds = staff.Select(u => u.Id).ToList();
        var courseIds = courses.Values.Select(c => c.Id).ToList();
        var certified = (await db.Certificates.AsNoTracking()
                .Where(c => c.Status == CertificateStatus.Valid && c.RecipeVersionId != null
                            && versionIds.Contains(c.RecipeVersionId.Value) && staffIds.Contains(c.UserId))
                .Select(c => new { c.UserId, VersionId = c.RecipeVersionId!.Value }).ToListAsync(cancellationToken))
            .Select(c => (c.UserId, c.VersionId)).ToHashSet();
        var enrolled = (await db.Enrollments.AsNoTracking()
                .Where(e => e.State != EnrollmentState.Closed && courseIds.Contains(e.CourseId) && staffIds.Contains(e.UserId))
                .Select(e => new { e.UserId, e.CourseId, e.State }).ToListAsync(cancellationToken))
            .GroupBy(e => (e.UserId, e.CourseId)).ToDictionary(g => g.Key, g => g.First().State);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var regulations = await db.TrainingRegulations.AsNoTracking().ToListAsync(cancellationToken);

        var needs = new List<TrainingNeedDto>();
        foreach (var shortfall in shortOf)
        {
            if (!courses.TryGetValue(shortfall.VersionId, out var course)) continue;
            var rules = TrainingRegulation.Resolve(regulations, course.CourseType, today);
            foreach (var user in staff.Where(u => u.BranchId == shortfall.BranchId))
            {
                if (certified.Contains((user.Id, shortfall.VersionId))) continue;
                needs.Add(new TrainingNeedDto(user.Id, user.Username, user.FullName, user.BranchId, course.Id,
                    course.Title, course.CourseType, ShortfallReason, rules.RegulationId, rules.DueDays, null,
                    enrolled.TryGetValue((user.Id, course.Id), out var state) ? state : null));
            }
        }
        return needs;
    }

    private async Task<List<TrainingNeedDto>> RegulationNeedsAsync(long? branchId, long? courseId,
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

        var boundVersions = courses.Select(c => c.RecipeVersionId).OfType<long>().Distinct().ToList();
        var certifiedOnVersion = (await db.Certificates.AsNoTracking()
                .Where(c => c.Status == CertificateStatus.Valid && c.RecipeVersionId != null
                            && boundVersions.Contains(c.RecipeVersionId.Value) && userIds.Contains(c.UserId))
                .Select(c => new { c.UserId, VersionId = c.RecipeVersionId!.Value }).ToListAsync(cancellationToken))
            .Select(c => (c.UserId, c.VersionId)).ToHashSet();

        var needs = new List<TrainingNeedDto>();
        foreach (var regulation in inForce)
        {
            foreach (var course in courses.Where(c => c.CourseType == regulation.CourseType))
            {
                foreach (var user in users.Where(u => u.Role.RoleName == regulation.MandatoryForRole))
                {
                    if (certified.Contains((user.Id, course.Id))) continue;
                    if (course.RecipeVersionId is { } versionId && certifiedOnVersion.Contains((user.Id, versionId))) continue;
                    needs.Add(new TrainingNeedDto(user.Id, user.Username, user.FullName, user.BranchId, course.Id,
                        course.Title, course.CourseType, RegulationReason, regulation.Id, regulation.DueDays,
                        regulation.PrerequisiteType,
                        enrolled.TryGetValue((user.Id, course.Id), out var state) ? state : null));
                }
            }
        }
        return needs;
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
