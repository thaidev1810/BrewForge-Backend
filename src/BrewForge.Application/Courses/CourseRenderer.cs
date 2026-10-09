using BrewForge.Application.Abstractions;
using BrewForge.Application.Recipes;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Courses;

/// <summary>
/// Turns a course into what a reader sees. This is where BR-22 happens: every
/// quantity, threshold, technique gate and shelf-life rule a lesson shows is
/// read here, from the bound recipe version and the catalogue, at the moment
/// of the request. Nothing of it is taken from the lesson row, so a trainer
/// cannot overwrite it and it cannot go stale.
/// </summary>
public sealed class CourseRenderer(IBrewForgeDbContext db)
{
    /// <summary>Everything a rendering needs from the recipe side, loaded once per request.</summary>
    public sealed record Source(Recipe Recipe, RecipeVersion Version, IReadOnlyDictionary<long, Ingredient> Ingredients,
        IReadOnlyDictionary<string, StandardEquipment> Equipment);

    public async Task<Source?> LoadSourceAsync(long? recipeVersionId, CancellationToken cancellationToken)
    {
        if (recipeVersionId is null) return null;

        var version = await db.FindVersionAsync(recipeVersionId.Value, cancellationToken);
        var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);
        var (equipment, ingredients) = await db.LoadCatalogAsync(cancellationToken);
        return new Source(recipe, version, ingredients.ToDictionary(i => i.Id),
            equipment.ToDictionary(e => e.EquipmentClass, StringComparer.Ordinal));
    }

    public async Task<CourseDetailDto> RenderAsync(Course course, CancellationToken cancellationToken)
    {
        var source = await LoadSourceAsync(course.RecipeVersionId, cancellationToken);
        return new CourseDetailDto(course.Id, course.RecipeVersionId, course.CourseType, course.Title,
            course.TotalDurationMin, course.CreatedBy, course.ApprovedBy, course.State, course.PublishedAt,
            source?.Recipe.Id, source?.Recipe.RecipeCode, source?.Recipe.Name, source?.Version.VersionNo,
            RenderModules(course, source), ToDto(course.Quiz), RenderChecklist(course, source));
    }

    public static IReadOnlyList<CourseModuleDto> RenderModules(Course course, Source? source) =>
        [.. course.OrderedModules().Select(module => RenderModule(module, source))];

    public static CourseModuleDto RenderModule(CourseModule module, Source? source) =>
        new(module.Id, module.CourseId, module.ModuleType, module.ModuleOrder, module.Source, module.DurationMinutes,
            module.State, [.. module.OrderedLessons().Select(lesson => RenderLesson(module, lesson, source))]);

    public static LessonDto RenderLesson(CourseModule module, Lesson lesson, Source? source)
    {
        var generated = module.Source == ModuleSource.Generated || lesson.RecipeStepId is not null;
        return new LessonDto(lesson.Id, lesson.LessonOrder, lesson.Title, lesson.RecipeStepId, lesson.Content,
            lesson.MediaUrl, generated, generated && source is not null ? Reference(module, lesson, source) : null);
    }

    public static QuizDto ToDto(Quiz quiz) => new(quiz.Id, quiz.CourseId, quiz.Title, quiz.PassScore, quiz.QuestionCount);

    /// <summary>
    /// The practical checklist: the gate items of the TECHNIQUE module, with
    /// their steps as they are now, or its lessons where the course is built
    /// from no recipe.
    /// </summary>
    public static IReadOnlyList<ChecklistItemDto> RenderChecklist(Course course, Source? source)
    {
        if (course.RecipeVersionId is null)
        {
            return
            [
                .. course.PracticalChecklist().Select(lesson =>
                    new ChecklistItemDto(lesson.Id, null, lesson.LessonOrder, lesson.Title, null)),
            ];
        }
        if (source is null) return [];
        var steps = source.Version.Steps.ToDictionary(step => step.Id);
        var technique = course.Modules.Single(module => module.ModuleType == ModuleType.Technique);
        return
        [
            .. technique.Lessons
                .Where(lesson => lesson.RecipeStepId is { } id && steps.ContainsKey(id))
                .Select(lesson => (Lesson: lesson, Step: steps[lesson.RecipeStepId!.Value]))
                .OrderBy(item => item.Step.StepOrder)
                .Select(item => new ChecklistItemDto(item.Lesson.Id, item.Step.Id, item.Step.StepOrder,
                    item.Step.ActionText, item.Step.TechniqueGate)),
        ];
    }

    private static object? Reference(CourseModule module, Lesson lesson, Source source)
    {
        var steps = source.Version.OrderedSteps();
        var step = lesson.RecipeStepId is { } stepId ? steps.SingleOrDefault(s => s.Id == stepId) : null;

        return module.ModuleType switch
        {
            ModuleType.ProductOverview => new OverviewReference(source.Recipe.RecipeCode, source.Recipe.Name,
                source.Recipe.Category, source.Version.VersionNo, steps.Count,
                steps.Sum(s => s.DurationSeconds ?? 0)),

            ModuleType.Ingredients => new IngredientsReference(
            [
                .. steps.SelectMany(s => s.Ingredients.Select(i => (Step: s, Use: i)))
                    .GroupBy(x => x.Use.IngredientId)
                    .Select(group =>
                    {
                        var ingredient = source.Ingredients.GetValueOrDefault(group.Key);
                        var unit = ingredient?.Unit.Code() ?? group.First().Use.Unit;
                        // Summed in the ingredient's own unit, whatever unit each step used.
                        var total = group.Sum(x =>
                            UnitConverter.TryConvert(x.Use.Quantity, x.Use.Unit, unit, out var converted)
                                ? converted
                                : x.Use.Quantity);
                        return new IngredientReference(ingredient?.IngredientCode ?? $"#{group.Key}",
                            ingredient?.Name ?? "(unknown ingredient)", total, unit,
                            ingredient?.ShelfLifeHours ?? 0, ingredient?.StorageRule,
                            ingredient?.Status ?? CatalogStatus.Inactive,
                            [.. group.Select(x => x.Step.StepOrder).Distinct().Order()]);
                    })
                    .OrderBy(reference => reference.IngredientCode, StringComparer.Ordinal),
            ]),

            ModuleType.Equipment => new EquipmentListReference(
            [
                .. steps.Where(s => s.EquipmentClass is not null)
                    .GroupBy(s => s.EquipmentClass!)
                    .Select(group =>
                    {
                        var equipment = source.Equipment.GetValueOrDefault(group.Key);
                        return new EquipmentReference(group.Key, equipment?.EquipmentCode, equipment?.MinThreshold,
                            equipment?.MaxThreshold, equipment?.DosingUnit, [.. group.Select(s => s.StepOrder).Order()]);
                    })
                    .OrderBy(reference => reference.EquipmentClass, StringComparer.Ordinal),
            ]),

            ModuleType.Sop when step is not null => new StepReference(step.StepOrder, step.ActionText,
                step.EquipmentClass, step.TechniqueGate, step.DurationSeconds,
                [
                    .. step.Ingredients.Select(i =>
                    {
                        var ingredient = source.Ingredients.GetValueOrDefault(i.IngredientId);
                        return new StepIngredientReference(ingredient?.IngredientCode ?? $"#{i.IngredientId}",
                            ingredient?.Name ?? "(unknown ingredient)", i.Quantity, i.Unit);
                    }),
                ],
                [.. step.Dependencies.Select(d => d.DependsOnStep.StepOrder).Order()]),

            ModuleType.Technique when step is not null =>
                new TechniqueGateReference(step.StepOrder, step.ActionText, step.TechniqueGate),

            _ => null,
        };
    }
}
