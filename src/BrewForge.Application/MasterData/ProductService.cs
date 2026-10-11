using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

/// <summary>
/// A product with its current recipe: the released version of the recipe it
/// points at. In this system the recipe version is also the SOP, so
/// <c>CurrentVersionId</c> answers both "Current Recipe" and "Current SOP".
/// </summary>
public sealed record ProductDto(long Id, string ProductCode, string Name, long? CategoryId, string? CategoryName,
    long? RecipeId, string? RecipeCode, long? CurrentVersionId, int? CurrentVersionNo, string? Description,
    string? ImageUrl, CatalogStatus Status);

public sealed record ProductRequest(string? ProductCode, string? Name, long? CategoryId, long? RecipeId,
    string? Description, string? ImageUrl, CatalogStatus? Status);

/// <summary>The drinks that are sold (R&amp;D "Products"). A product is deactivated, never deleted (BR-16).</summary>
public sealed class ProductService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly SortMap<Product> Sorting = new SortMap<Product>("name", p => p.Id)
        .Add("id", p => p.Id)
        .Add("productCode", p => p.ProductCode)
        .Add("name", p => p.Name)
        .Add("status", p => p.Status);

    public async Task<PagedResult<ProductDto>> ListAsync(PageQuery paging, string? keyword, long? categoryId,
        string? status, CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<CatalogStatus>(status, "status");

        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(p => p.ProductCode.ToLower().Contains(term) || p.Name.ToLower().Contains(term));
        }
        if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
        if (statusFilter is not null) query = query.Where(p => p.Status == statusFilter);

        var page = await query.ToPagedAsync(paging, Sorting, p => p, cancellationToken);
        return new PagedResult<ProductDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size,
            page.Total);
    }

    public async Task<ProductDto> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ToDtosAsync([await FindAsync(id, cancellationToken)], cancellationToken))[0];

    public async Task<ProductDto> CreateAsync(ProductRequest request, CancellationToken cancellationToken)
    {
        await EnsureReferencesAsync(request, cancellationToken);
        var product = Product.Create(request.ProductCode!, request.Name!, request.CategoryId, request.RecipeId,
            request.Description, request.ImageUrl, currentUser.RequireUserId(), clock.GetUtcNow());

        if (await db.Products.AnyAsync(p => p.ProductCode == product.ProductCode, cancellationToken))
        {
            throw DomainException.RuleViolation("UNIQUE", $"Product code '{product.ProductCode}' already exists.",
                details: new ErrorDetail("productCode", "already exists"));
        }

        db.Products.Add(product);
        db.Audit(AuditEntities.Product, () => product.Id, AuditActions.Create, new { product.ProductCode, product.Name });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([product], cancellationToken))[0];
    }

    public async Task<ProductDto> UpdateAsync(long id, ProductRequest request, CancellationToken cancellationToken)
    {
        var product = await FindAsync(id, cancellationToken);
        MasterDataGuards.EnsureCodeUnchanged("productCode", request.ProductCode?.ToUpperInvariant(), product.ProductCode);
        await EnsureReferencesAsync(request, cancellationToken);

        product.Update(request.Name!, request.CategoryId, request.RecipeId, request.Description, request.ImageUrl);
        if (request.Status == CatalogStatus.Inactive) product.Deactivate();
        if (request.Status == CatalogStatus.Active) product.Reactivate();

        db.Audit(AuditEntities.Product, () => product.Id, AuditActions.Update, new { product.Name, product.RecipeId });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([product], cancellationToken))[0];
    }

    public async Task<ProductDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        var product = await FindAsync(id, cancellationToken);
        product.Deactivate();
        db.Audit(AuditEntities.Product, () => product.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([product], cancellationToken))[0];
    }

    private async Task EnsureReferencesAsync(ProductRequest request, CancellationToken cancellationToken)
    {
        if (request.CategoryId is { } categoryId)
        {
            var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
            if (category is null || category.Kind != CategoryKind.Drink || category.Status != CatalogStatus.Active)
            {
                throw DomainException.Validation("The category must be an active drink category.",
                    new ErrorDetail("categoryId", "is not an active DRINK category"));
            }
        }
        if (request.RecipeId is { } recipeId && !await db.Recipes.AnyAsync(r => r.Id == recipeId, cancellationToken))
        {
            throw DomainException.Validation("The recipe does not exist.", new ErrorDetail("recipeId", "does not exist"));
        }
    }

    private async Task<Product> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Product", id);

    private async Task<IReadOnlyList<ProductDto>> ToDtosAsync(IReadOnlyList<Product> products,
        CancellationToken cancellationToken)
    {
        var categoryIds = products.Where(p => p.CategoryId is not null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        var recipeIds = products.Where(p => p.RecipeId is not null).Select(p => p.RecipeId!.Value).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.RecipeCode, cancellationToken);
        var released = await db.RecipeVersions.AsNoTracking()
            .Where(v => recipeIds.Contains(v.RecipeId) && v.State == VersionState.Released)
            .Select(v => new { v.Id, v.RecipeId, v.VersionNo }).ToDictionaryAsync(v => v.RecipeId, cancellationToken);

        return [.. products.Select(p =>
        {
            var version = p.RecipeId is { } r ? released.GetValueOrDefault(r) : null;
            return new ProductDto(p.Id, p.ProductCode, p.Name, p.CategoryId,
                p.CategoryId is { } c ? categories.GetValueOrDefault(c) : null, p.RecipeId,
                p.RecipeId is { } rid ? recipes.GetValueOrDefault(rid) : null, version?.Id, version?.VersionNo,
                p.Description, p.ImageUrl, p.Status);
        })];
    }
}
