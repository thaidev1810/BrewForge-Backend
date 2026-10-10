using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;

namespace BrewForge.Domain.Training;

/// <summary>
/// The rules an enrolment is held to: how long the trainee has, how many
/// retakes, how much attendance. Taken from the regulation in force on the
/// day the enrolment was created, or from the defaults of the schema when no
/// regulation covers that course type yet.
/// </summary>
public readonly record struct TrainingRules(long? RegulationId, int DueDays, int MaxRetakes, int MinAttendancePct)
{
    public static readonly TrainingRules Default = new(null, DueDays: 14, MaxRetakes: 2, MinAttendancePct: 80);
}

/// <summary>The policy layer of the training process, held as data so the system can enforce it (UC-27).</summary>
public sealed class TrainingRegulation
{
    /// <summary>Named when a change would alter the rules of enrolments that already exist.</summary>
    public const string RetroactiveRule = "REGULATION_RETROACTIVE";

    private TrainingRegulation() { }

    public long Id { get; private set; }
    public CourseType CourseType { get; private set; }

    /// <summary>The role that must take courses of this type, if any.</summary>
    public RoleName? MandatoryForRole { get; private set; }

    /// <summary>The course type that must be passed first, if any.</summary>
    public CourseType? PrerequisiteType { get; private set; }
    public int DueDays { get; private set; }
    public int MaxRetakes { get; private set; } = 2;
    public int MinAttendancePct { get; private set; } = 80;
    public DateOnly EffectiveFrom { get; private set; }
    public long CreatedBy { get; private set; }

    public TrainingRules Rules => new(Id, DueDays, MaxRetakes, MinAttendancePct);

    public static TrainingRegulation Create(CourseType courseType, RoleName? mandatoryForRole,
        CourseType? prerequisiteType, int dueDays, int maxRetakes, int minAttendancePct, DateOnly effectiveFrom,
        long createdBy)
    {
        var regulation = new TrainingRegulation { CourseType = courseType, CreatedBy = createdBy };
        regulation.Update(mandatoryForRole, prerequisiteType, dueDays, maxRetakes, minAttendancePct, effectiveFrom);
        return regulation;
    }

    public void Update(RoleName? mandatoryForRole, CourseType? prerequisiteType, int dueDays, int maxRetakes,
        int minAttendancePct, DateOnly effectiveFrom)
    {
        new FieldErrors()
            .Check(Enum.IsDefined(CourseType), "courseType", "is not a valid course type")
            .Check(prerequisiteType != CourseType, "prerequisiteType", "a course type cannot be its own prerequisite")
            .Check(dueDays > 0, "dueDays", "must be greater than 0")
            .Check(maxRetakes >= 0, "maxRetakes", "must not be negative")
            .Check(minAttendancePct is >= 0 and <= 100, "minAttendancePct", "must be between 0 and 100")
            .ThrowIfAny();

        MandatoryForRole = mandatoryForRole;
        PrerequisiteType = prerequisiteType;
        DueDays = dueDays;
        MaxRetakes = maxRetakes;
        MinAttendancePct = minAttendancePct;
        EffectiveFrom = effectiveFrom;
    }

    /// <summary>
    /// The rules of an enrolment: those of the regulation for its course type
    /// that was most recently in force on the day it was created. A
    /// regulation that takes effect later never reaches back to it.
    /// </summary>
    public static TrainingRules Resolve(IEnumerable<TrainingRegulation> regulations, CourseType courseType,
        DateOnly enrolledOn) =>
        InForce(regulations, courseType, enrolledOn)?.Rules ?? TrainingRules.Default;

    /// <summary>The regulation for a course type that is in force on a day: the one that took effect most recently.</summary>
    public static TrainingRegulation? InForce(IEnumerable<TrainingRegulation> regulations, CourseType courseType,
        DateOnly on) =>
        regulations
            .Where(regulation => regulation.CourseType == courseType && regulation.EffectiveFrom <= on)
            .OrderByDescending(regulation => regulation.EffectiveFrom)
            .ThenByDescending(regulation => regulation.Id)
            .FirstOrDefault();

    /// <summary>
    /// A rule change applies only to enrolments created after it takes
    /// effect. A regulation whose effective date is on or before the day an
    /// existing enrolment of that course type was created would change the
    /// rules of that enrolment after the fact, and is refused.
    /// </summary>
    public static void EnsureNotRetroactive(DateOnly effectiveFrom, DateOnly? latestEnrolmentOfThatType)
    {
        if (latestEnrolmentOfThatType is { } latest && effectiveFrom <= latest)
        {
            throw DomainException.RuleViolation(RetroactiveRule,
                $"Enrolments of this course type were created up to {latest:yyyy-MM-dd}. A regulation must take effect " +
                "after that date, so that enrolments in flight keep the rule they were created under.",
                details: new ErrorDetail("effectiveFrom", $"must be after {latest:yyyy-MM-dd}"));
        }
    }
}
