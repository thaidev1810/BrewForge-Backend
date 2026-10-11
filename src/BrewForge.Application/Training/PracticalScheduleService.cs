using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

public sealed record PracticalScheduleDto(long Id, long EnrollmentId, long CourseId, string? CourseTitle, long UserId,
    string? TraineeName, long TrainerId, string? TrainerName, DateTimeOffset ScheduledAt, string Location, string? Note,
    ScheduleStatus Status);

public sealed record PracticalScheduleRequest(DateTimeOffset? ScheduledAt, string? Location, string? Note);

/// <summary>
/// The appointment of a practical assessment. A trainer sets it once the trainee has passed the quiz;
/// the trainee reads it. One open appointment per enrolment.
/// </summary>
public sealed class PracticalScheduleService(IBrewForgeDbContext db, LearningService learning, ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly SortMap<PracticalSchedule> Sorting = new SortMap<PracticalSchedule>("scheduledAt", s => s.Id)
        .Add("id", s => s.Id)
        .Add("scheduledAt", s => s.ScheduledAt)
        .Add("status", s => s.Status);

    /// <summary>The trainer's agenda: <c>?status=&amp;from=&amp;to=</c>. A trainer sees the appointments they hold.</summary>
    public async Task<PagedResult<PracticalScheduleDto>> ListAsync(PageQuery paging, string? status, DateOnly? from,
        DateOnly? to, CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<ScheduleStatus>(status, "status");
        var me = currentUser.RequireUserId();

        var query = db.PracticalSchedules.AsNoTracking().Where(s => s.TrainerId == me);
        if (statusFilter is not null) query = query.Where(s => s.Status == statusFilter);
        if (from is not null) query = query.Where(s => s.ScheduledAt >= StartOf(from.Value));
        if (to is not null) query = query.Where(s => s.ScheduledAt < StartOf(to.Value.AddDays(1)));

        var page = await query.ToPagedAsync(paging, Sorting, s => s, cancellationToken);
        return new PagedResult<PracticalScheduleDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page,
            page.Size, page.Total);
    }

    /// <summary>The appointments of one enrolment. A trainee sees only their own.</summary>
    public async Task<IReadOnlyList<PracticalScheduleDto>> ForEnrollmentAsync(long enrollmentId,
        CancellationToken cancellationToken)
    {
        await learning.FindAsync(enrollmentId, false, cancellationToken);
        var rows = await db.PracticalSchedules.AsNoTracking().Where(s => s.EnrollmentId == enrollmentId)
            .OrderByDescending(s => s.ScheduledAt).ToListAsync(cancellationToken);
        return await ToDtosAsync(rows, cancellationToken);
    }

    public async Task<PracticalScheduleDto> CreateAsync(long enrollmentId, PracticalScheduleRequest request,
        CancellationToken cancellationToken)
    {
        var enrollment = await learning.FindAsync(enrollmentId, false, cancellationToken);
        var now = clock.GetUtcNow();
        var schedule = PracticalSchedule.Create(enrollment, currentUser.RequireUserId(), request.ScheduledAt,
            request.Location, request.Note, now);

        if (await db.PracticalSchedules.AnyAsync(s => s.EnrollmentId == enrollmentId && s.Status == ScheduleStatus.Scheduled,
                cancellationToken))
        {
            throw DomainException.RuleViolation(PracticalSchedule.Rule,
                "This enrolment already has an open appointment; reschedule or cancel it.");
        }

        db.PracticalSchedules.Add(schedule);
        db.Audit(AuditEntities.PracticalSchedule, () => schedule.Id, AuditActions.ScheduleSession,
            new { enrollmentId, schedule.ScheduledAt, schedule.Location });
        db.Notify(enrollment.UserId, "Practical scheduled",
            $"Your practical assessment is on {schedule.ScheduledAt:yyyy-MM-dd HH:mm} UTC at {schedule.Location}.", now);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([schedule], cancellationToken))[0];
    }

    public async Task<PracticalScheduleDto> RescheduleAsync(long id, PracticalScheduleRequest request,
        CancellationToken cancellationToken)
    {
        var schedule = await FindAsync(id, cancellationToken);
        var now = clock.GetUtcNow();
        schedule.Reschedule(request.ScheduledAt, request.Location, request.Note, now);

        db.Audit(AuditEntities.PracticalSchedule, () => schedule.Id, AuditActions.RescheduleSession,
            new { schedule.ScheduledAt, schedule.Location });
        var userId = await db.Enrollments.Where(e => e.Id == schedule.EnrollmentId).Select(e => e.UserId)
            .SingleAsync(cancellationToken);
        db.Notify(userId, "Practical rescheduled",
            $"Your practical assessment moved to {schedule.ScheduledAt:yyyy-MM-dd HH:mm} UTC at {schedule.Location}.", now);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([schedule], cancellationToken))[0];
    }

    public Task<PracticalScheduleDto> CancelAsync(long id, CancellationToken cancellationToken) =>
        ChangeAsync(id, s => s.Cancel(), AuditActions.Cancel, cancellationToken);

    public Task<PracticalScheduleDto> CompleteAsync(long id, CancellationToken cancellationToken) =>
        ChangeAsync(id, s => s.Complete(), AuditActions.Close, cancellationToken);

    private async Task<PracticalScheduleDto> ChangeAsync(long id, Action<PracticalSchedule> change, string action,
        CancellationToken cancellationToken)
    {
        var schedule = await FindAsync(id, cancellationToken);
        change(schedule);
        db.Audit(AuditEntities.PracticalSchedule, () => schedule.Id, action);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([schedule], cancellationToken))[0];
    }

    private async Task<PracticalSchedule> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.PracticalSchedules.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("PracticalSchedule", id);

    private async Task<IReadOnlyList<PracticalScheduleDto>> ToDtosAsync(IReadOnlyList<PracticalSchedule> rows,
        CancellationToken cancellationToken)
    {
        var enrollmentIds = rows.Select(r => r.EnrollmentId).Distinct().ToList();
        var enrollments = await db.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Where(e => enrollmentIds.Contains(e.Id)).Select(e => new { e.Id, e.CourseId, e.UserId })
            .ToDictionaryAsync(e => e.Id, cancellationToken);
        var courseIds = enrollments.Values.Select(e => e.CourseId).Distinct().ToList();
        var courseTitles = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, cancellationToken);
        var userIds = enrollments.Values.Select(e => e.UserId).Concat(rows.Select(r => r.TrainerId)).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return [.. rows.Select(r =>
        {
            var e = enrollments[r.EnrollmentId];
            return new PracticalScheduleDto(r.Id, r.EnrollmentId, e.CourseId, courseTitles.GetValueOrDefault(e.CourseId),
                e.UserId, names.GetValueOrDefault(e.UserId), r.TrainerId, names.GetValueOrDefault(r.TrainerId),
                r.ScheduledAt, r.Location, r.Note, r.Status);
        })];
    }

    private static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
