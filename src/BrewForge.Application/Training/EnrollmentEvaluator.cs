using BrewForge.Application.Abstractions;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>
/// Supplies the two facts the eligibility check needs from outside the
/// enrolment: the rules in force on the day it was created, and the trainee's
/// attendance so far. The check itself is the enrolment's.
/// </summary>
public sealed class EnrollmentEvaluator(IBrewForgeDbContext db)
{
    public async Task<TrainingRules> RulesAsync(CourseType courseType, DateOnly enrolledOn,
        CancellationToken cancellationToken)
    {
        var regulations = await db.TrainingRegulations.AsNoTracking()
            .Where(regulation => regulation.CourseType == courseType)
            .ToListAsync(cancellationToken);
        return TrainingRegulation.Resolve(regulations, courseType, enrolledOn);
    }

    public async Task<AttendanceSummary> AttendanceAsync(Enrollment enrollment, CancellationToken cancellationToken)
    {
        if (enrollment.TrainingClassId is not { } classId) return AttendanceSummary.NoSessions;

        var sessionIds = await db.TrainingClasses.IgnoreQueryFilters()
            .Where(trainingClass => trainingClass.Id == classId)
            .SelectMany(trainingClass => trainingClass.Sessions)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);
        var recorded = await db.Attendances
            .Where(attendance => attendance.EnrollmentId == enrollment.Id && sessionIds.Contains(attendance.SessionId))
            .Select(attendance => attendance.Status)
            .ToListAsync(cancellationToken);
        return AttendanceSummary.From(sessionIds.Count, recorded);
    }

    public async Task<(TrainingRules Rules, AttendanceSummary Attendance)> LoadAsync(Enrollment enrollment,
        CourseType courseType, CancellationToken cancellationToken) =>
        (await RulesAsync(courseType, enrollment.EnrolledOn, cancellationToken),
            await AttendanceAsync(enrollment, cancellationToken));

    /// <summary>
    /// Runs the eligibility checker on an enrolment after its modules or its
    /// attendance changed: IN_PROGRESS becomes ELIGIBLE when both conditions
    /// hold (BR-32).
    /// </summary>
    public async Task<EligibilityResult> EvaluateAsync(Enrollment enrollment, CourseType courseType,
        CancellationToken cancellationToken)
    {
        var (rules, attendance) = await LoadAsync(enrollment, courseType, cancellationToken);
        return enrollment.EvaluateEligibility(attendance, rules);
    }
}
