using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Impact;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Impact;

/// <summary>UC-19: the reverse dependency graph, the what-if and the commit (BR-15); and UC-31.</summary>
public sealed class ImpactTests
{
    private const long RecipeId = 12;
    private const long Trainer = 6;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.Parse("7f3c0000-0000-0000-0000-000000000001");

    // ---------------------------------------------------------------- fixtures

    /// <summary>A released version that uses the oolong and the brewer, and no milk.</summary>
    private static RecipeVersion PlainTea(long versionId, long recipeId = 77)
    {
        var version = RecipeVersion.CreateDraft(recipeId, 1, Author);
        version.ReplaceContent([Step(1, "Brew the oolong", "TEA_BREWER", 480, [(Oolong, 18m, "g")])], Author);
        WithId(version, versionId);
        Release(version, null);
        return version;
    }

    private static void Release(RecipeVersion candidate, RecipeVersion? currentlyReleased)
    {
        var report = candidate.Validate(Catalog());
        if (candidate.State == VersionState.Draft) candidate.Submit(report);
        var release = RecipeRelease.Prepare(candidate, currentlyReleased, report, Author + 100,
            Math.Max(candidate.VersionNo, currentlyReleased?.VersionNo ?? 0), Now);
        release.SupersedePrevious();
        release.Seal();
    }

    /// <summary>Releases a second version of the recipe, which supersedes the given one.</summary>
    private static RecipeVersion Supersede(RecipeVersion released)
    {
        var next = RecipeVersion.CreateDraft(released.RecipeId, released.VersionNo + 1, Author);
        next.ReplaceContent([Step(1, "Brew a lighter oolong", "TEA_BREWER", 420, [(Oolong, 16m, "g")])], Author);
        WithId(next, released.Id + 10);
        Release(next, released);
        return next;
    }

    private static Course PublishedCourse(RecipeVersion version, long courseId)
    {
        var course = WithId(Course.Create(CourseType.Product, $"Course {courseId}", version, Trainer), courseId);
        foreach (var module in course.Modules) WithId(module, courseId * 10 + module.ModuleOrder);
        foreach (var module in course.Modules.Where(m => m.Source != ModuleSource.Generated))
        {
            foreach (var lesson in module.Lessons.ToList()) course.UpdateLesson(module, lesson, null, "Prose", null);
            if (module.Lessons.Count == 0) course.AddLesson(module, "Lesson", "Content", null);
            course.SetModuleDuration(module, 10);
        }
        QuizOption[] options = [new("A", "Right"), new("B", "Wrong")];
        var questionId = courseId * 1000;
        foreach (var module in course.Modules) WithId(course.AddQuizQuestion(module.Id, "Which?", options, "A"), questionId++);
        WithId(course.Quiz, courseId * 100);
        course.Submit();
        course.Approve(8, Now);
        return course;
    }

    /// <summary>A certificate earned the only way one can be: through the course.</summary>
    private static Certificate Certified(Course course, long userId, long certificateId)
    {
        var rules = TrainingRules.Default;
        var enrollment = Enrollment.Assign(course, userId, null, rules, Now);
        foreach (var module in enrollment.Modules.ToList()) enrollment.CompleteModule(module.CourseModuleId, Now);
        enrollment.EvaluateEligibility(AttendanceSummary.NoSessions, rules);
        enrollment.AttemptQuiz(course, course.Quiz.Questions.ToDictionary(q => q.Id, _ => (string?)"A"),
            AttendanceSummary.NoSessions, rules, Now);
        long[] checklist =
        [
            .. course.Modules.Single(m => m.ModuleType == ModuleType.Technique).Lessons
                .Where(l => l.RecipeStepId is not null).Select(l => l.RecipeStepId!.Value),
        ];
        enrollment.EvaluatePractical(course, Trainer, [.. checklist.Select(step => new ChecklistMark(step, true, null))], checklist, Video(enrollment, course), Now);
        return WithId(enrollment.TryCertify(course, [], Now)!, certificateId);
    }

    private static PracticalVideo Video(Enrollment enrollment, Course course) =>
        enrollment.AddPracticalVideo(course, Trainer, uploaderRunsThePractical: true, "practical.mp4", "video/mp4", 4096,
            new string('a', 64), $"test/{Guid.NewGuid():N}.mp4", Now);

    private static BranchLaunchStatus Live(long branchId, RecipeVersion version, long launchId) =>
        WithId(BranchLaunchStatus.LiveForExistingRecipe(branchId, version.RecipeId, version.Id, 2, Now), launchId);

    private static ImpactTrigger Version(RecipeVersion version) => new(TriggerKind.RecipeVersion, version.Id);

    // ---------------------------------------------------------------- the graph

    [Fact]
    public void Graph_reaches_the_versions_that_use_an_ingredient()
    {
        var withMilk = ReleasedVersion(310, RecipeId);
        var withoutMilk = PlainTea(410);
        var draftWithMilk = WithId(ValidOolongMilkTea(), 510);

        var affected = DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.Ingredient, Milk),
            [withoutMilk, draftWithMilk, withMilk]);

        // Not the version that does not use it, and not the draft: nothing was built on a draft.
        Assert.Equal([310], affected.Select(v => v.Id));
        // Every released version uses the oolong.
        Assert.Equal([310, 410], DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.Ingredient, Oolong),
            [withoutMilk, draftWithMilk, withMilk]).Select(v => v.Id));
        Assert.Empty(DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.Ingredient, Unknown), [withMilk, withoutMilk]));
    }

    [Fact]
    public void Graph_reaches_the_versions_whose_steps_use_an_equipment_class()
    {
        var brewed = ReleasedVersion(310, RecipeId);
        RecipeVersion[] versions = [brewed, PlainTea(410)];

        Assert.Equal([310, 410], DependencyGraph.VersionsDependingOn(
            new ImpactTrigger(TriggerKind.StandardEquipment, 1, "TEA_BREWER"), versions).Select(v => v.Id));
        Assert.Empty(DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.StandardEquipment, 2, "MILK_STEAMER"), versions));
        // A class nobody could name reaches nothing, rather than every step without a machine.
        Assert.Empty(DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.StandardEquipment, 9), versions));
    }

    [Fact]
    public void Graph_still_reaches_a_version_that_has_been_superseded()
    {
        var old = ReleasedVersion(310, RecipeId);
        var current = Supersede(old);
        Assert.Equal((VersionState.Superseded, VersionState.Released), (old.State, current.State));

        // Staff may still be certified on it and branches may still sell it.
        Assert.Equal([310, 320], DependencyGraph.VersionsDependingOn(new ImpactTrigger(TriggerKind.Ingredient, Oolong), [old, current]).Select(v => v.Id));
        Assert.Equal([310], DependencyGraph.VersionsDependingOn(Version(old), [old, current]).Select(v => v.Id));
    }

    [Fact]
    public void Graph_follows_a_version_to_its_published_course_its_valid_certificates_and_its_live_branches()
    {
        var version = ReleasedVersion(310, RecipeId);
        var other = ReleasedVersion(410, recipeId: 77);
        var course = PublishedCourse(version, 20);
        var otherCourse = PublishedCourse(other, 21);
        var certificate = Certified(course, userId: 61, certificateId: 980);
        var flagged = Certified(course, userId: 62, certificateId: 981);
        flagged.FlagForRecertification();
        var elsewhere = Certified(otherCourse, userId: 63, certificateId: 982);
        var live = Live(7, version, 1);
        var planned = WithId(BranchLaunchStatus.Plan(8, RecipeId, version.Id, 2), 2);
        var liveOnOther = Live(7, other, 3);

        var affected = DependencyGraph.Downstream([version.Id], [course, otherCourse], [certificate, flagged, elsewhere],
            [live, planned, liveOnOther]);

        Assert.Equal([310], affected.RecipeVersionIds);
        Assert.Equal([course], affected.Courses);
        // A certificate that is already flagged, and a branch that is not selling, are not affected again.
        Assert.Equal([certificate], affected.Certificates);
        Assert.Equal([live], affected.LiveBranches);
        Assert.False(affected.IsEmpty);
        Assert.True(DependencyGraph.Downstream([999], [course], [certificate], [live]).IsEmpty);
    }

    [Fact]
    public void Course_that_is_not_published_is_not_put_out_of_date()
    {
        var version = ReleasedVersion(310, RecipeId);
        var draft = WithId(Course.Create(CourseType.Product, "Still being written", version, Trainer), 20);

        Assert.Empty(DependencyGraph.Downstream([version.Id], [draft], [], []).Courses);
    }

    [Fact]
    public void Trigger_is_named_as_the_audit_log_names_its_entities()
    {
        Assert.Equal(["Ingredient", "StandardEquipment", "RecipeVersion"],
            Enum.GetValues<TriggerKind>().Select(kind => new ImpactTrigger(kind, 1).EntityType));
        Assert.True(ImpactTrigger.TryParseKind("StandardEquipment", out var kind));
        Assert.Equal(TriggerKind.StandardEquipment, kind);
        Assert.All(new[] { null, "", "Course", "ingredient", "7" }, name => Assert.False(ImpactTrigger.TryParseKind(name, out _)));
    }

    // ---------------------------------------------------------------- the what-if

    [Fact]
    public void What_if_computes_one_uncommitted_row_per_affected_entity_and_changes_nothing()
    {
        var version = ReleasedVersion(310, RecipeId);
        var course = PublishedCourse(version, 20);
        var certificate = Certified(course, 61, 980);
        var live = Live(7, version, 1);
        var affected = DependencyGraph.Downstream([version.Id], [course], [certificate], [live]);

        var run = ImpactRun.WhatIf(RunId, new ImpactTrigger(TriggerKind.Ingredient, Milk), affected, Now);

        Assert.Equal(
        [
            ("Course", 20L, ImpactType.OutOfDate), ("Certificate", 980L, ImpactType.NeedsRecert),
            ("BranchLaunchStatus", 1L, ImpactType.BranchAffected),
        ], run.NewRows.Select(row => (row.AffectedEntity, row.AffectedId, row.ImpactType)));
        Assert.All(run.NewRows, row =>
        {
            Assert.Equal((RunId, "Ingredient", Milk, false, Now), (row.AnalysisRunId, row.TriggerEntity, row.TriggerId, row.Committed, row.CreatedAt));
        });
        // What-if: nothing but the rows.
        Assert.Equal((CourseState.Published, CertificateStatus.Valid, LaunchStatus.Live), (course.State, certificate.Status, live.Status));
    }

    // ---------------------------------------------------------------- BR-15: the commit

    [Fact]
    public void BR_15_commit_flags_the_course_and_the_certificates_and_deletes_nothing()
    {
        var version = ReleasedVersion(310, RecipeId);
        var course = PublishedCourse(version, 20);
        var modules = course.Modules.Count;
        var lessons = course.Modules.Sum(m => m.Lessons.Count);
        Certificate[] certificates = [Certified(course, 61, 980), Certified(course, 62, 981)];
        var live = Live(7, version, 1);
        var run = ImpactRun.WhatIf(RunId, Version(version),
            DependencyGraph.Downstream([version.Id], [course], certificates, [live]), Now);
        var rows = run.NewRows.ToList();

        run.Commit([]);

        // The old course is still there, whole, and out of date...
        Assert.Equal((20, CourseState.OutOfDate, version.Id), (course.Id, course.State, course.RecipeVersionId!.Value));
        Assert.Equal((modules, lessons, 7), (course.Modules.Count, course.Modules.Sum(m => m.Lessons.Count), course.Quiz.Questions.Count));
        // ...and so are the old certificates, still bound to the version they were earned on.
        Assert.All(certificates, certificate =>
        {
            Assert.Equal((CertificateStatus.NeedsRecert, version.Id, 20), (certificate.Status, certificate.RecipeVersionId!.Value, certificate.CourseId));
            Assert.False(certificate.Certifies(certificate.UserId, version.Id)); // kept as a record, no longer a qualification
        });
        // A branch is reported, not closed: what it sells is decided at the gate.
        Assert.Equal((LaunchStatus.Live, version.Id), (live.Status, live.RecipeVersionId!.Value));
        Assert.All(rows, row => Assert.True(row.Committed));
    }

    [Fact]
    public void BR_15_neither_a_course_nor_a_certificate_has_a_way_to_be_removed_by_propagation()
    {
        // The run can flag and nothing else: it holds no operation that deletes.
        var operations = typeof(ImpactRun).GetMethods().Where(m => m.DeclaringType == typeof(ImpactRun) && !m.IsSpecialName)
            .Select(m => m.Name).Order();
        Assert.Equal(["Commit", "Resume", "WhatIf"], operations);
        // And both records are of the kind the persistence layer refuses to delete.
        Assert.Equal("BR-15", ((INeverDeleted)PublishedCourse(ReleasedVersion(310, RecipeId), 20)).RetentionRule);
        Assert.True(typeof(INeverDeleted).IsAssignableFrom(typeof(Certificate)));
    }

    [Fact]
    public void Run_is_committed_once()
    {
        var version = ReleasedVersion(310, RecipeId);
        var course = PublishedCourse(version, 20);
        var first = ImpactRun.WhatIf(RunId, Version(version), DependencyGraph.Downstream([version.Id], [course], [], []), Now);
        first.Commit([]);

        // Taken up again later: nothing is affected any more, and its rows are all committed.
        var again = ImpactRun.Resume(RunId, Version(version), DependencyGraph.Downstream([version.Id], [course], [], []), first.NewRows, Now);
        var refusal = Assert.Throws<DomainException>(() => again.Commit(first.NewRows));

        Assert.Equal((ErrorKind.RuleViolation, "IMPACT_ALREADY_COMMITTED"), (refusal.Kind, refusal.Rule));
        Assert.Empty(again.NewRows);
    }

    [Fact]
    public void Commit_takes_in_what_became_affected_after_the_what_if()
    {
        var version = ReleasedVersion(310, RecipeId);
        var course = PublishedCourse(version, 20);
        var early = Certified(course, 61, 980);
        var whatIf = ImpactRun.WhatIf(RunId, Version(version), DependencyGraph.Downstream([version.Id], [course], [early], []), Now);
        // Someone is certified between the what-if and the commit.
        var late = Certified(course, 62, 981);

        var commit = ImpactRun.Resume(RunId, Version(version),
            DependencyGraph.Downstream([version.Id], [course], [early, late], []), whatIf.NewRows, Now.AddHours(2));
        commit.Commit(whatIf.NewRows);

        // One new row, for the one that was not known; nobody is left valid on the old procedure.
        Assert.Equal([("Certificate", 981L)], commit.NewRows.Select(row => (row.AffectedEntity, row.AffectedId)));
        Assert.All(new[] { early, late }, certificate => Assert.Equal(CertificateStatus.NeedsRecert, certificate.Status));
        Assert.All(whatIf.NewRows.Concat(commit.NewRows), row => Assert.True(row.Committed));
        Assert.Equal(CourseState.OutOfDate, course.State);
    }

    // ---------------------------------------------------------------- UC-31

    private static IReadOnlyList<ModuleScore> Attempt(params (ModuleType Module, int Correct, int Total)[] modules) =>
        [.. modules.Select(m => new ModuleScore((long)m.Module + 100, m.Module, m.Correct, m.Total))];

    [Fact]
    public void First_attempt_pass_rate_is_computed_per_module_and_a_module_markedly_below_the_others_is_highlighted()
    {
        // Four trainees. Everyone gets SOP right, three of four get the ingredients right, one of four the technique.
        var modules = CourseEffectiveness.PerModule(
        [
            Attempt((ModuleType.Sop, 3, 3), (ModuleType.Ingredients, 2, 2), (ModuleType.Technique, 2, 2)),
            Attempt((ModuleType.Sop, 3, 3), (ModuleType.Ingredients, 2, 2), (ModuleType.Technique, 1, 2)),
            Attempt((ModuleType.Sop, 3, 3), (ModuleType.Ingredients, 2, 2), (ModuleType.Technique, 0, 2)),
            Attempt((ModuleType.Sop, 3, 3), (ModuleType.Ingredients, 1, 2), (ModuleType.Technique, 1, 2)),
        ]).ToDictionary(m => m.ModuleType);

        Assert.Equal((4, 4, 100.0m, false), (modules[ModuleType.Sop].FirstAttempts, modules[ModuleType.Sop].Passed,
            modules[ModuleType.Sop].PassRatePct, modules[ModuleType.Sop].Highlighted));
        Assert.Equal((3, 75.0m, false), (modules[ModuleType.Ingredients].Passed, modules[ModuleType.Ingredients].PassRatePct,
            modules[ModuleType.Ingredients].Highlighted));
        // 25 percent against an average of 87.5 for the others: the module that needs attention.
        Assert.Equal((1, 25.0m, true), (modules[ModuleType.Technique].Passed, modules[ModuleType.Technique].PassRatePct,
            modules[ModuleType.Technique].Highlighted));
    }

    [Fact]
    public void Module_is_not_highlighted_for_a_small_difference_or_with_nothing_to_compare_it_with()
    {
        // Nine of ten against ten of ten: below, not markedly.
        var attempts = Enumerable.Range(1, 10)
            .Select(i => Attempt((ModuleType.Sop, 1, 1), (ModuleType.Technique, i == 1 ? 0 : 1, 1)));
        Assert.All(CourseEffectiveness.PerModule(attempts), module => Assert.False(module.Highlighted));
        // The line is twenty points: eight of ten is on it.
        Assert.True(CourseEffectiveness.PerModule(Enumerable.Range(1, 10)
                .Select(i => Attempt((ModuleType.Sop, 1, 1), (ModuleType.Technique, i <= 2 ? 0 : 1, 1))))
            .Single(module => module.ModuleType == ModuleType.Technique).Highlighted);
        // A single module is not below "the others".
        Assert.False(Assert.Single(CourseEffectiveness.PerModule([Attempt((ModuleType.Sop, 0, 3))])).Highlighted);
        Assert.Empty(CourseEffectiveness.PerModule([]));
    }

    [Fact]
    public void Rates_are_percentages_to_one_decimal_and_null_without_a_denominator()
    {
        Assert.Equal(66.7m, CourseEffectiveness.Percent(2, 3));
        Assert.Equal(0m, CourseEffectiveness.Percent(0, 5));
        Assert.Null(CourseEffectiveness.Percent(0, 0));
    }

    [Fact]
    public void On_time_rate_judges_the_enrolments_that_were_passed_or_whose_deadline_has_gone_by()
    {
        var today = new DateOnly(2026, 10, 20);
        var due = new DateOnly(2026, 10, 15);
        DateTimeOffset At(int day) => new(2026, 10, day, 8, 0, 0, TimeSpan.Zero);

        var rate = CourseEffectiveness.OnTimeRate(
        [
            (EnrollmentState.Passed, due, At(14)),                      // on time
            (EnrollmentState.Passed, due, At(15)),                      // on the last day: on time
            (EnrollmentState.Passed, due, At(18)),                      // passed, late
            (EnrollmentState.InProgress, due, null),                    // overdue and not passed
            (EnrollmentState.Locked, due, null),                        // overdue and not passed
            (EnrollmentState.InProgress, today.AddDays(5), null),       // still has time: not judged
            (EnrollmentState.Closed, due, null),                        // withdrawn: not judged
        ], today);

        Assert.Equal(40.0m, rate); // two of five
        Assert.Null(CourseEffectiveness.OnTimeRate([(EnrollmentState.InProgress, today.AddDays(5), null)], today));
    }

    [Fact]
    public void Time_to_certification_is_the_average_from_assignment_to_the_certificate()
    {
        DateTimeOffset At(int day, int hour = 8) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(4.5m, CourseEffectiveness.AverageDaysToCertification([(At(1), At(4)), (At(2), At(8))])); // 3 and 6 days
        Assert.Equal(0.5m, CourseEffectiveness.AverageDaysToCertification([(At(1), At(1, 20))]));
        Assert.Null(CourseEffectiveness.AverageDaysToCertification([(At(1), null)]));
    }
}
