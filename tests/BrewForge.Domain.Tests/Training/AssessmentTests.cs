using System.Reflection;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Training;

/// <summary>UC-15 to UC-17: BR-13, BR-14, BR-17, BR-21, BR-32 and BR-33.</summary>
public sealed class AssessmentTests
{
    private const long Trainer = 6;
    private const long Trainee = 61;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly AttendanceSummary Attended = AttendanceSummary.NoSessions;

    private static TrainingRules RulesWith(int maxRetakes) => new(3, DueDays: 14, MaxRetakes: maxRetakes, MinAttendancePct: 80);

    private static readonly TrainingRules Rules = RulesWith(2);

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A PUBLISHED course on the given version whose quiz has five questions,
    /// three on SOP (ids 1-3) and two on TECHNIQUE (ids 4-5), every correct
    /// answer being "A". The pass mark is 80: four of five.
    /// </summary>
    private static Course PublishedCourse(RecipeVersion? version = null, long courseId = 20)
    {
        var course = Course.Create(CourseType.Product, "Oolong milk tea", version ?? ReleasedVersion(), Trainer);
        WithId(course, courseId);
        foreach (var module in course.Modules) WithId(module, courseId * 10 + module.ModuleOrder);
        foreach (var module in course.Modules.Where(m => m.Source != ModuleSource.Generated))
        {
            foreach (var lesson in module.Lessons.ToList()) course.UpdateLesson(module, lesson, null, "Prose", null);
            if (module.Lessons.Count == 0) course.AddLesson(module, "Lesson", "Content", null);
            course.SetModuleDuration(module, 10);
        }

        var sop = course.Modules.Single(m => m.ModuleType == ModuleType.Sop).Id;
        var technique = course.Modules.Single(m => m.ModuleType == ModuleType.Technique).Id;
        QuizOption[] options = [new("A", "Right"), new("B", "Wrong")];
        long id = 1;
        foreach (var moduleId in new[] { sop, sop, sop, technique, technique })
        {
            WithId(course.AddQuizQuestion(moduleId, $"Question {id}?", options, "A"), id++);
        }
        WithId(course.Quiz, courseId * 100);
        course.Submit();
        course.Approve(8, Now);
        return course;
    }

    private static Enrollment Eligible(Course course, long userId = Trainee)
    {
        var enrollment = Enrollment.Assign(course, userId, null, Rules, Now);
        StudyEverything(enrollment);
        enrollment.EvaluateEligibility(Attended, Rules);
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);
        return enrollment;
    }

    private static void StudyEverything(Enrollment enrollment)
    {
        foreach (var module in enrollment.Modules.ToList()) enrollment.CompleteModule(module.CourseModuleId, Now);
    }

    /// <summary>Answers "A" (right) for the first questions and "B" (wrong) for the rest.</summary>
    private static Dictionary<long, string?> Answers(int correct) =>
        Enumerable.Range(1, 5).ToDictionary(i => (long)i, i => (string?)(i <= correct ? "A" : "B"));

    private static IReadOnlyCollection<long> Checklist(Course course) =>
        [.. course.Modules.Single(m => m.ModuleType == ModuleType.Technique).Lessons
            .Where(l => l.RecipeStepId is not null).Select(l => l.RecipeStepId!.Value)];

    private static ChecklistMark[] Marks(Course course, bool passed = true) =>
        [.. Checklist(course).Select(stepId => new ChecklistMark(stepId, passed, null))];

    /// <summary>A recording of the practical, uploaded by the trainer who ran it.</summary>
    private static PracticalVideo Video(Enrollment enrollment, Course course, long uploader = Trainer, bool runsThePractical = true) =>
        enrollment.AddPracticalVideo(course, uploader, runsThePractical, "practical.mp4", "video/mp4", 4096,
            new string('a', 64), $"test/{Guid.NewGuid():N}.mp4", Now);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    // ---------------------------------------------------------------- BR-32: the gate

    [Fact]
    public void BR_32_the_quiz_cannot_be_attempted_before_the_enrolment_is_eligible()
    {
        var course = PublishedCourse();
        var assigned = Enrollment.Assign(course, Trainee, null, Rules, Now);
        var inProgress = Enrollment.Assign(course, Trainee, null, Rules, Now);
        StudyEverything(inProgress); // everything done, but the checker has not passed it

        Assert.All(new[] { assigned, inProgress }, enrollment =>
        {
            var refusal = Refused(() => enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now));
            Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
            Assert.Equal("BR-32", refusal.Rule);
            Assert.Empty(enrollment.QuizAttempts);
        });
    }

    // ---------------------------------------------------------------- UC-15: scoring

    [Fact]
    public void Passing_attempt_is_scored_against_the_pass_mark_with_a_breakdown_per_module()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var result = enrollment.AttemptQuiz(course, Answers(correct: 4), Attended, Rules, Now);

        Assert.Equal(1, result.Attempt.AttemptNo);
        Assert.Equal(80, result.Attempt.Score);
        Assert.True(result.Attempt.Passed);
        Assert.Equal(80, result.PassScore);
        Assert.Equal([(ModuleType.Sop, 3, 3), (ModuleType.Technique, 1, 2)],
            result.PerModule.Select(m => (m.ModuleType, m.Correct, m.Total)));
        // A passed quiz resets nothing.
        Assert.All(enrollment.Modules, module => Assert.True(module.IsComplete));
        Assert.True(enrollment.HasPassedQuiz);
    }

    [Fact]
    public void Failing_attempt_says_which_module_was_failed_and_sends_the_trainee_back_to_it()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        var sop = course.Modules.Single(m => m.ModuleType == ModuleType.Sop).Id;
        var technique = course.Modules.Single(m => m.ModuleType == ModuleType.Technique).Id;

        var result = enrollment.AttemptQuiz(course, Answers(correct: 3), Attended, Rules, Now);

        Assert.Equal(60, result.Attempt.Score);
        Assert.False(result.Attempt.Passed);
        Assert.Equal(2, result.RetakesLeft);
        var failed = Assert.Single(result.PerModule, m => m.Failed);
        Assert.Equal((ModuleType.Technique, 0, 2), (failed.ModuleType, failed.Correct, failed.Total));

        // Only the failed module has to be studied again.
        Assert.False(enrollment.Modules.Single(m => m.CourseModuleId == technique).IsComplete);
        Assert.True(enrollment.Modules.Single(m => m.CourseModuleId == sop).IsComplete);
        Assert.Equal(85, enrollment.ProgressPercent); // 6 of 7
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);
    }

    [Fact]
    public void Retake_is_refused_until_the_failed_module_has_been_studied_again()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(correct: 3), Attended, Rules, Now);

        Assert.Equal("BR-32", Refused(() => enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now)).Rule);

        StudyEverything(enrollment);
        var second = enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now.AddHours(1));
        Assert.Equal(2, second.Attempt.AttemptNo);
        Assert.True(second.Attempt.Passed);
    }

    [Fact]
    public void Unanswered_question_is_a_wrong_answer_and_an_unknown_question_is_refused()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var refusal = Refused(() => enrollment.AttemptQuiz(course, new Dictionary<long, string?> { [999] = "A" }, Attended, Rules, Now));
        Assert.Contains(refusal.Details, d => d.Field == "answers");

        var result = enrollment.AttemptQuiz(course, new Dictionary<long, string?> { [1] = "A" }, Attended, Rules, Now);
        Assert.Equal(20, result.Attempt.Score); // one of five
    }

    [Fact]
    public void Quiz_that_was_passed_is_not_attempted_again()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now);

        Assert.Equal("QUIZ_ALREADY_PASSED", Refused(() => enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now)).Rule);
    }

    // ---------------------------------------------------------------- BR-33: the retake limit

    [Fact]
    public void BR_33_exhausting_the_retakes_locks_the_enrolment()
    {
        // One retake allowed: the attempt result of the API contract (attempt 2, 0 left, LOCKED).
        var rules = RulesWith(maxRetakes: 1);
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var first = enrollment.AttemptQuiz(course, Answers(3), Attended, rules, Now);
        Assert.Equal(1, first.RetakesLeft);
        Assert.Equal(EnrollmentState.Eligible, enrollment.State);

        StudyEverything(enrollment);
        var second = enrollment.AttemptQuiz(course, Answers(3), Attended, rules, Now.AddHours(1));

        Assert.Equal(2, second.Attempt.AttemptNo);
        Assert.False(second.Attempt.Passed);
        Assert.Equal(0, second.RetakesLeft);
        Assert.Equal(EnrollmentState.Locked, enrollment.State);
    }

    [Fact]
    public void BR_33_an_attempt_beyond_the_limit_is_refused()
    {
        var rules = RulesWith(maxRetakes: 1);
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(3), Attended, rules, Now);
        StudyEverything(enrollment);
        enrollment.AttemptQuiz(course, Answers(3), Attended, rules, Now.AddHours(1));
        StudyEverything2(enrollment); // even with everything studied again...

        var refusal = Refused(() => enrollment.AttemptQuiz(course, Answers(5), Attended, rules, Now.AddHours(2)));

        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal("BR-33", refusal.Rule);
        Assert.Equal(2, enrollment.QuizAttempts.Count); // ...no third attempt was recorded

        static void StudyEverything2(Enrollment locked)
        {
            // A LOCKED enrolment does not accept module completions either.
            Assert.Equal(Enrollment.StateRule,
                Assert.Throws<DomainException>(() => locked.CompleteModule(locked.Modules[0].CourseModuleId, Now)).Rule);
        }
    }

    [Fact]
    public void BR_33_with_no_retakes_the_first_failure_locks()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var result = enrollment.AttemptQuiz(course, Answers(0), Attended, RulesWith(maxRetakes: 0), Now);

        Assert.Equal(0, result.RetakesLeft);
        Assert.Equal(EnrollmentState.Locked, enrollment.State);
    }

    [Fact]
    public void Reset_by_the_training_manager_starts_the_course_and_the_retake_count_over()
    {
        var rules = RulesWith(maxRetakes: 0);
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(0), Attended, rules, Now);
        var later = Now.AddDays(3);

        enrollment.ResetForRetake(rules, later);

        Assert.Equal(EnrollmentState.Assigned, enrollment.State);
        Assert.Equal(0, enrollment.ProgressPercent);
        Assert.Equal(DateOnly.FromDateTime(later.UtcDateTime).AddDays(14), enrollment.DueDate);
        Assert.Empty(enrollment.CurrentAttempts());
        Assert.Single(enrollment.QuizAttempts); // the history is kept

        // From the start again: study, become eligible, and the attempt is allowed and numbered on.
        StudyEverything(enrollment);
        enrollment.EvaluateEligibility(Attended, rules);
        var again = enrollment.AttemptQuiz(course, Answers(5), Attended, rules, later.AddHours(1));
        Assert.Equal(2, again.Attempt.AttemptNo);
        Assert.True(again.Attempt.Passed);
    }

    // ---------------------------------------------------------------- UC-16: BR-14, BR-17

    [Fact]
    public void BR_14_a_trainee_can_never_evaluate_themselves()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var refusal = Refused(() => enrollment.EvaluatePractical(course, evaluatorId: Trainee, Marks(course), Checklist(course), Video(enrollment, course), Now));

        Assert.Equal(ErrorKind.Forbidden, refusal.Kind);
        Assert.Equal("BR-14", refusal.Rule);
        Assert.Empty(enrollment.PracticalEvaluations);
    }

    [Fact]
    public void Practical_is_passed_only_when_every_item_of_the_checklist_passed()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        var oneFailed = Marks(course);
        oneFailed[1] = oneFailed[1] with { Passed = false, Note = "Poured too fast" };

        Assert.False(enrollment.EvaluatePractical(course, Trainer, oneFailed, Checklist(course), Video(enrollment, course), Now).Passed);
        Assert.True(enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(enrollment, course), Now.AddHours(1)).Passed);
        Assert.Equal(2, enrollment.PracticalEvaluations.Count);
        Assert.True(enrollment.LatestPractical()!.Passed);
    }

    [Fact]
    public void Evaluation_must_mark_exactly_the_items_of_the_checklist()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        var checklist = Checklist(course);

        var missing = Refused(() => enrollment.EvaluatePractical(course, Trainer, [Marks(course)[0]], checklist, Video(enrollment, course), Now));
        var foreign = Refused(() => enrollment.EvaluatePractical(course, Trainer, [.. Marks(course), new ChecklistMark(424242, true, null)], checklist, Video(enrollment, course), Now));

        Assert.All(new[] { missing, foreign }, refusal => Assert.Contains(refusal.Details, d => d.Field == "items"));
        Assert.Empty(enrollment.PracticalEvaluations);
    }

    [Fact]
    public void Practical_evaluation_opens_only_once_the_enrolment_is_eligible()
    {
        var course = PublishedCourse();
        var assigned = Enrollment.Assign(course, Trainee, null, Rules, Now);

        Assert.Equal("BR-32", Refused(() => assigned.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(assigned, course), Now)).Rule);
    }

    // ---------------------------------------------------------------- the recording of the practical

    [Fact]
    public void Practical_evaluation_is_refused_without_the_recording_of_the_practical()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var refusal = Refused(() => enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), video: null, Now));

        Assert.Equal((ErrorKind.RuleViolation, PracticalVideo.Rule), (refusal.Kind, refusal.Rule));
        Assert.Contains(refusal.Details, d => d.Field == "practicalVideoId");
        Assert.Empty(enrollment.PracticalEvaluations);
    }

    [Fact]
    public void BR_14_a_trainee_can_never_upload_the_recording_of_their_own_practical()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var refusal = Refused(() => Video(enrollment, course, uploader: Trainee));

        Assert.Equal((ErrorKind.Forbidden, "BR-14"), (refusal.Kind, refusal.Rule));
        Assert.Empty(enrollment.PracticalVideos);
    }

    [Fact]
    public void Recording_is_uploaded_only_by_the_trainer_who_runs_the_practical()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);

        var refusal = Refused(() => Video(enrollment, course, uploader: 77, runsThePractical: false));

        Assert.Equal((ErrorKind.Forbidden, PracticalVideo.Rule), (refusal.Kind, refusal.Rule));
        Assert.Empty(enrollment.PracticalVideos);
    }

    [Fact]
    public void Practical_is_evaluated_by_the_trainer_who_uploaded_its_recording()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        var video = Video(enrollment, course, uploader: 77);

        var refusal = Refused(() => enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), video, Now));

        Assert.Equal((ErrorKind.Forbidden, PracticalVideo.Rule), (refusal.Kind, refusal.Rule));
        Assert.Same(video, enrollment.EvaluatePractical(course, 77, Marks(course), Checklist(course), video, Now).Video);
    }

    [Fact]
    public void Recording_is_the_evidence_of_one_evaluation_and_of_this_enrolment_only()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        var other = Eligible(course, userId: 62);
        var video = Video(enrollment, course);
        enrollment.EvaluatePractical(course, Trainer, Marks(course, passed: false), Checklist(course), video, Now);

        var again = Refused(() => enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), video, Now.AddHours(1)));
        var foreign = Refused(() => other.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), video, Now));

        Assert.All(new[] { again, foreign }, refusal => Assert.Equal((ErrorKind.RuleViolation, PracticalVideo.Rule), (refusal.Kind, refusal.Rule)));
        Assert.Single(enrollment.PracticalEvaluations);
        Assert.Empty(other.PracticalEvaluations);
    }

    [Theory]
    [InlineData("practical.mp4", "video/mp4")]
    [InlineData("Practical.MOV", "video/quicktime")]
    [InlineData("b01 trainee.webm", "video/webm")]
    public void Recording_is_a_video_file_named_as_one(string fileName, string contentType) =>
        Assert.Equal(contentType, PracticalVideo.Format(fileName).ContentType);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("practical")]
    [InlineData("practical.exe")]
    [InlineData("practical.mp4.txt")]
    public void File_that_is_not_named_as_a_video_is_refused(string? fileName) =>
        Assert.Contains(Refused(() => PracticalVideo.Format(fileName)).Details, d => d.Field == "file");

    [Fact]
    public void Recording_is_neither_empty_nor_larger_than_the_limit()
    {
        PracticalVideo.EnsureSize(1);
        PracticalVideo.EnsureSize(PracticalVideo.MaxBytes);

        Assert.Equal(ErrorKind.Validation, Refused(() => PracticalVideo.EnsureSize(0)).Kind);
        Assert.Equal(ErrorKind.Validation, Refused(() => PracticalVideo.EnsureSize(PracticalVideo.MaxBytes + 1)).Kind);
    }

    [Fact]
    public void BR_17_a_practical_evaluation_holds_an_observation_and_nothing_a_machine_scored()
    {
        // No score and nothing a machine judged: a pass flag per checklist item, set by a person,
        // and the recording that person watched.
        var evaluation = typeof(PracticalEvaluation).GetProperties().Select(p => p.Name).Order();
        var mark = typeof(ChecklistMark).GetProperties().Select(p => p.Name).Order();

        Assert.Equal(["ChecklistJson", "EnrollmentId", "EvaluatedAt", "EvaluatedBy", "Id", "Passed", "PracticalVideoId", "Video"], evaluation);
        Assert.Equal(["Note", "Passed", "RecipeStepId"], mark);
    }

    // ---------------------------------------------------------------- UC-17: BR-13, BR-21

    [Fact]
    public void BR_21_the_certificate_is_issued_only_when_quiz_and_practical_have_both_passed()
    {
        var course = PublishedCourse();

        var quizOnly = Eligible(course);
        quizOnly.AttemptQuiz(course, Answers(5), Attended, Rules, Now);
        Assert.Null(quizOnly.TryCertify(course, [], Now));
        Assert.Equal(EnrollmentState.Eligible, quizOnly.State);

        var practicalOnly = Eligible(course);
        practicalOnly.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(practicalOnly, course), Now);
        Assert.Null(practicalOnly.TryCertify(course, [], Now));

        var practicalFailed = Eligible(course);
        practicalFailed.AttemptQuiz(course, Answers(5), Attended, Rules, Now);
        practicalFailed.EvaluatePractical(course, Trainer, Marks(course, passed: false), Checklist(course), Video(practicalFailed, course), Now);
        Assert.Null(practicalFailed.TryCertify(course, [], Now));

        // The practical is observed again and passes: now all three conditions hold.
        practicalFailed.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(practicalFailed, course), Now.AddHours(1));
        Assert.NotNull(practicalFailed.TryCertify(course, [], Now.AddHours(1)));
    }

    [Fact]
    public void BR_13_the_certificate_is_bound_to_the_recipe_version_of_the_course()
    {
        var version = ReleasedVersion(versionId: 310);
        var course = PublishedCourse(version);
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now);
        enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(enrollment, course), Now);
        var issuedAt = Now.AddMinutes(5);

        var certificate = enrollment.TryCertify(course, [], issuedAt)!;

        Assert.Equal(Trainee, certificate.UserId);
        Assert.Equal(course.Id, certificate.CourseId);
        Assert.Equal(310, certificate.RecipeVersionId);
        Assert.Equal(CertificateStatus.Valid, certificate.Status);
        Assert.Equal(issuedAt, certificate.IssuedAt);
        Assert.True(certificate.Certifies(Trainee, 310));
        Assert.False(certificate.Certifies(Trainee, 311));
        Assert.Equal(EnrollmentState.Passed, enrollment.State);
        Assert.Equal(issuedAt, enrollment.CompletedAt);
    }

    [Fact]
    public void Latest_practical_verdict_is_the_one_that_counts()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now);
        enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(enrollment, course), Now);
        enrollment.EvaluatePractical(course, Trainer, Marks(course, passed: false), Checklist(course), Video(enrollment, course), Now.AddHours(1));

        Assert.Null(enrollment.TryCertify(course, [], Now.AddHours(2)));
    }

    [Fact]
    public void A_certificate_cannot_be_created_any_other_way()
    {
        // No public constructor...
        Assert.Empty(typeof(Certificate).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // ...and in the whole domain exactly one public member hands out a
        // certificate: the enrolment, when its three conditions are met.
        var producers = typeof(Certificate).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && method.ReturnType == typeof(Certificate))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}");

        Assert.Equal(["Enrollment.TryCertify"], producers);

        // The internal constructor is out of reach of every other assembly:
        // the domain shares its internals with nobody.
        Assert.Empty(typeof(Certificate).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>());
    }

    [Fact]
    public void New_certificate_supersedes_the_one_held_on_an_older_version_and_keeps_it()
    {
        var oldVersion = ReleasedVersion(versionId: 310, versionNo: 1);
        var newVersion = ReleasedVersion(versionId: 320, versionNo: 2);

        // First certified on the old version...
        var oldCourse = PublishedCourse(oldVersion, courseId: 20);
        var first = Eligible(oldCourse);
        first.AttemptQuiz(oldCourse, Answers(5), Attended, Rules, Now);
        first.EvaluatePractical(oldCourse, Trainer, Marks(oldCourse), Checklist(oldCourse), Video(first, oldCourse), Now);
        var old = first.TryCertify(oldCourse, [], Now)!;
        old.FlagForRecertification(); // the version was superseded (BR-15)

        // ...then on the same course rebuilt on the new one.
        var rebuilt = PublishedCourse(newVersion, courseId: 20);
        var second = Eligible(rebuilt);
        second.AttemptQuiz(rebuilt, Answers(5), Attended, Rules, Now.AddDays(1));
        second.EvaluatePractical(rebuilt, Trainer, Marks(rebuilt), Checklist(rebuilt), Video(second, rebuilt), Now.AddDays(1));

        var current = second.TryCertify(rebuilt, [old], Now.AddDays(1))!;

        Assert.NotSame(old, current);
        Assert.Equal(320, current.RecipeVersionId);
        Assert.Equal(CertificateStatus.Valid, current.Status);
        Assert.Equal(CertificateStatus.Superseded, old.Status); // superseded, not deleted
        Assert.Same(current, old.Successor);
        Assert.Equal(310, old.RecipeVersionId);
    }

    [Fact]
    public void Assessment_is_refused_while_the_course_is_out_of_date()
    {
        var course = PublishedCourse();
        var enrollment = Eligible(course);
        course.MarkOutOfDate();

        var quiz = Refused(() => enrollment.AttemptQuiz(course, Answers(5), Attended, Rules, Now));
        var practical = Refused(() => enrollment.EvaluatePractical(course, Trainer, Marks(course), Checklist(course), Video(enrollment, course), Now));

        Assert.Equal(("BR-15", "MSG-W05"), (quiz.Rule, quiz.Code));
        Assert.Equal(("BR-15", "MSG-W02"), (practical.Rule, practical.Code));
    }

    // ---------------------------------------------------------------- the readiness count

    [Fact]
    public void Launch_status_becomes_ready_when_the_certified_count_reaches_the_threshold()
    {
        var status = BranchLaunchStatus.Plan(branchId: 1, recipeId: 12, recipeVersionId: 310, minCertifiedStaff: 2);

        status.RecomputeCoverage(1);
        Assert.Equal((LaunchStatus.Preparing, 1, false), (status.Status, status.CertifiedCount, status.CoverageMet));

        status.RecomputeCoverage(2);
        Assert.Equal((LaunchStatus.Ready, 2, true), (status.Status, status.CertifiedCount, status.CoverageMet));
    }
}
