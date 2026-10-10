using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Recipes;

/// <summary>UC-05: structured recipe authoring (SCR-07, SCR-08, SCR-12).</summary>
public sealed class RecipeService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly SortMap<Recipe> Sorting = new SortMap<Recipe>("recipeCode", r => r.Id)
        .Add("id", r => r.Id)
        .Add("recipeCode", r => r.RecipeCode)
        .Add("name", r => r.Name)
        .Add("category", r => r.Category)
        .Add("origin", r => r.Origin)
        .Add("status", r => r.Status)
        .Add("createdAt", r => r.CreatedAt);

    public async Task<PagedResult<RecipeDto>> ListAsync(PageQuery paging, string? keyword, string? category,
        string? origin, string? status, CancellationToken cancellationToken)
    {
        var categoryFilter = PagingExtensions.ParseFilter<RecipeCategory>(category, "category");
        var originFilter = PagingExtensions.ParseFilter<RecipeOrigin>(origin, "origin");
        var statusFilter = PagingExtensions.ParseFilter<RecipeStatus>(status, "status");

        var query = db.Recipes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(r => r.RecipeCode.ToLower().Contains(term) || r.Name.ToLower().Contains(term));
        }
        if (categoryFilter is not null) query = query.Where(r => r.Category == categoryFilter);
        if (originFilter is not null) query = query.Where(r => r.Origin == originFilter);
        if (statusFilter is not null) query = query.Where(r => r.Status == statusFilter);

        var page = await query.ToPagedAsync(paging, Sorting, recipe => recipe, cancellationToken);
        var versions = await VersionFactsAsync([.. page.Items.Select(r => r.Id)], cancellationToken);
        return new PagedResult<RecipeDto>([.. page.Items.Select(recipe => ToDto(recipe, versions))], page.Page,
            page.Size, page.Total);
    }

    public async Task<RecipeDto> GetAsync(long id, CancellationToken cancellationToken)
    {
        var recipe = await FindRecipeAsync(id, cancellationToken);
        return ToDto(recipe, await VersionFactsAsync([id], cancellationToken));
    }

    public async Task<RecipeDto> CreateAsync(CreateRecipeRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.Category is not null, "category", "is required").ThrowIfAny();

        var recipe = Recipe.Create(request.RecipeCode!, request.Name!, request.Category!.Value,
            request.Origin ?? RecipeOrigin.New, currentUser.RequireUserId(), clock.GetUtcNow());

        if (await db.Recipes.AnyAsync(r => r.RecipeCode == recipe.RecipeCode, cancellationToken))
        {
            throw DomainException.RuleViolation("UNIQUE", $"Recipe code '{recipe.RecipeCode}' already exists.",
                details: new ErrorDetail("recipeCode", "already exists"));
        }

        db.Recipes.Add(recipe);
        db.Audit(AuditEntities.Recipe, () => recipe.Id, AuditActions.Create,
            new { recipe.RecipeCode, recipe.Name, category = recipe.Category.Code(), origin = recipe.Origin.Code() });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(recipe, []);
    }

    public async Task<IReadOnlyList<RecipeVersionSummaryDto>> ListVersionsAsync(long recipeId,
        CancellationToken cancellationToken)
    {
        await FindRecipeAsync(recipeId, cancellationToken);

        var versions = await db.RecipeVersions.AsNoTracking()
            .Where(v => v.RecipeId == recipeId)
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new { Version = v, StepCount = v.Steps.Count() })
            .ToListAsync(cancellationToken);
        var ids = versions.Select(v => v.Version.Id).ToList();

        // The outcome of the latest run of each version: a run is all rows sharing one run_at.
        var runs = await db.ValidationResults.AsNoTracking()
            .Where(r => ids.Contains(r.RecipeVersionId))
            .GroupBy(r => new { r.RecipeVersionId, r.RunAt })
            .Select(g => new { g.Key.RecipeVersionId, g.Key.RunAt, Passed = g.Count(r => !r.Passed) == 0 })
            .ToListAsync(cancellationToken);
        var latest = runs.GroupBy(r => r.RecipeVersionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RunAt).First());

        return
        [
            .. versions.Select(v =>
            {
                var run = latest.GetValueOrDefault(v.Version.Id);
                return new RecipeVersionSummaryDto(v.Version.Id, v.Version.RecipeId, v.Version.VersionNo,
                    v.Version.State, v.Version.IsImmutable, v.Version.CreatedBy, v.Version.ApprovedBy,
                    v.Version.ReleasedAt, v.Version.SupersededAt, v.StepCount, run?.Passed, run?.RunAt);
            }),
        ];
    }

    /// <summary>
    /// Creates a DRAFT version with the next number of the recipe. Numbers
    /// are never reused, because versions are never deleted (BR-03).
    /// </summary>
    public async Task<RecipeVersionDto> CreateVersionAsync(long recipeId, CreateVersionRequest? request,
        CancellationToken cancellationToken)
    {
        await FindRecipeAsync(recipeId, cancellationToken);
        var authorId = currentUser.RequireUserId();

        var version = RecipeVersion.CreateDraft(recipeId, await NextVersionNoAsync(recipeId, cancellationToken),
            authorId);

        if (request?.CopyFromVersionId is { } sourceId)
        {
            // Tracked on purpose: tracking is what links each dependency to the step it points at.
            var source = await db.RecipeVersions.WithContent()
                .SingleOrDefaultAsync(v => v.Id == sourceId && v.RecipeId == recipeId, cancellationToken)
                ?? throw DomainException.Validation("The version to copy from does not belong to this recipe.",
                    new ErrorDetail("copyFromVersionId", "is not a version of this recipe"));
            version.ReplaceContent(source.ToSpecs(), authorId);
        }

        db.RecipeVersions.Add(version);
        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Create,
            new { recipeId, version.VersionNo, copiedFrom = request?.CopyFromVersionId });
        await db.SaveChangesAsync(cancellationToken);
        return await db.ToDtoAsync(version, cancellationToken);
    }

    public async Task<RecipeVersionDto> GetVersionAsync(long id, CancellationToken cancellationToken) =>
        await db.ToDtoAsync(await db.FindVersionAsync(id, cancellationToken), cancellationToken);

    /// <summary>
    /// What differs between a version and an earlier one of the same recipe,
    /// step by step. Without <paramref name="againstId"/> the earlier one is
    /// the version that was in production before it.
    /// </summary>
    public async Task<RecipeVersionDiffDto> DiffAsync(long id, long? againstId, CancellationToken cancellationToken)
    {
        var after = await db.FindVersionAsync(id, cancellationToken);
        var before = againstId is { } other
            ? await db.FindVersionAsync(other, cancellationToken)
            : await db.FindPreviousVersionAsync(after, cancellationToken)
              ?? throw DomainException.RuleViolation("NO_EARLIER_VERSION",
                  $"Version {after.VersionNo} is the first version of its recipe that was released; there is nothing to compare it with.");
        if (before.RecipeId != after.RecipeId)
        {
            throw DomainException.Validation("Two versions are compared only when they are versions of one recipe.",
                new ErrorDetail("against", "is a version of another recipe"));
        }

        var diff = RecipeVersionDiff.Between(before, after);
        var ids = before.Steps.Concat(after.Steps).SelectMany(step => step.Ingredients).Select(i => i.IngredientId)
            .Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        var steps = RecipeContentResolver.ToDto(before, ingredients).Steps
            .Concat(RecipeContentResolver.ToDto(after, ingredients).Steps).ToDictionary(step => step.Id);

        return new RecipeVersionDiffDto(after.RecipeId, new VersionRefDto(before.Id, before.VersionNo, before.State),
            new VersionRefDto(after.Id, after.VersionNo, after.State), diff.HasChanges,
            diff.Count(StepChangeKind.Added), diff.Count(StepChangeKind.Removed), diff.Count(StepChangeKind.Changed),
            diff.Count(StepChangeKind.Unchanged),
        [
            .. diff.Steps.Select(change => new StepChangeDto(change.Kind,
                change.Before is null ? null : steps[change.Before.Id], change.After is null ? null : steps[change.After.Id],
                change.ChangedFields,
                [
                    .. change.Ingredients.Select(i =>
                    {
                        var ingredient = ingredients.GetValueOrDefault(i.IngredientId);
                        return new IngredientChangeDto(i.IngredientId, ingredient?.IngredientCode, ingredient?.Name,
                            i.QuantityBefore, i.UnitBefore, i.QuantityAfter, i.UnitAfter);
                    }),
                ])),
        ]);
    }

    /// <summary>Replaces the content of a draft. A released version refuses with BR-01.</summary>
    public async Task<RecipeVersionDto> UpdateVersionAsync(long id, RecipeContentRequest request,
        CancellationToken cancellationToken)
    {
        var version = await db.FindVersionAsync(id, cancellationToken);
        // Before anything else: a released version is refused for what it is,
        // whatever the request looks like.
        version.EnsureMutable();

        var specs = await db.ResolveAsync(request, version, cancellationToken);
        version.ReplaceContent(specs, currentUser.RequireUserId());

        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Update,
            new { version.VersionNo, steps = specs.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await db.ToDtoAsync(version, cancellationToken);
    }

    internal async Task<int> NextVersionNoAsync(long recipeId, CancellationToken cancellationToken) =>
        (await db.RecipeVersions.Where(v => v.RecipeId == recipeId)
            .MaxAsync(v => (int?)v.VersionNo, cancellationToken) ?? 0) + 1;

    private async Task<Recipe> FindRecipeAsync(long id, CancellationToken cancellationToken) =>
        await db.Recipes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Recipe", id);

    private async Task<Dictionary<long, VersionFacts>> VersionFactsAsync(IReadOnlyCollection<long> recipeIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.RecipeVersions.AsNoTracking()
            .Where(v => recipeIds.Contains(v.RecipeId))
            .Select(v => new { v.RecipeId, v.Id, v.VersionNo, v.State })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(v => v.RecipeId).ToDictionary(g => g.Key, g =>
        {
            var released = g.SingleOrDefault(v => v.State == VersionState.Released);
            return new VersionFacts(released?.Id, released?.VersionNo, g.Count());
        });
    }

    private static RecipeDto ToDto(Recipe recipe, Dictionary<long, VersionFacts> versions)
    {
        var facts = versions.GetValueOrDefault(recipe.Id);
        return new RecipeDto(recipe.Id, recipe.RecipeCode, recipe.Name, recipe.Category, recipe.Origin,
            recipe.Status, recipe.CreatedBy, recipe.CreatedAt, facts?.ReleasedVersionId, facts?.ReleasedVersionNo,
            facts?.Count ?? 0);
    }

    private sealed record VersionFacts(long? ReleasedVersionId, int? ReleasedVersionNo, int Count);
}

internal static class RecipeVersionCopy
{
    /// <summary>The content of a version as specifications, ready to become the content of another.</summary>
    public static IReadOnlyList<StepSpec> ToSpecs(this RecipeVersion version) =>
    [
        .. version.OrderedSteps().Select(step => new StepSpec(step.StepOrder, step.ActionText, step.EquipmentClass,
            step.TechniqueGate, step.DurationSeconds,
            [.. step.Ingredients.Select(i => new IngredientSpec(i.IngredientId, i.Quantity, i.Unit))],
            [.. step.Dependencies.Select(d => new DependencySpec(d.DependsOnStep.StepOrder, d.DependencyType))])),
    ];
}
