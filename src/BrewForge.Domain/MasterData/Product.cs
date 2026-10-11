using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

/// <summary>
/// A drink as it is sold: a name, a category, a picture, and the recipe whose
/// released version is its current recipe and SOP. Deactivated, never deleted (BR-16).
/// </summary>
public sealed class Product : INeverDeleted
{
    private Product() { }

    public long Id { get; private set; }
    public string ProductCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public long? CategoryId { get; private set; }
    public long? RecipeId { get; private set; }
    public string? Description { get; private set; }
    public string? ImageUrl { get; private set; }
    public CatalogStatus Status { get; private set; } = CatalogStatus.Active;
    public long CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    string INeverDeleted.RetentionRule => "BR-16";

    public static Product Create(string productCode, string name, long? categoryId, long? recipeId,
        string? description, string? imageUrl, long createdBy, DateTimeOffset now)
    {
        productCode = productCode?.Trim().ToUpperInvariant() ?? "";
        new FieldErrors().RequiredMax("productCode", productCode, 24).ThrowIfAny();

        var product = new Product { ProductCode = productCode, CreatedBy = createdBy, CreatedAt = now };
        product.Update(name, categoryId, recipeId, description, imageUrl);
        return product;
    }

    public void Update(string name, long? categoryId, long? recipeId, string? description, string? imageUrl)
    {
        name = name?.Trim() ?? "";
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        imageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        new FieldErrors()
            .RequiredMax("name", name, 120)
            .MaxLength("description", description, 1000)
            .MaxLength("imageUrl", imageUrl, 500)
            .ThrowIfAny();

        Name = name;
        CategoryId = categoryId;
        RecipeId = recipeId;
        Description = description;
        ImageUrl = imageUrl;
    }

    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Reactivate() => Status = CatalogStatus.Active;
}
