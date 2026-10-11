using BrewForge.Domain.Courses;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;

namespace BrewForge.Application.Courses;

public sealed record CourseDto(long Id, long? RecipeVersionId, CourseType CourseType, string Title,
    int? TotalDurationMin, long CreatedBy, long? ApprovedBy, CourseState State, DateTimeOffset? PublishedAt,
    long? RecipeId, string? RecipeCode, string? RecipeName, int? VersionNo);

public sealed record CourseDetailDto(long Id, long? RecipeVersionId, CourseType CourseType, string Title,
    int? TotalDurationMin, long CreatedBy, long? ApprovedBy, CourseState State, DateTimeOffset? PublishedAt,
    long? RecipeId, string? RecipeCode, string? RecipeName, int? VersionNo,
    IReadOnlyList<CourseModuleDto> Modules, QuizDto Quiz, IReadOnlyList<ChecklistItemDto> PracticalChecklist,
    RecertificationDto? Recertification = null);

/// <summary>
/// On a RECERTIFICATION course: the version its learners are certified on,
/// and how many steps differ in the bound one. The differences themselves
/// are served by <c>GET /recipe-versions/{id}/diff</c>.
/// </summary>
public sealed record RecertificationDto(long FromVersionId, int FromVersionNo, int Added, int Removed, int Changed);


public sealed record CourseModuleDto(long Id, long CourseId, ModuleType ModuleType, int ModuleOrder,
    ModuleSource Source, int? DurationMinutes, ModuleState State, IReadOnlyList<LessonDto> Lessons);

/// <summary>
/// <c>Content</c> is what the trainer wrote. <c>Reference</c> is what the
/// lesson shows from the recipe; it is computed from the bound version every
/// time the lesson is read and is never stored with the lesson (BR-22).
/// </summary>
public sealed record LessonDto(long Id, int LessonOrder, string Title, long? RecipeStepId, string? Content,
    string? MediaUrl, bool Generated, object? Reference,
    IReadOnlyList<LessonMediaDto>? Media = null);

/// <summary>A picture of a lesson. <c>Url</c> is where its content is served, relative to the API.</summary>
public sealed record LessonMediaDto(long Id, long LessonId, int SortOrder, string FileName, string ContentType,
    long SizeBytes, string Url, DateTimeOffset UploadedAt)
{
    public static LessonMediaDto From(Domain.Courses.LessonMedia media) =>
        new(media.Id, media.LessonId, media.SortOrder, media.FileName, media.ContentType, media.SizeBytes,
            $"/api/v1/lesson-media/{media.Id}", media.UploadedAt);
}

/// <summary>A picture as it is served: its content and what the response says about it.</summary>
public sealed record LessonMediaContent(Stream Content, string ContentType, string FileName);

public sealed record QuizDto(long Id, long CourseId, string Title, int PassScore, int QuestionCount);

public sealed record QuizQuestionDto(long Id, long CourseModuleId, ModuleType ModuleType, string QuestionText,
    IReadOnlyList<QuizOption> Options, string CorrectOption);

/// <summary>
/// One item the trainer observes in the practical evaluation: a step of the
/// bound version, or, on a course built from no recipe, a lesson of the
/// TECHNIQUE module. In that case <c>RecipeStepId</c> is null, the order is
/// that of the lesson and the text is its title.
/// </summary>
public sealed record ChecklistItemDto(long LessonId, long? RecipeStepId, int StepOrder, string ActionText,
    string? TechniqueGate);

// ---------------------------------------------------------------- reference values (rendered, never stored)

public sealed record OverviewReference(string RecipeCode, string RecipeName, RecipeCategory Category, int VersionNo,
    int StepCount, int TotalDurationSeconds);

public sealed record IngredientsReference(IReadOnlyList<IngredientReference> Ingredients);

public sealed record IngredientReference(string IngredientCode, string Name, decimal TotalQuantity, string Unit,
    int ShelfLifeHours, string? StorageRule, CatalogStatus Status, IReadOnlyList<int> UsedInSteps);

public sealed record EquipmentListReference(IReadOnlyList<EquipmentReference> Equipment);

public sealed record EquipmentReference(string EquipmentClass, string? EquipmentCode, decimal? MinThreshold,
    decimal? MaxThreshold, DosingUnit? DosingUnit, IReadOnlyList<int> UsedInSteps);

public sealed record StepReference(int StepOrder, string ActionText, string? EquipmentClass, string? TechniqueGate,
    int? DurationSeconds, IReadOnlyList<StepIngredientReference> Ingredients, IReadOnlyList<int> DependsOnSteps);

public sealed record StepIngredientReference(string IngredientCode, string Name, decimal Quantity, string Unit);

public sealed record TechniqueGateReference(int StepOrder, string ActionText, string? TechniqueGate);

// ---------------------------------------------------------------- requests

public sealed record CreateCourseRequest(long? RecipeVersionId, CourseType? CourseType, string? Title);

public sealed record UpdateModuleRequest(int? DurationMinutes);

public sealed record LessonRequest(string? Title, string? Content, string? MediaUrl);

public sealed record QuizRequest(string? Title, int? PassScore);

public sealed record QuizQuestionRequest(long? CourseModuleId, string? QuestionText,
    IReadOnlyList<QuizOptionRequest>? Options, string? CorrectOption);

public sealed record QuizOptionRequest(string? Key, string? Text);

public sealed record ChecklistRequest(IReadOnlyList<ChecklistItemRequest>? Items);

public sealed record ChecklistItemRequest(long? RecipeStepId);

public sealed record ReturnCourseRequest(string? Comment);
