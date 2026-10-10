using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>One course of a stage, and where the user stands on it.</summary>
public sealed record PathCourseDto(long CourseId, string Title, long? RecipeVersionId, string Status,
    long? EnrollmentId, EnrollmentState? EnrollmentState, DateOnly? DueDate, long? CertificateId);

/// <summary>
/// One stage of a learning path: the courses of one type that are mandatory
/// for the user's role. A stage is blocked while the user has not passed a
/// course of its prerequisite type.
/// </summary>
public sealed record PathStageDto(CourseType CourseType, CourseType? PrerequisiteType, bool Blocked, long RegulationId,
    int DueDays, IReadOnlyList<PathCourseDto> Courses);

public sealed record LearningPathDto(long UserId, string Username, string FullName, RoleName Role, long? BranchId,
    IReadOnlyList<PathStageDto> Stages);

/// <summary>
/// The learning path of a member of staff: what the training regulation
/// makes mandatory for their role, in the order its prerequisites impose.
/// The path is not stored. It is read from the regulation in force, the
/// published courses and what the user has passed, so it follows a change of
/// any of them.
///
/// A stage holds the published courses of its type that concern the user: a
/// course built from no recipe concerns everybody, and a course built from a
/// recipe version concerns those whose branch has that version on its menu.
/// </summary>
public sealed class LearningPathService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public const string PrerequisiteRule = "PREREQUISITE";

    public const string Certified = "CERTIFIED";
    public const string Enrolled = "ENROLLED";
    public const string BlockedStatus = "BLOCKED";
    public const string Available = "AVAILABLE";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    // ---------------------------------------------------------------- the path

    public Task<LearningPathDto> MyPathAsync(CancellationToken cancellationToken) =>
        GetPathAsync(currentUser.RequireUserId(), cancellationToken);

    public async Task<LearningPathDto> GetPathAsync(long userId, CancellationToken cancellationToken) =>
        ToDto(await LoadAsync(userId, tracking: false, cancellationToken));

    // ---------------------------------------------------------------- loading

    private sealed record Stage(TrainingRegulation Regulation, bool Blocked, List<Course> Courses);

    private sealed record UserPath(AppUser User, List<Stage> Stages, List<Certificate> Certificates,
        List<Enrollment> Enrollments)
    {
        public Certificate? CertificateOn(Course course) =>
            Certificates.FirstOrDefault(c => c.Status == CertificateStatus.Valid
                                             && (c.CourseId == course.Id
                                                 || (course.RecipeVersionId is not null && c.RecipeVersionId == course.RecipeVersionId)));

        public Enrollment? EnrollmentOn(Course course) => Enrollments.FirstOrDefault(e => e.CourseId == course.Id);

        public string StatusOf(Stage stage, Course course) =>
            CertificateOn(course) is not null ? Certified
            : EnrollmentOn(course) is not null ? Enrolled
            : stage.Blocked ? BlockedStatus
            : Available;
    }

    private async Task<UserPath> LoadAsync(long userId, bool tracking, CancellationToken cancellationToken)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Role)
                       .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw DomainException.NotFound("User", userId);

        var today = Today;
        var mandatory = (await db.TrainingRegulations.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(r => r.CourseType)
            .Select(group => TrainingRegulation.InForce(group, group.Key, today))
            .OfType<TrainingRegulation>()
            .Where(r => r.MandatoryForRole == user.Role.RoleName)
            // What has no prerequisite comes first; the rest follows in the order of the course types.
            .OrderBy(r => r.PrerequisiteType is null ? 0 : 1).ThenBy(r => r.CourseType)
            .ToList();
        var types = mandatory.Select(r => r.CourseType).ToList();

        var menu = user.BranchId is { } branchId
            ? await db.BranchLaunchStatuses.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.BranchId == branchId && l.Status != LaunchStatus.Withdrawn && l.RecipeVersionId != null)
                .Select(l => l.RecipeVersionId!.Value).ToListAsync(cancellationToken)
            : [];
        var courseQuery = db.Courses.Include(c => c.Modules)
            .Where(c => c.State == CourseState.Published && types.Contains(c.CourseType)
                        && (c.RecipeVersionId == null || menu.Contains(c.RecipeVersionId.Value)));
        var courses = await (tracking ? courseQuery : courseQuery.AsNoTracking()).OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var certificates = await db.Certificates.AsNoTracking().Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);
        var passedCourseIds = certificates.Select(c => c.CourseId).Distinct().ToList();
        var passedTypes = await db.Courses.AsNoTracking().Where(c => passedCourseIds.Contains(c.Id))
            .Select(c => c.CourseType).Distinct().ToListAsync(cancellationToken);
        var enrollments = await db.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.UserId == userId && e.State != EnrollmentState.Closed).ToListAsync(cancellationToken);

        return new UserPath(user,
        [
            .. mandatory.Select(regulation => new Stage(regulation,
                regulation.PrerequisiteType is { } prerequisite && !passedTypes.Contains(prerequisite),
                [.. courses.Where(c => c.CourseType == regulation.CourseType)])),
        ], certificates, enrollments);
    }

    private static LearningPathDto ToDto(UserPath path) =>
        new(path.User.Id, path.User.Username, path.User.FullName, path.User.Role.RoleName, path.User.BranchId,
        [
            .. path.Stages.Select(stage => new PathStageDto(stage.Regulation.CourseType, stage.Regulation.PrerequisiteType,
                stage.Blocked, stage.Regulation.Id, stage.Regulation.DueDays,
                [
                    .. stage.Courses.Select(course =>
                    {
                        var enrollment = path.EnrollmentOn(course);
                        return new PathCourseDto(course.Id, course.Title, course.RecipeVersionId, path.StatusOf(stage, course),
                            enrollment?.Id, enrollment?.State, enrollment?.DueDate, path.CertificateOn(course)?.Id);
                    }),
                ])),
        ]);
}
