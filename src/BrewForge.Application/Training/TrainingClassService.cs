using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Notifications;
using BrewForge.Application.Courses;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

/// <summary>UC-29 and UC-30: classes, sessions and attendance (SCR-31, SCR-32).</summary>
public sealed class TrainingClassService(IBrewForgeDbContext db, CourseService courses, EnrollmentEvaluator evaluator,
    ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly SortMap<TrainingClass> Sorting = new SortMap<TrainingClass>("startDate", c => c.Id)
        .Add("id", c => c.Id)
        .Add("name", c => c.Name)
        .Add("startDate", c => c.StartDate)
        .Add("endDate", c => c.EndDate)
        .Add("state", c => c.State);

    // ---------------------------------------------------------------- classes

    public async Task<PagedResult<TrainingClassDto>> ListAsync(PageQuery paging, string? state, long? branchId,
        long? courseId, CancellationToken cancellationToken)
    {
        var stateFilter = PagingExtensions.ParseFilter<ClassState>(state, "state");

        var query = db.TrainingClasses.AsNoTracking().Include(c => c.Sessions).ThenInclude(s => s.Modules).AsQueryable();
        if (stateFilter is not null) query = query.Where(c => c.State == stateFilter);
        if (branchId is not null) query = query.Where(c => c.BranchId == branchId);
        if (courseId is not null) query = query.Where(c => c.CourseId == courseId);

        var page = await query.ToPagedAsync(paging, Sorting, c => c, cancellationToken);
        var counts = await EnrolledCountsAsync([.. page.Items.Select(c => c.Id)], cancellationToken);
        return new PagedResult<TrainingClassDto>([.. page.Items.Select(c => ToDto(c, counts.GetValueOrDefault(c.Id)))],
            page.Page, page.Size, page.Total);
    }

    public async Task<TrainingClassDto> GetAsync(long id, CancellationToken cancellationToken) =>
        await ToDtoAsync(await FindAsync(id, cancellationToken), cancellationToken);

    public async Task<TrainingClassDto> CreateAsync(TrainingClassRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(request.CourseId is not null, "courseId", "is required")
            .Check(request.BranchId is not null, "branchId", "is required")
            .Check(request.StartDate is not null, "startDate", "is required")
            .Check(request.EndDate is not null, "endDate", "is required")
            .ThrowIfAny();

        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CourseId, cancellationToken)
                     ?? throw DomainException.Validation("The course does not exist.",
                         new ErrorDetail("courseId", $"course {request.CourseId} does not exist"));
        if (!await db.Branches.AnyAsync(b => b.Id == request.BranchId, cancellationToken))
        {
            throw DomainException.Validation("The branch does not exist.",
                new ErrorDetail("branchId", $"branch {request.BranchId} does not exist"));
        }

        var trainingClass = TrainingClass.Create(course, request.BranchId!.Value, request.Name,
            request.StartDate!.Value, request.EndDate!.Value, currentUser.RequireUserId());

        db.TrainingClasses.Add(trainingClass);
        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.Create,
            new { trainingClass.Name, trainingClass.CourseId, trainingClass.BranchId });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(trainingClass, 0);
    }

    public async Task<TrainingClassDto> UpdateAsync(long id, TrainingClassRequest request,
        CancellationToken cancellationToken)
    {
        var trainingClass = await FindAsync(id, cancellationToken);
        new FieldErrors()
            .Check(request.CourseId is null || request.CourseId == trainingClass.CourseId, "courseId", "cannot be changed")
            .Check(request.BranchId is null || request.BranchId == trainingClass.BranchId, "branchId", "cannot be changed")
            .ThrowIfAny();

        trainingClass.Update(request.Name, request.StartDate ?? trainingClass.StartDate,
            request.EndDate ?? trainingClass.EndDate);

        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.Update,
            new { trainingClass.Name, trainingClass.StartDate, trainingClass.EndDate });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(trainingClass, cancellationToken);
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>409 BR-34 if the trainer is not certified on the version the course is bound to.</summary>
    public async Task<TrainingSessionDto> AddSessionAsync(long classId, SessionRequest request,
        CancellationToken cancellationToken)
    {
        var trainingClass = await FindAsync(classId, cancellationToken);
        var (spec, course, certificates) = await PrepareAsync(trainingClass, request, cancellationToken);

        var session = trainingClass.AddSession(spec, course, certificates);

        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.ScheduleSession,
            new { session.SessionNo, session.ScheduledDate, session.TrainerId });
        await db.SaveChangesAsync(cancellationToken);
        return TrainingSessionDto.From(session);
    }

    public async Task<TrainingSessionDto> UpdateSessionAsync(long sessionId, SessionRequest request,
        CancellationToken cancellationToken)
    {
        var (trainingClass, session) = await FindSessionAsync(sessionId, cancellationToken);
        var (spec, course, certificates) = await PrepareAsync(trainingClass, request, cancellationToken);

        trainingClass.UpdateSession(session, spec, course, certificates);

        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.RescheduleSession,
            new { session.SessionNo, session.ScheduledDate, session.TrainerId });
        await db.SaveChangesAsync(cancellationToken);
        return TrainingSessionDto.From(session);
    }

    public async Task DeleteSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        var (trainingClass, session) = await FindSessionAsync(sessionId, cancellationToken);

        trainingClass.RemoveSession(session);

        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.RemoveSession,
            new { session.SessionNo });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- open and close

    /// <summary>
    /// PLANNED to RUNNING. Assigns the course to every trainee of the class,
    /// sets each deadline from the regulation in force today, and notifies them.
    /// </summary>
    public async Task<OpenClassResultDto> OpenAsync(long classId, OpenClassRequest? request,
        CancellationToken cancellationToken)
    {
        var trainingClass = await FindAsync(classId, cancellationToken);
        var course = await courses.FindAsync(trainingClass.CourseId, cancellationToken);
        var roster = await RosterAsync(trainingClass, request?.TraineeIds, cancellationToken);

        var now = clock.GetUtcNow();
        var rules = await evaluator.RulesAsync(course.CourseType, DateOnly.FromDateTime(now.UtcDateTime),
            cancellationToken);
        var rosterIds = roster.Select(u => u.Id).ToList();
        var alreadyEnrolled = await db.Enrollments.IgnoreQueryFilters()
            .Where(e => e.CourseId == course.Id && e.State != EnrollmentState.Closed && rosterIds.Contains(e.UserId))
            .Select(e => e.UserId).ToListAsync(cancellationToken);

        trainingClass.Open();
        var created = new List<Enrollment>();
        foreach (var user in roster.Where(u => !alreadyEnrolled.Contains(u.Id)))
        {
            var enrollment = Enrollment.Assign(course, user.Id, trainingClass.Id, rules, now);
            db.Enrollments.Add(enrollment);
            created.Add(enrollment);
            db.Audit(AuditEntities.Enrollment, () => enrollment.Id, AuditActions.Assign,
                new { enrollment.CourseId, enrollment.UserId, enrollment.DueDate, classId, regulationId = rules.RegulationId });
            db.Notify(user.Id, "Course assigned",
                $"You have been enrolled on '{course.Title}' in class '{trainingClass.Name}'. Due {enrollment.DueDate:yyyy-MM-dd}.",
                now);
        }
        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.Open,
            new { enrolled = created.Count, skipped = alreadyEnrolled });
        await db.SaveChangesAsync(cancellationToken);

        return new OpenClassResultDto(await ToDtoAsync(trainingClass, cancellationToken),
            [.. created.Select(e => e.Id)], alreadyEnrolled);
    }

    /// <summary>RUNNING to CLOSED. Attendance is frozen from here on.</summary>
    public async Task<TrainingClassDto> CloseAsync(long classId, CancellationToken cancellationToken)
    {
        var trainingClass = await FindAsync(classId, cancellationToken);
        trainingClass.Close();
        db.Audit(AuditEntities.TrainingClass, () => trainingClass.Id, AuditActions.Close);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(trainingClass, cancellationToken);
    }

    // ---------------------------------------------------------------- attendance

    public async Task<AttendanceSheetDto> GetAttendanceAsync(long sessionId, CancellationToken cancellationToken)
    {
        var (trainingClass, session) = await FindSessionAsync(sessionId, cancellationToken);
        return await SheetAsync(trainingClass, session, cancellationToken);
    }

    /// <summary>
    /// UC-30. Records PRESENT, ABSENT or EXCUSED per enrolment. Allowed only
    /// while the class is RUNNING; a change to an entry that already exists
    /// is a correction and is written to the audit log on its own.
    /// </summary>
    public async Task<AttendanceSheetDto> RecordAttendanceAsync(long sessionId, AttendanceRequest request,
        CancellationToken cancellationToken)
    {
        var (trainingClass, session) = await FindSessionAsync(sessionId, cancellationToken);
        trainingClass.EnsureAttendanceOpen();

        var entries = request.Entries ?? throw DomainException.Validation("The attendance sheet is empty.",
            new ErrorDetail("entries", "is required"));
        var enrollments = await db.Enrollments.Include(e => e.Modules)
            .Where(e => e.TrainingClassId == trainingClass.Id).ToListAsync(cancellationToken);
        var byId = enrollments.ToDictionary(e => e.Id);

        var errors = new FieldErrors();
        for (var i = 0; i < entries.Count; i++)
        {
            errors.Check(entries[i].EnrollmentId is { } id && byId.ContainsKey(id), $"entries[{i}].enrollmentId",
                    "is not an enrolment of this class")
                .Check(entries[i].Status is not null, $"entries[{i}].status", "is required: PRESENT, ABSENT or EXCUSED");
        }
        errors.Check(entries.Select(e => e.EnrollmentId).Distinct().Count() == entries.Count, "entries",
            "lists the same enrolment more than once");
        errors.ThrowIfAny();

        var recorderId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        var existing = await db.Attendances.Where(a => a.SessionId == session.Id).ToDictionaryAsync(a => a.EnrollmentId, cancellationToken);
        var recorded = 0;

        foreach (var entry in entries)
        {
            var enrollmentId = entry.EnrollmentId!.Value;
            if (!existing.TryGetValue(enrollmentId, out var attendance))
            {
                db.Attendances.Add(new Attendance(session.Id, enrollmentId, entry.Status!.Value, entry.Note, recorderId, now));
                recorded++;
                continue;
            }

            var before = attendance.Status;
            if (attendance.Correct(entry.Status!.Value, entry.Note, recorderId, now))
            {
                db.Audit(AuditEntities.Attendance, () => attendance.Id, AuditActions.CorrectAttendance, new
                {
                    sessionId = session.Id, enrollmentId, from = before.Code(), to = attendance.Status.Code(), attendance.Note,
                });
            }
        }
        if (recorded > 0)
        {
            db.Audit(AuditEntities.TrainingSession, () => session.Id, AuditActions.RecordAttendance,
                new { classId = trainingClass.Id, session.SessionNo, entries = recorded });
        }
        await db.SaveChangesAsync(cancellationToken);

        // Attendance is half of the eligibility condition, so the checker runs
        // for everyone on the sheet (BR-32).
        var courseType = await db.Courses.Where(c => c.Id == trainingClass.CourseId).Select(c => c.CourseType)
            .SingleAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await evaluator.EvaluateAsync(byId[entry.EnrollmentId!.Value], courseType, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);

        return await SheetAsync(trainingClass, session, cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(SessionSpec Spec, Course Course, List<Certificate> Certificates)> PrepareAsync(
        TrainingClass trainingClass, SessionRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(request.ScheduledDate is not null, "scheduledDate", "is required")
            .Check(request.StartTime is not null, "startTime", "is required")
            .Check(request.DurationMinutes is not null, "durationMinutes", "is required")
            .Check(request.TrainerId is not null, "trainerId", "is required")
            .ThrowIfAny();

        var trainerId = request.TrainerId!.Value;
        var trainer = await db.Users.AsNoTracking().Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Id == trainerId, cancellationToken);
        if (trainer is not { IsActive: true, Role.RoleName: RoleName.Trainer })
        {
            throw DomainException.Validation("The trainer must be an active user with the TRAINER role.",
                new ErrorDetail("trainerId", $"user {trainerId} is not an active trainer"));
        }

        var course = await courses.FindAsync(trainingClass.CourseId, cancellationToken);
        var certificates = await db.Certificates.AsNoTracking()
            .Where(c => c.UserId == trainerId).ToListAsync(cancellationToken);
        var spec = new SessionSpec(request.SessionNo, request.ScheduledDate!.Value, request.StartTime!.Value,
            request.DurationMinutes!.Value, request.Location, trainerId, request.ModuleIds ?? []);
        return (spec, course, certificates);
    }

    private async Task<List<AppUser>> RosterAsync(TrainingClass trainingClass, IReadOnlyList<long>? traineeIds,
        CancellationToken cancellationToken)
    {
        if (traineeIds is not { Count: > 0 })
        {
            var branchTrainees = await db.Users.Include(u => u.Role)
                .Where(u => u.BranchId == trainingClass.BranchId && u.Role.RoleName == RoleName.Trainee
                            && u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);
            if (branchTrainees.Count == 0)
            {
                throw DomainException.RuleViolation("CLASS_HAS_NO_TRAINEES",
                    "The branch of this class has no active trainee to enrol. Name the trainees explicitly.");
            }
            return branchTrainees;
        }

        var ids = traineeIds.Distinct().ToList();
        var users = await db.Users.Include(u => u.Role).Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
        var errors = new FieldErrors();
        foreach (var id in ids)
        {
            var user = users.SingleOrDefault(u => u.Id == id);
            errors.Check(user is not null, "traineeIds", $"user {id} does not exist")
                .Check(user is null || user.IsActive, "traineeIds", $"user {id} is inactive")
                .Check(user is null || user.Role.RoleName is RoleName.Trainee or RoleName.Trainer, "traineeIds",
                    $"user {id} is neither a trainee nor a trainer")
                .Check(user is null || user.Role.RoleName != RoleName.Trainee || user.BranchId == trainingClass.BranchId,
                    "traineeIds", $"trainee {id} belongs to another branch");
        }
        errors.ThrowIfAny();
        return users;
    }

    private async Task<AttendanceSheetDto> SheetAsync(TrainingClass trainingClass, TrainingSession session,
        CancellationToken cancellationToken)
    {
        var enrollments = await db.Enrollments.AsNoTracking().Include(e => e.User).Include(e => e.Modules)
            .Where(e => e.TrainingClassId == trainingClass.Id && e.State != EnrollmentState.Closed)
            .OrderBy(e => e.User.FullName).ToListAsync(cancellationToken);
        var recorded = await db.Attendances.AsNoTracking().Where(a => a.SessionId == session.Id)
            .ToDictionaryAsync(a => a.EnrollmentId, cancellationToken);
        var courseType = await db.Courses.Where(c => c.Id == trainingClass.CourseId).Select(c => c.CourseType)
            .SingleAsync(cancellationToken);

        var entries = new List<AttendanceEntryDto>();
        foreach (var enrollment in enrollments)
        {
            var (rules, attendance) = await evaluator.LoadAsync(enrollment, courseType, cancellationToken);
            var entry = recorded.GetValueOrDefault(enrollment.Id);
            entries.Add(new AttendanceEntryDto(enrollment.Id, enrollment.UserId, enrollment.User.FullName,
                entry?.Status, entry?.Note, entry?.RecordedAt, attendance.CanStillMeet(rules.MinAttendancePct)));
        }
        return new AttendanceSheetDto(session.Id, trainingClass.Id, trainingClass.State, entries);
    }

    private async Task<TrainingClass> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.TrainingClasses.Include(c => c.Sessions).ThenInclude(s => s.Modules)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Training class", id);

    private async Task<(TrainingClass Class, TrainingSession Session)> FindSessionAsync(long sessionId,
        CancellationToken cancellationToken)
    {
        var trainingClass = await db.TrainingClasses.Include(c => c.Sessions).ThenInclude(s => s.Modules)
                                .SingleOrDefaultAsync(c => c.Sessions.Any(s => s.Id == sessionId), cancellationToken)
                            ?? throw DomainException.NotFound("Training session", sessionId);
        return (trainingClass, trainingClass.Sessions.Single(s => s.Id == sessionId));
    }

    private async Task<Dictionary<long, int>> EnrolledCountsAsync(IReadOnlyCollection<long> classIds,
        CancellationToken cancellationToken) =>
        await db.Enrollments.AsNoTracking()
            .Where(e => e.TrainingClassId != null && classIds.Contains(e.TrainingClassId.Value) && e.State != EnrollmentState.Closed)
            .GroupBy(e => e.TrainingClassId!.Value)
            .Select(g => new { ClassId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClassId, x => x.Count, cancellationToken);

    private async Task<TrainingClassDto> ToDtoAsync(TrainingClass trainingClass, CancellationToken cancellationToken) =>
        ToDto(trainingClass, (await EnrolledCountsAsync([trainingClass.Id], cancellationToken)).GetValueOrDefault(trainingClass.Id));

    private static TrainingClassDto ToDto(TrainingClass c, int enrolled) =>
        new(c.Id, c.CourseId, c.BranchId, c.Name, c.StartDate, c.EndDate, c.OpenedBy, c.State, enrolled,
            [.. c.OrderedSessions().Select(TrainingSessionDto.From)]);
}
