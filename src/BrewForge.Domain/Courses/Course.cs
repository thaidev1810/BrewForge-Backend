using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;

namespace BrewForge.Domain.Courses;

/// <summary>
/// A training course, and the aggregate root of its seven modules, their
/// lessons and its quiz. A PRODUCT course is bound to exactly one released
/// recipe version (BR-18, BR-20).
/// </summary>
public sealed class Course : INeverDeleted
{
    public const string StateRule = "STATE_TRANSITION";

    /// <summary>The only transitions that exist (data dictionary, section 7).</summary>
    private static readonly HashSet<(CourseState From, CourseState To)> Transitions =
    [
        (CourseState.Draft, CourseState.PendingApproval),
        (CourseState.PendingApproval, CourseState.Draft),
        (CourseState.PendingApproval, CourseState.Published),
        (CourseState.Published, CourseState.OutOfDate),
        (CourseState.OutOfDate, CourseState.Draft),
        (CourseState.Published, CourseState.Archived),
    ];

    private readonly List<CourseModule> _modules = [];

    private Course() { }

    public long Id { get; private set; }

    /// <summary>The bound version. Set at creation; only a rebuild of an out-of-date course moves it (BR-18).</summary>
    public long? RecipeVersionId { get; private set; }
    public CourseType CourseType { get; private set; }
    public string Title { get; private set; } = null!;
    public int? TotalDurationMin { get; private set; }
    public long CreatedBy { get; private set; }
    public long? ApprovedBy { get; private set; }
    public CourseState State { get; private set; } = CourseState.Draft;
    public DateTimeOffset? PublishedAt { get; private set; }

    public IReadOnlyList<CourseModule> Modules => _modules;
    public Quiz Quiz { get; private set; } = null!;

    public IReadOnlyList<CourseModule> OrderedModules() => [.. _modules.OrderBy(module => module.ModuleOrder)];

    // BR-15: an out-of-date course is flagged, never deleted.
    string INeverDeleted.RetentionRule => "BR-15";

    /// <summary>
    /// Creates a course in DRAFT with its seven modules. When it is built
    /// from a recipe version, that version must be RELEASED (BR-20) and
    /// modules 1 to 4 and the gate list of module 5 are generated from it.
    /// </summary>
    public static Course Create(CourseType courseType, string? title, RecipeVersion? version, long createdBy)
    {
        title = title?.Trim();
        new FieldErrors()
            .RequiredMax("title", title, 160)
            .Check(Enum.IsDefined(courseType), "courseType", "is not a valid course type")
            .Check(courseType != CourseType.Product || version is not null, "recipeVersionId",
                "is required for a PRODUCT course")
            .ThrowIfAny();
        EnsureUsableAsSource(version);

        var course = new Course
        {
            CourseType = courseType,
            Title = title!,
            RecipeVersionId = version?.Id,
            CreatedBy = createdBy,
            Quiz = new Quiz(Shorten($"{title} - quiz")),
        };
        course._modules.AddRange(CourseModuleGenerator.CreateModules(version));
        course.RefreshTotalDuration();
        return course;
    }

    /// <summary>BR-20: only a version in RELEASED state may be the source of a course.</summary>
    public static void EnsureUsableAsSource(RecipeVersion? version)
    {
        if (version is not null && version.State != VersionState.Released)
        {
            throw DomainException.RuleViolation("BR-20",
                $"Only a released recipe version can be the source of a course; version {version.VersionNo} is {version.State.Code()}.");
        }
    }

    public CourseModule Module(long moduleId) =>
        _modules.SingleOrDefault(module => module.Id == moduleId)
        ?? throw DomainException.NotFound("Course module", moduleId);

    // ---------------------------------------------------------------- authoring

    /// <summary>A course is edited in DRAFT only. A published course is never edited in place.</summary>
    public void EnsureEditable()
    {
        if (State != CourseState.Draft)
        {
            throw DomainException.RuleViolation(StateRule,
                $"A course can only be edited in DRAFT; this one is {State.Code()}.");
        }
    }

    public void SetModuleDuration(CourseModule module, int? durationMinutes)
    {
        EnsureEditable();
        module.SetDuration(durationMinutes);
        RefreshTotalDuration();
    }

    public Lesson AddLesson(CourseModule module, string? title, string? content, string? mediaUrl)
    {
        EnsureEditable();
        return module.AddLesson(title, content, mediaUrl);
    }

    public void UpdateLesson(CourseModule module, Lesson lesson, string? title, string? content, string? mediaUrl)
    {
        EnsureEditable();
        module.UpdateLesson(lesson, title, content, mediaUrl);
    }

    public void RemoveLesson(CourseModule module, Lesson lesson)
    {
        EnsureEditable();
        module.RemoveLesson(lesson);
    }

    /// <summary>
    /// Rebuilds one module from the bound version. Refused for an AUTHORED
    /// module, which regeneration must never overwrite (BR-30).
    /// </summary>
    public void RegenerateModule(CourseModule module, RecipeVersion boundVersion)
    {
        EnsureEditable();
        EnsureIsBoundVersion(boundVersion);
        CourseModuleGenerator.Generate(module, boundVersion, previousVersion: null);
        RefreshTotalDuration();
    }

    /// <summary>
    /// Chooses which steps of the bound version the trainer observes in the
    /// practical evaluation. Items are steps of the recipe, never free text.
    /// </summary>
    public void SetPracticalChecklist(IReadOnlyCollection<long> recipeStepIds, RecipeVersion boundVersion)
    {
        EnsureEditable();
        EnsureIsBoundVersion(boundVersion);

        var steps = boundVersion.Steps.ToDictionary(step => step.Id);
        new FieldErrors()
            .Check(recipeStepIds.All(steps.ContainsKey), "items", "refers to a step that is not part of the bound recipe version")
            .Check(recipeStepIds.Distinct().Count() == recipeStepIds.Count, "items", "lists the same step more than once")
            .ThrowIfAny();

        var technique = _modules.Single(module => module.ModuleType == ModuleType.Technique);
        technique.SyncGeneratedItems(
            [.. recipeStepIds.Select(id => steps[id]).OrderBy(step => step.StepOrder)
                .Select(step => (step.StepOrder, $"Technique gate - step {step.StepOrder}", step.Id))],
            boundVersion.Steps.ToDictionary(step => step.Id, step => step.StepOrder));
        technique.ConfirmReviewed();
    }

    public void UpdateQuiz(string? title, int? passScore)
    {
        EnsureEditable();
        Quiz.Update(title, passScore);
    }

    public QuizQuestion AddQuizQuestion(long? courseModuleId, string? questionText,
        IReadOnlyList<QuizOption>? options, string? correctOption)
    {
        EnsureEditable();
        var module = courseModuleId is { } id ? _modules.SingleOrDefault(m => m.Id == id) : null;
        return Quiz.AddQuestion(module, questionText, options, correctOption);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// DRAFT to PENDING_APPROVAL. Refused while any module has no content or
    /// no duration (BR-31, BR-19), or while the quiz has no question, since
    /// nobody could then be certified on the course (BR-21).
    /// </summary>
    public void Submit()
    {
        EnsureCanMoveTo(CourseState.PendingApproval);

        var incomplete = OrderedModules().Where(module => !module.IsComplete || module.State == ModuleState.NeedsReview).ToList();
        if (incomplete.Count > 0)
        {
            var names = string.Join(", ", incomplete.Select(module => module.ModuleType.Code()));
            throw DomainException.RuleViolation("BR-31",
                $"This course cannot be published: {incomplete.Count} modules have no content or no duration ({names}).",
                ErrorCodes.CourseHasEmptyLessons,
                [.. incomplete.Select(module => new ErrorDetail($"modules[{module.ModuleType.Code()}]",
                    module.State == ModuleState.NeedsReview ? "needs review after the rebuild"
                    : !module.HasContent ? "has no content"
                    : "has no duration"))]);
        }
        if (Quiz.QuestionCount == 0)
        {
            throw DomainException.RuleViolation("BR-21",
                "This course cannot be published: its quiz has no questions, so no trainee could pass it.",
                details: new ErrorDetail("quiz", "has no questions"));
        }

        TransitionTo(CourseState.PendingApproval);
    }

    public void Approve(long approverId, DateTimeOffset now)
    {
        TransitionTo(CourseState.Published);
        ApprovedBy = approverId;
        PublishedAt = now;
    }

    /// <summary>PENDING_APPROVAL back to DRAFT, with the manager's comment kept in the audit log.</summary>
    public void Return() => TransitionTo(CourseState.Draft);

    /// <summary>PUBLISHED to OUT_OF_DATE when the bound version is superseded (BR-15). Nothing is deleted.</summary>
    public void MarkOutOfDate() => TransitionTo(CourseState.OutOfDate);

    public void Archive() => TransitionTo(CourseState.Archived);

    /// <summary>
    /// OUT_OF_DATE to DRAFT on the version that replaced the bound one. The
    /// generated modules are rebuilt from the new version; what the trainer
    /// authored is kept and marked for review (BR-30). This is the only way
    /// the binding of a course ever moves (BR-18).
    /// </summary>
    public void RebuildOn(RecipeVersion newVersion, RecipeVersion previousVersion)
    {
        if (State != CourseState.OutOfDate)
        {
            throw DomainException.RuleViolation("BR-18",
                "A course is bound to one recipe version and the binding cannot be changed. " +
                "Only an out-of-date course is rebuilt on the version that replaced its own.");
        }
        EnsureIsBoundVersion(previousVersion);
        EnsureUsableAsSource(newVersion);
        if (newVersion.RecipeId != previousVersion.RecipeId)
        {
            throw DomainException.RuleViolation("BR-18", "A course can only be rebuilt on a version of the same recipe.");
        }

        RecipeVersionId = newVersion.Id;
        foreach (var module in _modules)
        {
            if (module.Source != ModuleSource.Authored)
            {
                CourseModuleGenerator.Generate(module, newVersion, previousVersion);
            }
            module.RequireReview();
        }
        ApprovedBy = null;
        PublishedAt = null;
        TransitionTo(CourseState.Draft);
        RefreshTotalDuration();
    }

    private void EnsureIsBoundVersion(RecipeVersion version)
    {
        if (RecipeVersionId is null || version.Id != RecipeVersionId)
        {
            throw DomainException.RuleViolation("BR-18", "The course is not bound to that recipe version.");
        }
    }

    private void EnsureCanMoveTo(CourseState target)
    {
        if (!Transitions.Contains((State, target)))
        {
            throw DomainException.RuleViolation(StateRule,
                $"A course cannot move from {State.Code()} to {target.Code()}.");
        }
    }

    private void TransitionTo(CourseState target)
    {
        EnsureCanMoveTo(target);
        State = target;
    }

    private void RefreshTotalDuration()
    {
        var total = _modules.Sum(module => module.DurationMinutes ?? 0);
        TotalDurationMin = total > 0 ? total : null;
    }

    private static string Shorten(string title) => title.Length <= 160 ? title : title[..160];
}
