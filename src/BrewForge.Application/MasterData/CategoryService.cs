using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

public sealed record CategoryDto(long Id, CategoryKind Kind, string Code, string Name, CatalogStatus Status)
{
    public static CategoryDto From(Category category) =>
        new(category.Id, category.Kind, category.Code, category.Name, category.Status);
}

/// <summary><c>Status</c> is optional on update and reactivates or deactivates the category.</summary>
public sealed record CategoryRequest(CategoryKind? Kind, string? Code, string? Name, CatalogStatus? Status);

/// <summary>The categories an administrator maintains: drink, ingredient, training and equipment.</summary>
public sealed class CategoryService(IBrewForgeDbContext db, TimeProvider clock)
{
    private static readonly SortMap<Category> Sorting = new SortMap<Category>("kind", c => c.Id)
        .Add("id", c => c.Id)
        .Add("kind", c => c.Kind)
        .Add("code", c => c.Code)
        .Add("name", c => c.Name)
        .Add("status", c => c.Status);

    public async Task<PagedResult<CategoryDto>> ListAsync(PageQuery paging, string? keyword, string? kind,
        string? status, CancellationToken cancellationToken)
    {
        var kindFilter = PagingExtensions.ParseFilter<CategoryKind>(kind, "kind");
        var statusFilter = PagingExtensions.ParseFilter<CatalogStatus>(status, "status");

        var query = db.Categories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(c => c.Code.ToLower().Contains(term) || c.Name.ToLower().Contains(term));
        }
        if (kindFilter is not null) query = query.Where(c => c.Kind == kindFilter);
        if (statusFilter is not null) query = query.Where(c => c.Status == statusFilter);

        return await query.ToPagedAsync(paging, Sorting, CategoryDto.From, cancellationToken);
    }

    public async Task<CategoryDto> GetAsync(long id, CancellationToken cancellationToken) =>
        CategoryDto.From(await FindAsync(id, cancellationToken));

    public async Task<CategoryDto> CreateAsync(CategoryRequest request, CancellationToken cancellationToken)
    {
        var category = Category.Create(request.Kind, request.Code!, request.Name!, clock.GetUtcNow());

        if (await db.Categories.AnyAsync(c => c.Kind == category.Kind && c.Code == category.Code, cancellationToken))
        {
            throw DomainException.RuleViolation("UNIQUE", $"Category code '{category.Code}' already exists for this kind.",
                details: new ErrorDetail("code", "already exists"));
        }

        db.Categories.Add(category);
        db.Audit(AuditEntities.Category, () => category.Id, AuditActions.Create, CategoryDto.From(category));
        await db.SaveChangesAsync(cancellationToken);
        return CategoryDto.From(category);
    }

    public async Task<CategoryDto> UpdateAsync(long id, CategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await FindAsync(id, cancellationToken);
        MasterDataGuards.EnsureCodeUnchanged("code", request.Code?.ToUpperInvariant(), category.Code);
        if (request.Kind is not null && request.Kind != category.Kind)
        {
            throw DomainException.Validation("The kind of an existing category cannot be changed.",
                new ErrorDetail("kind", "cannot be changed after creation"));
        }

        category.Rename(request.Name!);
        if (request.Status == CatalogStatus.Inactive) category.Deactivate();
        if (request.Status == CatalogStatus.Active) category.Reactivate();

        db.Audit(AuditEntities.Category, () => category.Id, AuditActions.Update, CategoryDto.From(category));
        await db.SaveChangesAsync(cancellationToken);
        return CategoryDto.From(category);
    }

    /// <summary>A category is deactivated, never deleted (BR-16).</summary>
    public async Task<CategoryDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        var category = await FindAsync(id, cancellationToken);

        category.Deactivate();
        db.Audit(AuditEntities.Category, () => category.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return CategoryDto.From(category);
    }

    private async Task<Category> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.Categories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Category", id);
}
