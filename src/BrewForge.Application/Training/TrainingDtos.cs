using BrewForge.Application.Courses;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;

namespace BrewForge.Application.Training;

// ---------------------------------------------------------------- regulation

public sealed record TrainingRegulationDto(long Id, CourseType CourseType, RoleName? MandatoryForRole,
    CourseType? PrerequisiteType, int DueDays, int MaxRetakes, int MinAttendancePct, DateOnly EffectiveFrom)
{
    public static TrainingRegulationDto From(TrainingRegulation regulation) =>
        new(regulation.Id, regulation.CourseType, regulation.MandatoryForRole, regulation.PrerequisiteType,
            regulation.DueDays, regulation.MaxRetakes, regulation.MinAttendancePct, regulation.EffectiveFrom);
}

public sealed record TrainingRegulationRequest(CourseType? CourseType, RoleName? MandatoryForRole,
    CourseType? PrerequisiteType, int? DueDays, int? MaxRetakes, int? MinAttendancePct, DateOnly? EffectiveFrom);

/// <summary>Someone who must be trained, and why.</summary>
public sealed record TrainingNeedDto(long UserId, string Username, string FullName, long? BranchId, long CourseId,
    string CourseTitle, CourseType CourseType, string Reason, long? RegulationId, int DueDays,
    CourseType? PrerequisiteType, EnrollmentState? EnrollmentState);

// ---------------------------------------------------------------- classes and sessions

public sealed record TrainingSessionDto(long Id, long TrainingClassId, int SessionNo, DateOnly ScheduledDate,
    TimeOnly StartTime, int DurationMinutes, string? Location, long TrainerId, IReadOnlyList<long> ModuleIds)
{
    public static TrainingSessionDto From(TrainingSession session) =>
        new(session.Id, session.TrainingClassId, session.SessionNo, session.ScheduledDate, session.StartTime,
            session.DurationMinutes, session.Location, session.TrainerId,
            [.. session.Modules.Select(module => module.CourseModuleId).Order()]);
}

public sealed record TrainingClassDto(long Id, long CourseId, long BranchId, string Name, DateOnly StartDate,
    DateOnly EndDate, long OpenedBy, ClassState State, int EnrolledCount, IReadOnlyList<TrainingSessionDto> Sessions);

public sealed record TrainingClassRequest(long? CourseId, long? BranchId, string? Name, DateOnly? StartDate,
    DateOnly? EndDate);

public sealed record SessionRequest(int? SessionNo, DateOnly? ScheduledDate, TimeOnly? StartTime,
    int? DurationMinutes, string? Location, long? TrainerId, IReadOnlyList<long>? ModuleIds);

/// <summary>
/// <c>TraineeIds</c> is optional: without it the class takes every active
/// trainee of its branch.
/// </summary>
public sealed record OpenClassRequest(IReadOnlyList<long>? TraineeIds);

public sealed record OpenClassResultDto(TrainingClassDto Class, IReadOnlyList<long> EnrollmentIds,
    IReadOnlyList<long> SkippedUserIds);

// ---------------------------------------------------------------- attendance

public sealed record AttendanceEntryDto(long EnrollmentId, long UserId, string FullName, AttendanceStatus? Status,
    string? Note, DateTimeOffset? RecordedAt, bool AttendanceStillReachable);

public sealed record AttendanceSheetDto(long SessionId, long TrainingClassId, ClassState ClassState,
    IReadOnlyList<AttendanceEntryDto> Entries);

public sealed record AttendanceRequest(IReadOnlyList<AttendanceEntryRequest>? Entries);

public sealed record AttendanceEntryRequest(long? EnrollmentId, AttendanceStatus? Status, string? Note);

// ---------------------------------------------------------------- learning

public sealed record EnrollmentDto(long Id, long CourseId, string CourseTitle, CourseType CourseType,
    long? RecipeVersionId, long? TrainingClassId, long UserId, DateOnly? DueDate, int ProgressPercent,
    EnrollmentState State, DateTimeOffset EnrolledAt, DateTimeOffset? CompletedAt, bool Overdue);

/// <summary>A module as the trainee sees it: the rendered lessons, and whether it is done.</summary>
public sealed record EnrollmentModuleDto(CourseModuleDto Module, bool Completed, DateTimeOffset? CompletedAt);

public sealed record MissingModuleDto(long CourseModuleId, ModuleType ModuleType);

/// <summary>The answer of the eligibility checker (BR-32).</summary>
public sealed record EligibilityDto(bool Eligible, IReadOnlyList<MissingModuleDto> MissingModules, int AttendancePct,
    int MinAttendancePct, bool AttendanceStillReachable, int RetakesLeft, long? RegulationId, EnrollmentState State);
