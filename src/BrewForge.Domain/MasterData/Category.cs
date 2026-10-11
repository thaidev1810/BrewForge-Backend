using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

/// <summary>What a category classifies: the four lists an administrator maintains.</summary>
public enum CategoryKind
{
    Drink,
    Ingredient,
    Training,
    Equipment,
}

/// <summary>A label of one kind (Milk Tea, Topping, ...). Deactivated, never deleted (BR-16).</summary>
public sealed class Category : INeverDeleted
{
    private Category() { }

    public long Id { get; private set; }
    public CategoryKind Kind { get; private set; }

    /// <summary>Unique within its kind and fixed once created.</summary>
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public CatalogStatus Status { get; private set; } = CatalogStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; }

    string INeverDeleted.RetentionRule => "BR-16";

    public static Category Create(CategoryKind? kind, string code, string name, DateTimeOffset now)
    {
        code = code?.Trim().ToUpperInvariant() ?? "";
        new FieldErrors()
            .Check(kind is not null, "kind", "is required")
            .RequiredMax("code", code, 40)
            .ThrowIfAny();

        var category = new Category { Kind = kind!.Value, Code = code, CreatedAt = now };
        category.Rename(name);
        return category;
    }

    public void Rename(string name)
    {
        name = name?.Trim() ?? "";
        new FieldErrors().RequiredMax("name", name, 120).ThrowIfAny();
        Name = name;
    }

    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Reactivate() => Status = CatalogStatus.Active;
}
