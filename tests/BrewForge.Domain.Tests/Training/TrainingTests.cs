using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Training;

/// <summary>
/// UC-27, UC-29, UC-30 and UC-14: the training regulation, classes and
/// sessions (BR-34), attendance, and the eligibility gate (BR-32).
/// </summary>
public sealed class TrainingTests
{
    private const long Trainer = 6;
    private const long Trainee = 61;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly TrainingRules Rules = new(3, DueDays: 14, MaxRetakes: 2, MinAttendancePct: 80);

    // ---------------------------------------------------------------- fixtures

    /// <summary>A PUBLISHED course, with ids on itself and its modules as it would have after being stored.</summary>
    private static Course PublishedCourse(RecipeVersion? version = null, long courseId = 20,
        CourseType type = CourseType.Product)
    {
        var course = Course.Create(type, "Oolong milk tea", type == CourseType.Product ? version ?? ReleasedVersion() : version, Trainer);
        WithId(course, courseId);
        foreach (var module in course.Modules) WithId(module, courseId * 10 + module.ModuleOrder);
        foreach (var module in course.Modules.Where(m => m.Source != ModuleSource.Generated))
        {
            foreach (var lesson in module.Lessons.ToList()) course.UpdateLesson(module, lesson, null, "Prose", null);
            if (module.Lessons.Count == 0) course.AddLesson(module, "Lesson", "Content", null);
            course.SetModuleDuration(module, 10);
        }
        course.AddQuizQuestion(course.Modules[0].Id, "A question?", [new("A", "Yes"), new("B", "No")], "A");
        course.Submit();
        course.Approve(8, Now);
        return course;
    }

    private static Enrollment Assigned(Course? course = null) =>
        Enrollment.Assign(course ?? PublishedCourse(), Trainee, trainingClassId: 9, Rules, Now);

    private static Enrollment WithAllModulesComplete(Course? course = null)
    {
        var enrollment = Assigned(course);
        foreach (var module in enrollment.Modules.ToList()) enrollment.CompleteModule(module.CourseModuleId, Now);
        return enrollment;
    }

    private static AttendanceSummary Attended(int present, int of, int absent = 0, int excused = 0) =>
        new(of, present, absent, excused);

    /// <summary>A certificate as the database would hold it. The domain offers no public way to make one.</summary>
    private static Certificate CertificateOf(long userId, long versionId, CertificateStatus status = CertificateStatus.Valid)
    {
        var certificate = (Certificate)Activator.CreateInstance(typeof(Certificate), nonPublic: true)!;
        typeof(Certificate).GetProperty(nameof(Certificate.UserId))!.SetValue(certificate, userId);
        typeof(Certificate).GetProperty(nameof(Certificate.RecipeVersionId))!.SetValue(certificate, (long?)versionId);
        typeof(Certificate).GetProperty(nameof(Certificate.Status))!.SetValue(certificate, status);
        return certificate;
    }

    private static SessionSpec Session(long trainerId = Trainer, int? sessionNo = null, int dayOffset = 1,
        params long[] moduleIds) =>
        new(sessionNo, Today.AddDays(dayOffset), new TimeOnly(9, 0), 120, "Branch 1", trainerId, moduleIds);

    private static TrainingClass NewClass(Course course) =>
        TrainingClass.Create(course, branchId: 1, "October intake", Today, Today.AddDays(30), Trainer);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- attendance arithmetic

    [Theory]
    [InlineData(5, 5, 0, 0, 100, true)]
    [InlineData(4, 5, 1, 0, 80, true)]    // exactly on the minimum
    [InlineData(3, 5, 2, 0, 60, false)]
    [InlineData(3, 5, 1, 1, 75, false)]   // one excused: 3 of the 4 that count
    [InlineData(4, 5, 0, 1, 100, true)]   // an excused session does not count against the trainee
    [InlineData(2, 3, 1, 0, 66, false)]
    [InlineData(0, 0, 0, 0, 100, true)]   // no sessions to attend
    public void Attendance_is_measured_over_the_sessions_that_count(int present, int total, int absent, int excused,
        int percent, bool meets80)
    {
        var attendance = Attended(present, total, absent, excused);

        Assert.Equal(percent, attendance.Percent);
        Assert.Equal(meets80, attendance.Meets(80));
    }

    [Fact]
    public void Trainee_is_flagged_as_soon_as_the_minimum_has_become_arithmetically_impossible()
    {
        // Five sessions, minimum 80 percent: one absence can be afforded, two cannot.
        Assert.True(Attended(present: 0, of: 5).CanStillMeet(80));
        Assert.True(Attended(present: 1, of: 5, absent: 1).CanStillMeet(80));
        Assert.False(Attended(present: 1, of: 5, absent: 2).CanStillMeet(80)); // best case is now 3 of 5
        Assert.False(Attended(present: 3, of: 5, absent: 2).CanStillMeet(80));
        // An excused session lowers the bar instead of counting as missed.
        Assert.True(Attended(present: 1, of: 5, absent: 1, excused: 1).CanStillMeet(75));
    }

    // ---------------------------------------------------------------- enrolment

    [Fact]
    public void Assignment_creates_one_progress_row_per_module_and_sets_the_deadline_from_the_regulation()
    {
        var course = PublishedCourse();

        var enrollment = Enrollment.Assign(course, Trainee, 9, Rules, Now);

        Assert.Equal(EnrollmentState.Assigned, enrollment.State);
        Assert.Equal(7, enrollment.Modules.Count);
        Assert.Equal(course.OrderedModules().Select(m => m.Id), enrollment.Modules.Select(m => m.CourseModuleId));
        Assert.All(enrollment.Modules, module => Assert.False(module.IsComplete));
        Assert.Equal(Today.AddDays(14), enrollment.DueDate);
        Assert.Equal(0, enrollment.ProgressPercent);
    }

    [Fact]
    public void Only_a_published_course_can_be_assigned()
    {
        var draft = Course.Create(CourseType.Product, "Not ready", ReleasedVersion(), Trainer);

        Assert.Equal("COURSE_NOT_PUBLISHED", Refused(() => Enrollment.Assign(draft, Trainee, null, Rules, Now)).Rule);
    }

    [Fact]
    public void First_completed_module_moves_the_enrolment_to_in_progress()
    {
        var enrollment = Assigned();
        var first = enrollment.Modules[0].CourseModuleId;

        enrollment.CompleteModule(first, Now);
        enrollment.CompleteModule(first, Now.AddHours(1)); // repeating it changes nothing

        Assert.Equal(EnrollmentState.InProgress, enrollment.State);
        Assert.Equal(14, enrollment.ProgressPercent); // 1 of 7
        Assert.Equal(Now, enrollment.Modules[0].CompletedAt);
        Assert.Equal(ErrorKind.NotFound, Refused(() => enrollment.CompleteModule(424242, Now)).Kind);
    }

    // ---------------------------------------------------------------- BR-32

    [Fact]
    public void BR_32_all_seven_modules_and_enough_attendance_make_the_enrolment_eligible()
    {
        var enrollment = WithAllModulesComplete();

        var result = enrollment.EvaluateEligibility(Attended(4, of: 5, absent: 1), Rules);

        Assert.True(result.Eligible);
        Assert.Empty(result.MissingModuleIds);
        Assert.Equal(80, result.AttendancePercent);
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);
        Assert.Equal(100, enrollment.ProgressPercent);
    }

    [Fact]
    public void BR_32_six_of_seven_modules_is_not_eligible_whatever_the_attendance()
    {
        var enrollment = Assigned();
        foreach (var module in enrollment.Modules.Take(6).ToList()) enrollment.CompleteModule(module.CourseModuleId, Now);

        var result = enrollment.EvaluateEligibility(Attended(5, of: 5), Rules);

        Assert.False(result.Eligible);
        Assert.Equal(enrollment.Modules[6].CourseModuleId, Assert.Single(result.MissingModuleIds));
        Assert.Equal(EnrollmentState.InProgress, enrollment.State);
    }

    [Fact]
    public void BR_32_all_modules_but_attendance_below_the_minimum_is_not_eligible()
    {
        var enrollment = WithAllModulesComplete();

        var result = enrollment.EvaluateEligibility(Attended(3, of: 5, absent: 2), Rules);

        Assert.False(result.Eligible);
        Assert.False(result.AttendanceMet);
        Assert.False(result.AttendanceStillReachable);
        Assert.Equal(60, result.AttendancePercent);
        Assert.Equal(EnrollmentState.InProgress, enrollment.State);
    }

    [Fact]
    public void BR_32_the_assessment_cannot_be_reached_from_in_progress()
    {
        // Everything is in place, but the eligibility checker has not passed the enrolment.
        var enrollment = WithAllModulesComplete();
        Assert.Equal(EnrollmentState.InProgress, enrollment.State);

        var refusal = Refused(() => enrollment.EnsureEligibleForAssessment(Attended(5, of: 5), Rules));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-32", refusal.Rule);
        Assert.Contains(refusal.Details, d => d.Field == "state");
    }

    [Fact]
    public void BR_32_the_gate_checks_again_and_does_not_trust_the_state_alone()
    {
        var enrollment = WithAllModulesComplete();
        enrollment.EvaluateEligibility(Attended(4, of: 5, absent: 1), Rules);
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);
        enrollment.EnsureEligibleForAssessment(Attended(4, of: 5, absent: 1), Rules); // passes

        // An attendance entry is later corrected from PRESENT to ABSENT.
        var refusal = Refused(() => enrollment.EnsureEligibleForAssessment(Attended(3, of: 5, absent: 2), Rules));

        Assert.Equal("BR-32", refusal.Rule);
        Assert.Contains(refusal.Details, d => d.Field == "attendance");
    }

    [Fact]
    public void Enrolment_outside_a_class_has_no_attendance_to_meet()
    {
        var enrollment = WithAllModulesComplete();

        Assert.True(enrollment.EvaluateEligibility(AttendanceSummary.NoSessions, Rules).Eligible);
    }

    // ---------------------------------------------------------------- the enrolment state model

    [Fact]
    public void Enrolment_state_model_is_exactly_the_table_of_the_data_dictionary()
    {
        var states = Enum.GetValues<EnrollmentState>();
        var allowed = (from origin in states from target in states where Enrollment.IsAllowed(origin, target) select (origin, target))
            .ToHashSet();

        Assert.Equal(new HashSet<(EnrollmentState, EnrollmentState)>
        {
            (EnrollmentState.Assigned, EnrollmentState.InProgress),
            (EnrollmentState.InProgress, EnrollmentState.Eligible),
            (EnrollmentState.Eligible, EnrollmentState.Passed),
            (EnrollmentState.Eligible, EnrollmentState.Locked),
            (EnrollmentState.Locked, EnrollmentState.Assigned),
            (EnrollmentState.Assigned, EnrollmentState.Closed),
            (EnrollmentState.InProgress, EnrollmentState.Closed),
            (EnrollmentState.Eligible, EnrollmentState.Closed),
        }, allowed);
    }

    [Fact]
    public void Transition_that_is_not_in_the_table_is_rejected()
    {
        var closed = Assigned();
        closed.Close();

        Assert.Equal(Enrollment.StateRule, Refused(closed.Close).Rule);                                   // CLOSED -> CLOSED
        Assert.Equal(Enrollment.StateRule, Refused(closed.ResetForRetake).Rule);                          // CLOSED -> ASSIGNED
        Assert.Equal(Enrollment.StateRule, Refused(() => closed.CompleteModule(closed.Modules[0].CourseModuleId, Now)).Rule);
        Assert.Equal(EnrollmentState.Closed, closed.State);

        var assigned = Assigned();
        Assert.Equal(Enrollment.StateRule, Refused(assigned.ResetForRetake).Rule);                        // ASSIGNED -> ASSIGNED
        // ASSIGNED cannot jump to ELIGIBLE: the checker only promotes IN_PROGRESS, and with no module done it reports false.
        Assert.False(assigned.EvaluateEligibility(Attended(5, of: 5), Rules).Eligible);
        Assert.Equal(EnrollmentState.Assigned, assigned.State);
    }

    // ---------------------------------------------------------------- regulation

    private static TrainingRegulation Regulation(long id, DateOnly effectiveFrom, int maxRetakes = 2,
        int minAttendance = 80, CourseType type = CourseType.Product) =>
        WithId(TrainingRegulation.Create(type, RoleName.Trainee, null, 14, maxRetakes, minAttendance, effectiveFrom, 8), id);

    [Fact]
    public void Enrolment_keeps_the_rule_it_was_created_under()
    {
        var original = Regulation(1, new DateOnly(2026, 1, 1), maxRetakes: 2, minAttendance: 80);
        var stricter = Regulation(2, new DateOnly(2026, 11, 1), maxRetakes: 0, minAttendance: 100);
        TrainingRegulation[] regulations = [original, stricter];

        var inFlight = TrainingRegulation.Resolve(regulations, CourseType.Product, new DateOnly(2026, 10, 8));
        var onTheDay = TrainingRegulation.Resolve(regulations, CourseType.Product, new DateOnly(2026, 11, 1));
        var afterwards = TrainingRegulation.Resolve(regulations, CourseType.Product, new DateOnly(2026, 12, 24));

        // Created before the change takes effect: still two retakes, still 80 percent.
        Assert.Equal(new TrainingRules(1, 14, 2, 80), inFlight);
        // Created on or after it: the new rule.
        Assert.Equal(new TrainingRules(2, 14, 0, 100), onTheDay);
        Assert.Equal(2, afterwards.RegulationId);
    }

    [Fact]
    public void Regulation_of_another_course_type_never_applies_and_the_defaults_fill_the_gap()
    {
        TrainingRegulation[] regulations = [Regulation(1, new DateOnly(2026, 1, 1), type: CourseType.Induction)];

        Assert.Equal(TrainingRules.Default, TrainingRegulation.Resolve(regulations, CourseType.Product, Today));
        Assert.Equal(TrainingRules.Default, TrainingRegulation.Resolve(regulations, CourseType.Induction, new DateOnly(2025, 12, 31)));
    }

    [Fact]
    public void Regulation_may_not_reach_back_to_enrolments_that_already_exist()
    {
        var lastEnrolment = new DateOnly(2026, 10, 8);

        var refusal = Refused(() => TrainingRegulation.EnsureNotRetroactive(lastEnrolment, lastEnrolment));
        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal(TrainingRegulation.RetroactiveRule, refusal.Rule);
        Refused(() => TrainingRegulation.EnsureNotRetroactive(new DateOnly(2026, 1, 1), lastEnrolment));

        TrainingRegulation.EnsureNotRetroactive(lastEnrolment.AddDays(1), lastEnrolment); // from tomorrow: fine
        TrainingRegulation.EnsureNotRetroactive(new DateOnly(2020, 1, 1), null);          // nothing to reach back to
    }

    [Theory]
    [InlineData(0, 2, 80, "dueDays")]
    [InlineData(14, -1, 80, "maxRetakes")]
    [InlineData(14, 2, 101, "minAttendancePct")]
    public void Regulation_values_are_validated(int dueDays, int maxRetakes, int minAttendance, string field)
    {
        var refusal = Refused(() => TrainingRegulation.Create(CourseType.Product, null, null, dueDays, maxRetakes,
            minAttendance, Today, 8));

        Assert.Contains(refusal.Details, d => d.Field == field);
    }

    // ---------------------------------------------------------------- classes, BR-34

    [Fact]
    public void BR_34_a_trainer_without_a_certificate_on_the_bound_version_cannot_be_scheduled()
    {
        var version = ReleasedVersion(versionId: 310);
        var course = PublishedCourse(version);
        var trainingClass = NewClass(course);

        var refusal = Refused(() => trainingClass.AddSession(Session(), course, []));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-34", refusal.Rule);
        Assert.Contains(refusal.Details, d => d.Field == "trainerId");
        Assert.Empty(trainingClass.Sessions);
    }

    [Fact]
    public void BR_34_a_certificate_on_another_version_or_one_that_needs_recertification_does_not_qualify()
    {
        var version = ReleasedVersion(versionId: 310);
        var course = PublishedCourse(version);
        var trainingClass = NewClass(course);

        Certificate[][] insufficient =
        [
            [CertificateOf(Trainer, versionId: 999)],                                   // another version
            [CertificateOf(Trainer, version.Id, CertificateStatus.NeedsRecert)],        // flagged after a change
            [CertificateOf(Trainer, version.Id, CertificateStatus.Superseded)],
            [CertificateOf(userId: 777, version.Id)],                                   // someone else's
        ];

        Assert.All(insufficient, certificates =>
            Assert.Equal("BR-34", Refused(() => trainingClass.AddSession(Session(), course, certificates)).Rule));
    }

    [Fact]
    public void Certified_trainer_is_scheduled_and_sessions_are_numbered_in_order()
    {
        var version = ReleasedVersion(versionId: 310);
        var course = PublishedCourse(version);
        var trainingClass = NewClass(course);
        Certificate[] certified = [CertificateOf(Trainer, version.Id)];
        var modules = course.OrderedModules();

        var first = trainingClass.AddSession(Session(moduleIds: [modules[0].Id, modules[1].Id]), course, certified);
        var second = trainingClass.AddSession(Session(dayOffset: 2), course, certified);

        Assert.Equal(1, first.SessionNo);
        Assert.Equal(2, second.SessionNo);
        Assert.Equal([modules[0].Id, modules[1].Id], first.Modules.Select(m => m.CourseModuleId));
        Assert.Equal(Trainer, first.TrainerId);
    }

    [Fact]
    public void Course_that_is_not_built_from_a_recipe_needs_no_certificate_to_teach()
    {
        var induction = PublishedCourse(type: CourseType.Induction, courseId: 30);
        var trainingClass = NewClass(induction);

        trainingClass.AddSession(Session(), induction, []);

        Assert.Single(trainingClass.Sessions);
    }

    [Fact]
    public void Session_must_fall_within_the_class_and_cover_modules_of_its_course()
    {
        var version = ReleasedVersion(versionId: 310);
        var course = PublishedCourse(version);
        var trainingClass = NewClass(course);
        Certificate[] certified = [CertificateOf(Trainer, version.Id)];
        trainingClass.AddSession(Session(sessionNo: 1), course, certified);

        Assert.Contains(Refused(() => trainingClass.AddSession(Session(dayOffset: 45), course, certified)).Details,
            d => d.Field == "scheduledDate");
        Assert.Contains(Refused(() => trainingClass.AddSession(Session(moduleIds: [424242]), course, certified)).Details,
            d => d.Field == "moduleIds");
        Assert.Contains(Refused(() => trainingClass.AddSession(Session(sessionNo: 1), course, certified)).Details,
            d => d.Field == "sessionNo");
    }

    [Fact]
    public void Class_can_only_be_scheduled_for_a_published_course()
    {
        var draft = Course.Create(CourseType.Product, "Not ready", ReleasedVersion(), Trainer);

        Assert.Equal("COURSE_NOT_PUBLISHED", Refused(() => NewClass(draft)).Rule);
    }

    [Fact]
    public void Attendance_is_only_open_while_the_class_is_running()
    {
        var trainingClass = NewClass(PublishedCourse());

        Assert.Equal(TrainingClass.StateRule, Refused(trainingClass.EnsureAttendanceOpen).Rule); // PLANNED
        trainingClass.Open();
        trainingClass.EnsureAttendanceOpen();
        trainingClass.Close();
        Assert.Equal(TrainingClass.StateRule, Refused(trainingClass.EnsureAttendanceOpen).Rule); // CLOSED: frozen
    }

    [Fact]
    public void Class_follows_its_state_model()
    {
        var trainingClass = NewClass(PublishedCourse());

        Assert.Equal(TrainingClass.StateRule, Refused(trainingClass.Close).Rule);   // PLANNED -> CLOSED does not exist
        trainingClass.Open();
        Assert.Equal(ClassState.Running, trainingClass.State);
        Assert.Equal(TrainingClass.StateRule, Refused(trainingClass.Open).Rule);
        Assert.Equal(TrainingClass.StateRule, Refused(() => trainingClass.Update("Renamed", Today, Today)).Rule);
        trainingClass.Close();
        Assert.Equal(TrainingClass.StateRule, Refused(trainingClass.Cancel).Rule);  // CLOSED is final
    }

    [Fact]
    public void Attendance_entry_reports_whether_a_correction_changed_anything()
    {
        var entry = new Attendance(sessionId: 44, enrollmentId: 5, AttendanceStatus.Absent, null, Trainer, Now);

        Assert.False(entry.Correct(AttendanceStatus.Absent, " ", Trainer, Now));
        Assert.True(entry.Correct(AttendanceStatus.Excused, "Doctor's note", Trainer, Now.AddHours(1)));
        Assert.Equal(AttendanceStatus.Excused, entry.Status);
        Assert.Equal("Doctor's note", entry.Note);
        Assert.Equal(Now.AddHours(1), entry.RecordedAt);
    }
}
