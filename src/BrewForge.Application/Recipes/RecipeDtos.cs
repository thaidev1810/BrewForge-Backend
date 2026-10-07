using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Application.Recipes;

// ---------------------------------------------------------------- recipes

public sealed record RecipeDto(long Id, string RecipeCode, string Name, RecipeCategory Category, RecipeOrigin Origin,
    RecipeStatus Status, long CreatedBy, DateTimeOffset CreatedAt, long? ReleasedVersionId, int? ReleasedVersionNo,
    int VersionCount);

public sealed record CreateRecipeRequest(string? RecipeCode, string? Name, RecipeCategory? Category,
    RecipeOrigin? Origin);

/// <summary>One row of the version history (SCR-12).</summary>
public sealed record RecipeVersionSummaryDto(long Id, long RecipeId, int VersionNo, VersionState State,
    bool IsImmutable, long CreatedBy, long? ApprovedBy, DateTimeOffset? ReleasedAt, DateTimeOffset? SupersededAt,
    int StepCount, bool? LastValidationPassed, DateTimeOffset? LastValidatedAt);

/// <summary><c>CopyFromVersionId</c> is optional: the new draft starts with that version's content.</summary>
public sealed record CreateVersionRequest(long? CopyFromVersionId);

// ---------------------------------------------------------------- the version tree

public sealed record RecipeVersionDto(long Id, long RecipeId, int VersionNo, VersionState State, bool IsImmutable,
    string? ContentHash, long CreatedBy, long? ApprovedBy, DateTimeOffset? ReleasedAt, DateTimeOffset? SupersededAt,
    IReadOnlyList<StepDto> Steps);

public sealed record StepDto(long Id, int StepOrder, string ActionText, string? EquipmentClass,
    string? TechniqueGate, int? DurationSeconds, IReadOnlyList<StepIngredientDto> Ingredients,
    IReadOnlyList<StepDependencyDto> DependsOn);

public sealed record StepIngredientDto(long IngredientId, string? IngredientCode, string? IngredientName,
    decimal Quantity, string Unit);

public sealed record StepDependencyDto(long StepId, int StepOrder, DependencyType Type);

// ---------------------------------------------------------------- authoring

/// <summary>The whole content of a draft. A PUT replaces the content; it does not patch it.</summary>
public sealed record RecipeContentRequest(IReadOnlyList<StepRequest>? Steps);

public sealed record StepRequest(int? StepOrder, string? ActionText, string? EquipmentClass, string? TechniqueGate,
    int? DurationSeconds, IReadOnlyList<StepIngredientRequest>? Ingredients,
    IReadOnlyList<StepDependencyRequest>? DependsOn);

/// <summary>The ingredient is named by <c>IngredientId</c> or, failing that, by <c>IngredientCode</c>.</summary>
public sealed record StepIngredientRequest(long? IngredientId, string? IngredientCode, decimal? Quantity, string? Unit);

/// <summary>
/// The prerequisite step is named by its <c>StepOrder</c> in this request or,
/// failing that, by the <c>StepId</c> it had when the draft was last read.
/// </summary>
public sealed record StepDependencyRequest(int? StepOrder, long? StepId, DependencyType? Type);

// ---------------------------------------------------------------- validation

public sealed record ValidationResultDto(long VersionId, bool Passed, DateTimeOffset RunAt,
    IReadOnlyList<CheckDto> Checks)
{
    public static ValidationResultDto From(long versionId, ValidationReport report, DateTimeOffset runAt) =>
        new(versionId, report.Passed, runAt,
        [
            .. report.Checks.Select(check => new CheckDto(check.CheckType, check.Passed,
                [.. check.Violations.Select(v => new ViolationDto(v.StepId is > 0 ? v.StepId : null, v.StepOrder, v.Rule,
                    v.Code, v.Message, v.Expected, v.Actual))])),
        ]);
}

public sealed record CheckDto(CheckType CheckType, bool Passed, IReadOnlyList<ViolationDto> Violations);

/// <summary>A violation, attached to the step that caused it.</summary>
public sealed record ViolationDto(long? StepId, int? StepOrder, string Rule, string? Code, string Message,
    string Expected, string Actual);

// ---------------------------------------------------------------- drafting and repair

public sealed record GenerateDraftRequest(string? Description);

public enum RepairMode
{
    Ai,
    Manual,
}

public sealed record RepairRequest(RepairMode? Mode, RecipeContentRequest? Patch);

/// <summary>A draft together with the validation that was run on it straight away.</summary>
public sealed record DraftResultDto(RecipeVersionDto Version, ValidationResultDto Validation, string? Notes,
    int? ServingSizeMl, int AiRepairsUsed, int AiRepairsLeft);
