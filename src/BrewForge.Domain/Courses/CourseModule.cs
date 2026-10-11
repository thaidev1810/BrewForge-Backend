using BrewForge.Domain.Common;

namespace BrewForge.Domain.Courses;

/// <summary>
/// One teaching unit inside a module. A lesson that came from a recipe step
/// keeps the link to it, and shows the step's reference values by reading
/// them from the bound version when it is rendered: they are never copied
/// into <see cref="Content"/> (BR-22).
/// </summary>
public sealed class Lesson
{
    private Lesson() { }

    internal Lesson(int lessonOrder, string title, string? content, string? mediaUrl, long? recipeStepId)
    {
        LessonOrder = lessonOrder;
        Title = title;
        Content = content;
        MediaUrl = mediaUrl;
        RecipeStepId = recipeStepId;
    }

    public long Id { get; private set; }
    public long CourseModuleId { get; private set; }
    public int LessonOrder { get; private set; }
    public string Title { get; private set; } = null!;

    /// <summary>The trainer's own teaching text. Null on a generated lesson, by design.</summary>
    public string? Content { get; private set; }

    /// <summary>An uploaded image or an embedded video link. Video is never uploaded.</summary>
    public string? MediaUrl { get; private set; }

    /// <summary>The recipe step this lesson was generated from, if any.</summary>
    public long? RecipeStepId { get; private set; }

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    private readonly List<LessonMedia> _media = [];

    /// <summary>The pictures the trainer uploaded to the lesson. Use <see cref="OrderedMedia"/> to read them in order.</summary>
    public IReadOnlyList<LessonMedia> Media => _media;

    public IReadOnlyList<LessonMedia> OrderedMedia() => [.. _media.OrderBy(media => media.SortOrder).ThenBy(media => media.Id)];

    internal LessonMedia AddMedia(long uploadedBy, string fileName, string contentType, long sizeBytes, string sha256,
        string storageKey, DateTimeOffset now)
    {
        EnsureRoomForMedia();
        var media = new LessonMedia(_media.Count == 0 ? 1 : _media.Max(m => m.SortOrder) + 1, uploadedBy, fileName,
            contentType, sizeBytes, sha256, storageKey, now);
        _media.Add(media);
        return media;
    }

    internal void EnsureRoomForMedia()
    {
        if (_media.Count >= LessonMedia.MaxPerLesson)
        {
            throw DomainException.RuleViolation(LessonMedia.Rule,
                $"A lesson carries at most {LessonMedia.MaxPerLesson} pictures. Remove one before adding another.");
        }
    }

    internal void RemoveMedia(LessonMedia media)
    {
        if (!_media.Remove(media)) throw new ArgumentException("The picture is not part of this lesson.", nameof(media));
    }


    internal void SetAuthored(string title, string? content, string? mediaUrl)
    {
        Title = title;
        Content = content;
        MediaUrl = mediaUrl;
    }

    /// <summary>On a generated lesson the trainer writes the prose and nothing else.</summary>
    internal void SetProse(string? content, string? mediaUrl)
    {
        Content = content;
        MediaUrl = mediaUrl;
    }

    internal void Relink(long recipeStepId, string title)
    {
        RecipeStepId = recipeStepId;
        Title = title;
    }
}

/// <summary>One of the seven modules of a course (BR-29).</summary>
public sealed class CourseModule
{
    public const string AuthoringRule = "BR-30";

    private readonly List<Lesson> _lessons = [];

    private CourseModule() { }

    internal CourseModule(ModuleType moduleType, ModuleSource source)
    {
        ModuleType = moduleType;
        ModuleOrder = (int)moduleType + 1;
        Source = source;
        State = ModuleState.Empty;
    }

    public long Id { get; private set; }
    public long CourseId { get; private set; }
    public ModuleType ModuleType { get; private set; }

    /// <summary>1 to 7. The position is fixed by the module type.</summary>
    public int ModuleOrder { get; private set; }
    public ModuleSource Source { get; private set; }
    public int? DurationMinutes { get; private set; }
    public ModuleState State { get; private set; }

    public IReadOnlyList<Lesson> Lessons => _lessons;

    public IReadOnlyList<Lesson> OrderedLessons() => [.. _lessons.OrderBy(lesson => lesson.LessonOrder)];

    /// <summary>
    /// Whether the module is ready to be taught: it has at least one lesson,
    /// and every lesson either is generated from the recipe (and so renders
    /// its content from the bound version) or has the trainer's text.
    /// </summary>
    public bool HasContent =>
        _lessons.Count > 0 && (Source == ModuleSource.Generated || _lessons.All(lesson => lesson.HasContent));

    public bool IsComplete => HasContent && DurationMinutes is > 0;

    /// <summary>BR-30: a generated module belongs to the recipe, not to the trainer.</summary>
    public void EnsureAuthorable()
    {
        if (Source == ModuleSource.Generated)
        {
            throw DomainException.RuleViolation(AuthoringRule,
                $"The {ModuleType.Code()} module is generated from the recipe version and cannot be edited. " +
                "Regenerate it instead.");
        }
    }

    public void SetDuration(int? durationMinutes)
    {
        EnsureAuthorable();
        new FieldErrors()
            .Check(durationMinutes is null or > 0, "durationMinutes", "must be greater than 0")
            .ThrowIfAny();
        DurationMinutes = durationMinutes;
        Touch();
    }

    public Lesson AddLesson(string? title, string? content, string? mediaUrl)
    {
        EnsureAuthorable();
        var (cleanTitle, cleanContent, cleanMedia) = Clean(title, content, mediaUrl, titleRequired: true);

        var lesson = new Lesson(NextLessonOrder(), cleanTitle!, cleanContent, cleanMedia, recipeStepId: null);
        _lessons.Add(lesson);
        Touch();
        return lesson;
    }

    public void UpdateLesson(Lesson lesson, string? title, string? content, string? mediaUrl)
    {
        EnsureAuthorable();
        EnsureOwns(lesson);

        if (lesson.RecipeStepId is not null)
        {
            // A generated item of a MIXED module: the title and the link to
            // the step belong to the recipe; only the prose is the trainer's.
            var (_, prose, media) = Clean(null, content, mediaUrl, titleRequired: false);
            lesson.SetProse(prose, media);
        }
        else
        {
            var (cleanTitle, cleanContent, cleanMedia) = Clean(title, content, mediaUrl, titleRequired: true);
            lesson.SetAuthored(cleanTitle!, cleanContent, cleanMedia);
        }
        Touch();
    }

    public void RemoveLesson(Lesson lesson)
    {
        EnsureAuthorable();
        EnsureOwns(lesson);
        if (lesson.RecipeStepId is not null)
        {
            throw DomainException.RuleViolation(AuthoringRule,
                "This lesson is generated from a recipe step and cannot be deleted.");
        }
        _lessons.Remove(lesson);
        Touch();
    }

    /// <summary>
    /// Whether a picture may be added to the lesson, asked before the file is
    /// stored. BR-30 holds here too: a generated module is the recipe's, and
    /// a picture on it would be the trainer's.
    /// </summary>
    public void EnsureAcceptsMedia(Lesson lesson)
    {
        EnsureAuthorable();
        EnsureOwns(lesson);
        lesson.EnsureRoomForMedia();
    }

    public LessonMedia AddLessonMedia(Lesson lesson, long uploadedBy, string fileName, string contentType,
        long sizeBytes, string sha256, string storageKey, DateTimeOffset now)
    {
        EnsureAcceptsMedia(lesson);
        LessonMedia.EnsureSize(sizeBytes);
        return lesson.AddMedia(uploadedBy, fileName, contentType, sizeBytes, sha256, storageKey, now);
    }

    public void RemoveLessonMedia(Lesson lesson, LessonMedia media)
    {
        EnsureAuthorable();
        EnsureOwns(lesson);
        lesson.RemoveMedia(media);
    }

    // ---------------------------------------------------------------- generation (internal)


    internal void ReplaceGenerated(IEnumerable<(string Title, long? RecipeStepId)> lessons, int durationMinutes)
    {
        _lessons.Clear();
        var order = 1;
        foreach (var (title, stepId) in lessons)
        {
            _lessons.Add(new Lesson(order++, title, content: null, mediaUrl: null, stepId));
        }
        DurationMinutes = durationMinutes;
        State = ModuleState.Complete;
    }

    /// <summary>
    /// Rebuilds the generated items of a MIXED module while keeping what the
    /// trainer wrote: an item whose step number still exists keeps its prose,
    /// and lessons the trainer added by hand are left exactly as they are.
    /// </summary>
    internal void SyncGeneratedItems(IReadOnlyList<(int StepOrder, string Title, long RecipeStepId)> items,
        IReadOnlyDictionary<long, int> stepOrderOfPreviousItem)
    {
        var previous = _lessons.Where(lesson => lesson.RecipeStepId is not null).ToList();
        var byStepOrder = new Dictionary<int, Lesson>();
        foreach (var lesson in previous)
        {
            if (stepOrderOfPreviousItem.TryGetValue(lesson.RecipeStepId!.Value, out var stepOrder))
            {
                byStepOrder.TryAdd(stepOrder, lesson);
            }
        }

        var kept = new HashSet<Lesson>();
        foreach (var (stepOrder, title, stepId) in items)
        {
            if (byStepOrder.TryGetValue(stepOrder, out var existing))
            {
                existing.Relink(stepId, title);
                kept.Add(existing);
            }
            else
            {
                _lessons.Add(new Lesson(NextLessonOrder(), title, content: null, mediaUrl: null, stepId));
            }
        }
        _lessons.RemoveAll(lesson => lesson.RecipeStepId is not null && previous.Contains(lesson) && !kept.Contains(lesson));
        Touch();
    }

    /// <summary>After a rebuild on a new version, what the trainer wrote has to be looked at again.</summary>
    internal void RequireReview()
    {
        if (Source != ModuleSource.Generated) State = ModuleState.NeedsReview;
    }

    /// <summary>The trainer has looked at the module again: it is whatever its content says it is.</summary>
    public void ConfirmReviewed()
    {
        EnsureAuthorable();
        Touch();
    }

    private void Touch() =>
        State = IsComplete ? ModuleState.Complete
            : _lessons.Count == 0 && DurationMinutes is null ? ModuleState.Empty
            : ModuleState.Draft;

    private int NextLessonOrder() => _lessons.Count == 0 ? 1 : _lessons.Max(lesson => lesson.LessonOrder) + 1;

    private void EnsureOwns(Lesson lesson)
    {
        if (!_lessons.Contains(lesson)) throw new ArgumentException("The lesson is not part of this module.", nameof(lesson));
    }

    private static (string? Title, string? Content, string? MediaUrl) Clean(string? title, string? content,
        string? mediaUrl, bool titleRequired)
    {
        title = title?.Trim();
        content = string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        mediaUrl = string.IsNullOrWhiteSpace(mediaUrl) ? null : mediaUrl.Trim();

        var errors = new FieldErrors();
        if (titleRequired) errors.RequiredMax("title", title, 160);
        errors.MaxLength("mediaUrl", mediaUrl, 500)
            .Check(mediaUrl is null || IsHttpUrl(mediaUrl), "mediaUrl", "must be an http or https URL")
            .ThrowIfAny();
        return (title, content, mediaUrl);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
