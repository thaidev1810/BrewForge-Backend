using System.Text.Json;
using System.Text.Json.Serialization;
using BrewForge.Domain.Common;

namespace BrewForge.Domain.Launch;

public enum CriterionType
{
    /// <summary>Cups per day per branch.</summary>
    Absolute,

    /// <summary>Percent of a named control drink.</summary>
    Relative,

    /// <summary>Maximum percentage drop from the first half of the period to the second.</summary>
    Retention,
}

/// <summary>One success criterion of a pilot. Only the fields of its own kind are set.</summary>
public sealed record PilotCriterion(CriterionType Type, decimal? CupsPerDayPerBranch = null,
    long? ControlRecipeId = null, decimal? MinPercentOfControl = null, decimal? MaxDropSecondHalfPct = null)
{
    /// <summary>The figure the pilot is measured against.</summary>
    [JsonIgnore]
    public decimal Target => Type switch
    {
        CriterionType.Absolute => CupsPerDayPerBranch ?? 0,
        CriterionType.Relative => MinPercentOfControl ?? 0,
        _ => MaxDropSecondHalfPct ?? 0,
    };
}

/// <summary>
/// The success criteria of a pilot, as stored in <c>criteria_json</c>:
/// <c>{ "criteria": [ { "type": "ABSOLUTE", "cupsPerDayPerBranch": 40 }, ... ] }</c>.
/// A set of criteria is valid or it does not exist.
/// </summary>
public sealed class PilotCriteria
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new EnumCodeJsonConverterFactory() },
    };

    private PilotCriteria(IReadOnlyList<PilotCriterion> criteria)
    {
        Criteria = criteria;
    }

    public IReadOnlyList<PilotCriterion> Criteria { get; }

    /// <summary>The control drink of the RELATIVE criterion, if there is one.</summary>
    public long? ControlRecipeId =>
        Criteria.FirstOrDefault(criterion => criterion.Type == CriterionType.Relative)?.ControlRecipeId;

    public static PilotCriteria Create(IEnumerable<PilotCriterion>? criteria)
    {
        var list = (criteria ?? []).ToList();
        var errors = new FieldErrors()
            .Check(list.Count > 0, "criteria", "at least one criterion is required")
            .Check(list.Select(criterion => criterion.Type).Distinct().Count() == list.Count, "criteria",
                "each kind of criterion may appear once");

        for (var i = 0; i < list.Count; i++)
        {
            var (criterion, at) = (list[i], $"criteria[{i}]");
            errors.Check(Enum.IsDefined(criterion.Type), $"{at}.type", "must be ABSOLUTE, RELATIVE or RETENTION");
            switch (criterion.Type)
            {
                case CriterionType.Absolute:
                    errors.Check(criterion.CupsPerDayPerBranch is > 0 and <= 100_000, $"{at}.cupsPerDayPerBranch",
                            "is required and must be greater than 0")
                        .Check(criterion is { ControlRecipeId: null, MinPercentOfControl: null, MaxDropSecondHalfPct: null },
                            at, "an ABSOLUTE criterion has cupsPerDayPerBranch and nothing else");
                    break;
                case CriterionType.Relative:
                    errors.Check(criterion.ControlRecipeId is not null, $"{at}.controlRecipeId", "is required")
                        .Check(criterion.MinPercentOfControl is > 0 and <= 1000, $"{at}.minPercentOfControl",
                            "is required and must be greater than 0")
                        .Check(criterion is { CupsPerDayPerBranch: null, MaxDropSecondHalfPct: null },
                            at, "a RELATIVE criterion has controlRecipeId and minPercentOfControl and nothing else");
                    break;
                case CriterionType.Retention:
                    errors.Check(criterion.MaxDropSecondHalfPct is >= 0 and <= 100, $"{at}.maxDropSecondHalfPct",
                            "is required and must be between 0 and 100")
                        .Check(criterion is { CupsPerDayPerBranch: null, ControlRecipeId: null, MinPercentOfControl: null },
                            at, "a RETENTION criterion has maxDropSecondHalfPct and nothing else");
                    break;
            }
        }
        errors.ThrowIfAny();
        return new PilotCriteria(list);
    }

    public string ToJson() => JsonSerializer.Serialize(new Document(Criteria), Format);

    public static PilotCriteria FromJson(string json) =>
        new(JsonSerializer.Deserialize<Document>(json, Format)?.Criteria ?? []);

    private sealed record Document(IReadOnlyList<PilotCriterion> Criteria);
}
