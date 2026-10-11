using System.Text.Json;
using BrewForge.Domain.Common;

namespace BrewForge.Domain.Courses;

public enum QuestionType
{
    SingleChoice,
    MultipleChoice,
    TrueFalse,
}

/// <summary>
/// A question the trainer keeps for reuse, apart from any one quiz. It is
/// copied into a quiz when wanted; changing or deleting it later leaves the
/// quizzes that already hold a copy untouched.
/// </summary>
public sealed class QuestionBankItem
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);
    private static readonly QuizOption[] TrueFalseOptions = [new("TRUE", "True"), new("FALSE", "False")];

    private QuestionBankItem() { }

    public long Id { get; private set; }
    public QuestionType QuestionType { get; private set; }
    public string QuestionText { get; private set; } = null!;

    /// <summary>JSONB: <c>[{"key":"A","text":"..."}, ...]</c>.</summary>
    public string OptionsJson { get; private set; } = null!;

    /// <summary>The keys of the right answers, comma-separated: one for SINGLE_CHOICE and TRUE_FALSE, one or more otherwise.</summary>
    public string CorrectOptions { get; private set; } = null!;
    public long? CourseId { get; private set; }

    /// <summary>The module of a course that the question tests; needed to copy it into a quiz (BR-35).</summary>
    public ModuleType? ModuleType { get; private set; }
    public long CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<QuizOption> Options() =>
        JsonSerializer.Deserialize<List<QuizOption>>(OptionsJson, Format) ?? [];

    public IReadOnlyList<string> CorrectKeys() => CorrectOptions.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static QuestionBankItem Create(QuestionType? type, string? text, IReadOnlyList<QuizOption>? options,
        IReadOnlyList<string>? correct, long? courseId, ModuleType? moduleType, long createdBy, DateTimeOffset now)
    {
        var item = new QuestionBankItem { CreatedBy = createdBy, CreatedAt = now };
        item.Set(type, text, options, correct, courseId, moduleType);
        return item;
    }

    public void Set(QuestionType? type, string? text, IReadOnlyList<QuizOption>? options, IReadOnlyList<string>? correct,
        long? courseId, ModuleType? moduleType)
    {
        text = text?.Trim() ?? "";
        var errors = new FieldErrors()
            .Check(type is not null, "questionType", "is required: SINGLE_CHOICE, MULTIPLE_CHOICE or TRUE_FALSE")
            .RequiredMax("questionText", text, 500);

        var kept = type == QuestionType.TrueFalse
            ? [.. TrueFalseOptions]
            : (options ?? []).Select(o => new QuizOption(o.Key?.Trim() ?? "", o.Text?.Trim() ?? "")).ToList();
        var keys = kept.Select(o => o.Key).ToList();
        var answers = (correct ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();

        errors.Check(kept.Count >= 2, "options", "needs at least two options")
            .Check(kept.All(o => o.Key.Length > 0 && o.Text.Length > 0), "options", "every option needs a key and a text")
            .Check(keys.Distinct().Count() == keys.Count, "options", "keys must be unique")
            .Check(answers.Count > 0 && answers.All(keys.Contains), "correctOptions", "must name options that exist")
            .Check(type == QuestionType.MultipleChoice || answers.Count <= 1, "correctOptions", "exactly one answer is allowed for this type")
            .ThrowIfAny();

        QuestionType = type!.Value;
        QuestionText = text;
        OptionsJson = JsonSerializer.Serialize(kept, Format);
        CorrectOptions = string.Join(',', answers);
        CourseId = courseId;
        ModuleType = moduleType;
    }
}
