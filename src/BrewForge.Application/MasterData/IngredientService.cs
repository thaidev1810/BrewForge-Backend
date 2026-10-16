using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

public sealed record IngredientDto(long Id, string IngredientCode, string Name, IngredientUnit Unit,
    int ShelfLifeHours, string? StorageRule, CatalogStatus Status, decimal? BrewTempMinC = null,
    decimal? BrewTempMaxC = null)
{
    public static IngredientDto From(Ingredient ingredient) =>
        new(ingredient.Id, ingredient.IngredientCode, ingredient.Name, ingredient.Unit,
            ingredient.ShelfLifeHours, ingredient.StorageRule, ingredient.Status, ingredient.BrewTempMinC,
            ingredient.BrewTempMaxC);
}

/// <summary>
/// <c>Status</c> is optional on update and reactivates or deactivates the
/// ingredient. <c>BrewTempMinC</c> and <c>BrewTempMaxC</c> are the window a
/// leaf is brewed in: both, or neither for an ingredient that is not brewed.
/// </summary>
public sealed record IngredientRequest(string? IngredientCode, string? Name, IngredientUnit? Unit,
    int? ShelfLifeHours, string? StorageRule, CatalogStatus? Status, decimal? BrewTempMinC = null,
    decimal? BrewTempMaxC = null);

/// <summary>UC-02: the ingredient catalogue (SCR-04).</summary>
public sealed class IngredientService(IBrewForgeDbContext db)
{
    private static readonly SortMap<Ingredient> Sorting = new SortMap<Ingredient>("ingredientCode", i => i.Id)
        .Add("id", i => i.Id)
        .Add("ingredientCode", i => i.IngredientCode)
        .Add("name", i => i.Name)
        .Add("unit", i => i.Unit)
        .Add("shelfLifeHours", i => i.ShelfLifeHours)
        .Add("status", i => i.Status);

    public async Task<PagedResult<IngredientDto>> ListAsync(PageQuery paging, string? keyword, string? unit,
        string? status, CancellationToken cancellationToken)
    {
        var unitFilter = PagingExtensions.ParseFilter<IngredientUnit>(unit, "unit");
        var statusFilter = PagingExtensions.ParseFilter<CatalogStatus>(status, "status");

        var query = db.Ingredients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(i => i.IngredientCode.ToLower().Contains(term) || i.Name.ToLower().Contains(term));
        }
        if (unitFilter is not null) query = query.Where(i => i.Unit == unitFilter);
        if (statusFilter is not null) query = query.Where(i => i.Status == statusFilter);

        return await query.ToPagedAsync(paging, Sorting, IngredientDto.From, cancellationToken);
    }

    public async Task<IngredientDto> GetAsync(long id, CancellationToken cancellationToken) =>
        IngredientDto.From(await FindAsync(id, cancellationToken));

    public async Task<IngredientDto> CreateAsync(IngredientRequest request, CancellationToken cancellationToken)
    {
        RequireUnitAndShelfLife(request);
        var ingredient = Ingredient.Create(request.IngredientCode!, request.Name!, request.Unit!.Value,
            request.ShelfLifeHours!.Value, request.StorageRule);
        ingredient.SetBrewingWindow(request.BrewTempMinC, request.BrewTempMaxC);

        if (await db.Ingredients.AnyAsync(i => i.IngredientCode == ingredient.IngredientCode, cancellationToken))
        {
            throw DomainException.RuleViolation("UNIQUE",
                $"Ingredient code '{ingredient.IngredientCode}' already exists.",
                details: new ErrorDetail("ingredientCode", "already exists"));
        }

        db.Ingredients.Add(ingredient);
        db.Audit(AuditEntities.Ingredient, () => ingredient.Id, AuditActions.Create, IngredientDto.From(ingredient));
        await db.SaveChangesAsync(cancellationToken);
        return IngredientDto.From(ingredient);
    }

    public async Task<IngredientDto> UpdateAsync(long id, IngredientRequest request,
        CancellationToken cancellationToken)
    {
        var ingredient = await FindAsync(id, cancellationToken);
        MasterDataGuards.EnsureCodeUnchanged("ingredientCode", request.IngredientCode, ingredient.IngredientCode);
        RequireUnitAndShelfLife(request);

        ingredient.Update(request.Name!, request.Unit!.Value, request.ShelfLifeHours!.Value, request.StorageRule);
        ingredient.SetBrewingWindow(request.BrewTempMinC, request.BrewTempMaxC);
        if (request.Status == CatalogStatus.Inactive) ingredient.Deactivate();
        if (request.Status == CatalogStatus.Active) ingredient.Reactivate();

        db.Audit(AuditEntities.Ingredient, () => ingredient.Id, AuditActions.Update, IngredientDto.From(ingredient));
        await db.SaveChangesAsync(cancellationToken);
        return IngredientDto.From(ingredient);
    }

    /// <summary>An ingredient is deactivated, never deleted (BR-16).</summary>
    public async Task<IngredientDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        var ingredient = await FindAsync(id, cancellationToken);

        ingredient.Deactivate();
        db.Audit(AuditEntities.Ingredient, () => ingredient.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return IngredientDto.From(ingredient);
    }

    private static void RequireUnitAndShelfLife(IngredientRequest request) =>
        new FieldErrors()
            .Check(request.Unit is not null, "unit", "is required")
            .Check(request.ShelfLifeHours is not null, "shelfLifeHours", "is required")
            .ThrowIfAny();

    private async Task<Ingredient> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.Ingredients.SingleOrDefaultAsync(i => i.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Ingredient", id);
}
