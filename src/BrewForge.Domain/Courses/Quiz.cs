using System.Text.Json;
using BrewForge.Domain.Common;

namespace BrewForge.Domain.Courses;

public sealed record QuizOption(string Key, string Text);

/// <summary>
/// A multiple-choice question, tagged to the module it tests (BR-35). The tag
/// is what lets an attempt say which module a trainee failed.
/// </summary>
public sealed class QuizQuestion
{
    private static readonly JsonSerializerOptions OptionsFormat = new(JsonSerializerDefaults.Web);

    private QuizQuestion() { }

    internal QuizQuestion(long courseModuleId, string questionText, IReadOnlyList<QuizOption> options,
        string correctOption)
    {
        CourseModuleId = courseModuleId;
        QuestionText = questionText;
        OptionsJson = JsonSerializer.Serialize(options, OptionsFormat);
        CorrectOption = correctOption;
    }

    public long Id { get; private set; }
    public long QuizId { get; private set; }
    public long CourseModuleId { get; private set; }
    public string QuestionText { get; private set; } = null!;

    /// <summary>JSONB: <c>[{"key":"A","text":"..."}, ...]</c>.</summary>
    public string OptionsJson { get; private set; } = null!;
    public string CorrectOption { get; private set; } = null!;

    public IReadOnlyList<QuizOption> Options() =>
        JsonSerializer.Deserialize<List<QuizOption>>(OptionsJson, OptionsFormat) ?? [];
}

/// <summary>The single theoretical assessment of a course.</summary>
public sealed class Quiz
{
    public const string TaggingRule = "BR-35";

    private readonly List<QuizQuestion> _questions = [];

    private Quiz() { }

    internal Quiz(string title)
    {
        Title = title;
    }

    public long Id { get; private set; }
    public long CourseId { get; private set; }
    public string Title { get; private set; } = null!;

    /// <summary>The percentage a trainee must reach.</summary>
    public int PassScore { get; private set; } = 80;
    public int QuestionCount { get; private set; }

    public IReadOnlyList<QuizQuestion> Questions => _questions;

    internal void Update(string? title, int? passScore)
    {
        title = title?.Trim();
        new FieldErrors()
            .Check(title is null || title.Length > 0, "title", "must not be empty")
            .MaxLength("title", title, 160)
            .Check(passScore is null or (>= 0 and <= 100), "passScore", "must be between 0 and 100")
            .ThrowIfAny();

        if (title is not null) Title = title;
        if (passScore is not null) PassScore = passScore.Value;
    }

    /// <param name="module">
    /// The module the question tests. It is required and must be a module of
    /// the same course (BR-35).
    /// </param>
    internal QuizQuestion AddQuestion(CourseModule? module, string? questionText, IReadOnlyList<QuizOption>? options,
        string? correctOption)
    {
        if (module is null || module.CourseId != CourseId)
        {
            throw DomainException.RuleViolation(TaggingRule,
                "Every quiz question must be tagged with the module of this course that it tests.",
                details: new ErrorDetail("courseModuleId", module is null ? "is required" : "is not a module of this course"));
        }

        questionText = questionText?.Trim();
        correctOption = correctOption?.Trim();
        var cleaned = (options ?? []).Select(o => new QuizOption(o.Key?.Trim() ?? "", o.Text?.Trim() ?? "")).ToList();

        new FieldErrors()
            .RequiredMax("questionText", questionText, 500)
            .Check(cleaned.Count >= 2, "options", "a question needs at least two options")
            .Check(cleaned.All(o => o.Key.Length is > 0 and <= 8), "options", "every option needs a key of at most 8 characters")
            .Check(cleaned.All(o => o.Text.Length > 0), "options", "every option needs a text")
            .Check(cleaned.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() == cleaned.Count, "options",
                "option keys must be unique")
            .RequiredMax("correctOption", correctOption, 8)
            .Check(string.IsNullOrEmpty(correctOption) || cleaned.Any(o => o.Key == correctOption), "correctOption",
                "must be the key of one of the options")
            .ThrowIfAny();

        var question = new QuizQuestion(module.Id, questionText!, cleaned, correctOption!);
        _questions.Add(question);
        QuestionCount = _questions.Count;
        return question;
    }
}
