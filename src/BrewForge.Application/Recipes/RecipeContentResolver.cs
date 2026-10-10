using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Recipes;

/// <summary>
/// Shared reads of the recipe services: loading a version as a whole
/// aggregate, loading the catalogue, and turning request and response shapes
/// into and out of the domain model.
/// </summary>
internal static class RecipeContentResolver
{
    public static IQueryable<RecipeVersion> WithContent(this IQueryable<RecipeVersion> versions) =>
        versions
            .Include(version => version.Steps).ThenInclude(step => step.Ingredients)
            .Include(version => version.Steps).ThenInclude(step => step.Dependencies);

    public static async Task<RecipeVersion> FindVersionAsync(this IBrewForgeDbContext db, long id,
        CancellationToken cancellationToken) =>
        await db.RecipeVersions.WithContent().SingleOrDefaultAsync(version => version.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Recipe version", id);

    /// <summary>
    /// The version of the same recipe that was in production before this one:
    /// the highest version number below it that was ever released. Null for
    /// the first version of a recipe.
    /// </summary>
    public static async Task<RecipeVersion?> FindPreviousVersionAsync(this IBrewForgeDbContext db,
        RecipeVersion version, CancellationToken cancellationToken) =>
        await db.RecipeVersions.WithContent()
            .Where(v => v.RecipeId == version.RecipeId && v.VersionNo < version.VersionNo
                        && (v.State == VersionState.Released || v.State == VersionState.Superseded))
            .OrderByDescending(v => v.VersionNo)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The whole catalogue, inactive entries included, as the validator wants it.</summary>
    public static async Task<(List<StandardEquipment> Equipment, List<Ingredient> Ingredients)> LoadCatalogAsync(
        this IBrewForgeDbContext db, CancellationToken cancellationToken) =>
        (await db.StandardEquipment.AsNoTracking().ToListAsync(cancellationToken),
            await db.Ingredients.AsNoTracking().ToListAsync(cancellationToken));

    public static async Task<ValidationCatalog> LoadValidationCatalogAsync(this IBrewForgeDbContext db,
        CancellationToken cancellationToken)
    {
        var (equipment, ingredients) = await db.LoadCatalogAsync(cancellationToken);
        return new ValidationCatalog(equipment, ingredients);
    }

    public static async Task<RecipeVersionDto> ToDtoAsync(this IBrewForgeDbContext db, RecipeVersion version,
        CancellationToken cancellationToken)
    {
        var ids = version.Steps.SelectMany(step => step.Ingredients).Select(i => i.IngredientId).Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking()
            .Where(ingredient => ids.Contains(ingredient.Id))
            .ToDictionaryAsync(ingredient => ingredient.Id, cancellationToken);
        return ToDto(version, ingredients);
    }

    public static RecipeVersionDto ToDto(RecipeVersion version, IReadOnlyDictionary<long, Ingredient> ingredients) =>
        new(version.Id, version.RecipeId, version.VersionNo, version.State, version.IsImmutable, version.ContentHash,
            version.CreatedBy, version.ApprovedBy, version.ReleasedAt, version.SupersededAt,
        [
            .. version.OrderedSteps().Select(step => new StepDto(step.Id, step.StepOrder, step.ActionText,
                step.EquipmentClass, step.TechniqueGate, step.DurationSeconds,
                [
                    .. step.Ingredients.OrderBy(i => i.Id).Select(i =>
                    {
                        var ingredient = ingredients.GetValueOrDefault(i.IngredientId);
                        return new StepIngredientDto(i.IngredientId, ingredient?.IngredientCode, ingredient?.Name,
                            i.Quantity, i.Unit);
                    }),
                ],
                [
                    .. step.Dependencies.OrderBy(d => d.DependsOnStep.StepOrder).Select(d =>
                        new StepDependencyDto(d.DependsOnStepId, d.DependsOnStep.StepOrder, d.DependencyType)),
                ])),
        ]);

    /// <summary>
    /// Turns the request into step specifications, resolving ingredient codes
    /// to ids and step ids to step orders. A reference to master data that
    /// does not exist cannot be stored at all, so it is refused here as a 400;
    /// a reference to master data that exists but is inactive is stored and
    /// left for the validator to report.
    /// </summary>
    public static async Task<IReadOnlyList<StepSpec>> ResolveAsync(this IBrewForgeDbContext db,
        RecipeContentRequest request, RecipeVersion current, CancellationToken cancellationToken)
    {
        var steps = request.Steps ?? throw DomainException.Validation("The draft content is missing.",
            new ErrorDetail("steps", "is required"));
        var (equipment, ingredients) = await db.LoadCatalogAsync(cancellationToken);
        var equipmentClasses = equipment.Select(e => e.EquipmentClass).ToHashSet(StringComparer.Ordinal);
        var ingredientsById = ingredients.ToDictionary(i => i.Id);
        var ingredientsByCode = ingredients.ToDictionary(i => i.IngredientCode, StringComparer.Ordinal);
        var orderOfExistingStep = current.Steps.ToDictionary(step => step.Id, step => step.StepOrder);

        var errors = new FieldErrors();
        var specs = new List<StepSpec>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = $"steps[{i}]";
            errors.Check(step.StepOrder is not null, $"{at}.stepOrder", "is required");

            var equipmentClass = string.IsNullOrWhiteSpace(step.EquipmentClass) ? null : step.EquipmentClass.Trim();
            errors.Check(equipmentClass is null || equipmentClasses.Contains(equipmentClass), $"{at}.equipmentClass",
                $"'{equipmentClass}' is not a class of the standard equipment catalogue");

            var ingredientSpecs = new List<IngredientSpec>();
            var used = step.Ingredients ?? [];
            for (var j = 0; j < used.Count; j++)
            {
                var ingredientAt = $"{at}.ingredients[{j}]";
                var ingredient = used[j].IngredientId is { } id
                    ? ingredientsById.GetValueOrDefault(id)
                    : used[j].IngredientCode is { } code
                        ? ingredientsByCode.GetValueOrDefault(code.Trim())
                        : null;

                errors.Check(used[j].IngredientId is not null || used[j].IngredientCode is not null,
                        $"{ingredientAt}.ingredientId", "is required")
                    .Check(ingredient is not null || (used[j].IngredientId is null && used[j].IngredientCode is null),
                        $"{ingredientAt}.ingredientId", "is not an ingredient of the catalogue")
                    .Check(used[j].Quantity is not null, $"{ingredientAt}.quantity", "is required");

                if (ingredient is not null && used[j].Quantity is { } quantity)
                {
                    ingredientSpecs.Add(new IngredientSpec(ingredient.Id, quantity, used[j].Unit));
                }
            }

            var dependencySpecs = new List<DependencySpec>();
            var dependsOn = step.DependsOn ?? [];
            for (var j = 0; j < dependsOn.Count; j++)
            {
                int? order = dependsOn[j].StepOrder
                             ?? (dependsOn[j].StepId is { } stepId && orderOfExistingStep.TryGetValue(stepId, out var known)
                                 ? known
                                 : null);
                errors.Check(order is not null, $"{at}.dependsOn[{j}]",
                    "must name a step by stepOrder, or by the stepId of an existing step of this version");
                if (order is not null)
                {
                    dependencySpecs.Add(new DependencySpec(order.Value,
                        dependsOn[j].Type ?? DependencyType.FinishToStart));
                }
            }

            specs.Add(new StepSpec(step.StepOrder ?? 0, step.ActionText, equipmentClass, step.TechniqueGate,
                step.DurationSeconds, ingredientSpecs, dependencySpecs));
        }

        errors.ThrowIfAny();
        return specs;
    }
}
