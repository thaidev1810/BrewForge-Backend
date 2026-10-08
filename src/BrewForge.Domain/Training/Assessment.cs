using System.Text.Json;
using BrewForge.Domain.Courses;

namespace BrewForge.Domain.Training;

/// <summary>How a trainee did on the questions of one module. This is what tagging questions by module is for.</summary>
public sealed record ModuleScore(long CourseModuleId, ModuleType ModuleType, int Correct, int Total)
{
    public bool Failed => Correct < Total;
}

/// <summary>What is stored in <c>quiz_attempt.answers_json</c>: the answers as given and the breakdown as scored.</summary>
public sealed record QuizAnswerSheet(IReadOnlyDictionary<long, string?> Answers, IReadOnlyList<ModuleScore> PerModule);

/// <summary>One attempt at the course quiz. The attempt number is capped by the regulation (BR-33).</summary>
public sealed class QuizAttempt
{
    private static readonly JsonSerializerOptions SheetFormat = new(JsonSerializerDefaults.Web);

    private QuizAttempt() { }

    internal QuizAttempt(long quizId, int attemptNo, int score, bool passed, QuizAnswerSheet sheet,
        DateTimeOffset attemptedAt)
    {
        QuizId = quizId;
        AttemptNo = attemptNo;
        Score = score;
        Passed = passed;
        AnswersJson = JsonSerializer.Serialize(sheet, SheetFormat);
        AttemptedAt = attemptedAt;
    }

    public long Id { get; private set; }
    public long QuizId { get; private set; }
    public long EnrollmentId { get; private set; }
    public int AttemptNo { get; private set; }

    /// <summary>Percentage of questions answered correctly.</summary>
    public int Score { get; private set; }
    public bool Passed { get; private set; }
    public string AnswersJson { get; private set; } = null!;
    public DateTimeOffset AttemptedAt { get; private set; }

    public QuizAnswerSheet Sheet() =>
        JsonSerializer.Deserialize<QuizAnswerSheet>(AnswersJson, SheetFormat) ?? new QuizAnswerSheet(new Dictionary<long, string?>(), []);
}

/// <summary>The outcome of a quiz attempt, as the trainee is told it.</summary>
public sealed record QuizResult(QuizAttempt Attempt, int PassScore, int RetakesLeft, IReadOnlyList<ModuleScore> PerModule);

/// <summary>The trainer's verdict on one item of the practical checklist.</summary>
public sealed record ChecklistMark(long RecipeStepId, bool Passed, string? Note);

/// <summary>
/// A trainer's structured observation of a trainee performing the procedure
/// (UC-16). It records what a person saw: there is no image, no video and no
/// automated scoring of movement here, and there is not meant to be (BR-17).
/// </summary>
public sealed class PracticalEvaluation
{
    private static readonly JsonSerializerOptions ChecklistFormat = new(JsonSerializerDefaults.Web);

    private PracticalEvaluation() { }

    internal PracticalEvaluation(long evaluatedBy, IReadOnlyList<ChecklistMark> marks, DateTimeOffset evaluatedAt)
    {
        EvaluatedBy = evaluatedBy;
        ChecklistJson = JsonSerializer.Serialize(marks, ChecklistFormat);
        // Passed only when every item passed. An empty checklist proves nothing.
        Passed = marks.Count > 0 && marks.All(mark => mark.Passed);
        EvaluatedAt = evaluatedAt;
    }

    public long Id { get; private set; }
    public long EnrollmentId { get; private set; }
    public long EvaluatedBy { get; private set; }
    public string ChecklistJson { get; private set; } = null!;
    public bool Passed { get; private set; }
    public DateTimeOffset EvaluatedAt { get; private set; }

    public IReadOnlyList<ChecklistMark> Marks() =>
        JsonSerializer.Deserialize<List<ChecklistMark>>(ChecklistJson, ChecklistFormat) ?? [];
}
