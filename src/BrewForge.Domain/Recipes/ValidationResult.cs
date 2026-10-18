using System.Text.Json;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Domain.Recipes;

/// <summary>
/// The outcome of one validator check for one version, retained for audit.
/// A check that passed leaves one row; a check that failed leaves one row per
/// violation, each naming the offending step. All rows of one run share the
/// same <see cref="RunAt"/>.
/// </summary>
public sealed class ValidationResult
{
    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    private ValidationResult() { }

    public long Id { get; private set; }
    public long RecipeVersionId { get; private set; }
    public CheckType CheckType { get; private set; }
    public bool Passed { get; private set; }

    /// <summary>The offending step. The database clears it if the step is later replaced.</summary>
    public long? StepId { get; private set; }

    /// <summary><c>{rule, message, expected, actual}</c>, plus the step order and message code for display.</summary>
    public string? ViolationDetail { get; private set; }
    public DateTimeOffset RunAt { get; private set; }

    public static IReadOnlyList<ValidationResult> FromReport(long recipeVersionId, ValidationReport report,
        DateTimeOffset runAt)
    {
        var rows = new List<ValidationResult>();
        foreach (var check in report.Checks)
        {
            if (check.Passed)
            {
                rows.Add(new ValidationResult
                {
                    RecipeVersionId = recipeVersionId, CheckType = check.CheckType, Passed = true, RunAt = runAt,
                });
                continue;
            }

            rows.AddRange(check.Violations.Select(violation => new ValidationResult
            {
                RecipeVersionId = recipeVersionId,
                CheckType = check.CheckType,
                Passed = false,
                // Zero is the id of a step that has not been stored yet.
                StepId = violation.StepId is > 0 ? violation.StepId : null,
                ViolationDetail = JsonSerializer.Serialize(new
                {
                    violation.Rule, violation.Message, violation.Expected, violation.Actual, violation.StepOrder,
                    violation.Code, violation.Variant,
                }, DetailJson),
                RunAt = runAt,
            }));
        }
        return rows;
    }
}

/// <summary>
/// The prompt, model and raw response of one LLM call, so that a released
/// recipe can be traced to the generation that produced it (BR-06). Written
/// for every call, including the ones that fail.
/// </summary>
public sealed class AiDraftLog
{
    private AiDraftLog() { }

    public AiDraftLog(long recipeId, string promptText, string modelName, string? rawResponse, bool schemaValid,
        DateTimeOffset createdAt)
    {
        RecipeId = recipeId;
        PromptText = promptText;
        ModelName = modelName.Length > 64 ? modelName[..64] : modelName;
        RawResponse = ToJson(rawResponse);
        SchemaValid = schemaValid;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }
    public long RecipeId { get; private set; }
    public string PromptText { get; private set; } = null!;
    public string ModelName { get; private set; } = null!;

    /// <summary>JSONB. Null when the call itself failed and nothing came back.</summary>
    public string? RawResponse { get; private set; }

    /// <summary>False when the response broke the schema (BR-07).</summary>
    public bool SchemaValid { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The column is JSONB, and the whole point of this log is to keep what
    /// the model said even when it is not JSON. Such a response is stored as
    /// a JSON string, verbatim.
    /// </summary>
    private static string? ToJson(string? raw)
    {
        if (raw is null) return null;
        try
        {
            using var _ = JsonDocument.Parse(raw);
            return raw;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(raw);
        }
    }
}
