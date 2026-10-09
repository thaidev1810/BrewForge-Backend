using System.Reflection;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;

namespace BrewForge.Domain.Tests.MasterData;

public sealed class MasterDataTests
{
    // ---------------------------------------------------------------- BR-16

    /// <summary>
    /// BR-16 in the domain: master data offers deactivation and nothing that
    /// removes a record, and every master data entity is marked as retained
    /// so the persistence layer refuses to delete it.
    /// </summary>
    [Theory]
    [InlineData(typeof(Role))]
    [InlineData(typeof(AppUser))]
    [InlineData(typeof(Branch))]
    [InlineData(typeof(Ingredient))]
    [InlineData(typeof(StandardEquipment))]
    public void Master_data_is_marked_as_never_deleted_under_BR_16(Type entity)
    {
        Assert.True(typeof(INeverDeleted).IsAssignableFrom(entity), $"{entity.Name} must implement INeverDeleted");

        var instance = (INeverDeleted)Activator.CreateInstance(entity, nonPublic: true)!;
        Assert.Equal("BR-16", instance.RetentionRule);

        var methods = entity.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);
        Assert.DoesNotContain(methods, name => name.Contains("Delete") || name.Contains("Remove"));
    }

    [Fact]
    public void Ingredient_is_deactivated_not_removed()
    {
        var ingredient = Ingredient.Create("ING-OOLONG", "Oolong tea leaf", IngredientUnit.Gram, 8, null);

        ingredient.Deactivate();

        Assert.Equal(CatalogStatus.Inactive, ingredient.Status);
        Assert.False(ingredient.IsActive);
        Assert.Equal("ING-OOLONG", ingredient.IngredientCode);
    }

    [Fact]
    public void Equipment_class_is_deactivated_not_removed()
    {
        var equipment = StandardEquipment.Create("EQ-BREW-01", "TEA_BREWER", 15, 25, DosingUnit.Gram);

        equipment.Deactivate();

        Assert.Equal(CatalogStatus.Inactive, equipment.Status);
        Assert.Equal("TEA_BREWER", equipment.EquipmentClass);
    }

    [Fact]
    public void Branch_is_closed_not_removed()
    {
        var branch = Branch.Create("B01", "BrewForge Nguyễn Huệ", null);

        branch.Deactivate();

        Assert.Equal(BranchStatus.Closed, branch.Status);
        Assert.Equal("B01", branch.BranchCode);
    }

    // ---------------------------------------------------------------- ingredient

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Shelf_life_must_be_positive(int hours)
    {
        var refusal = Assert.Throws<DomainException>(() =>
            Ingredient.Create("ING-X", "Something", IngredientUnit.Gram, hours, null));

        Assert.Contains(refusal.Details, d => d.Field == "shelfLifeHours");
    }

    [Fact]
    public void Ingredient_code_and_name_are_required_and_sized_to_their_columns()
    {
        Assert.Contains(Assert.Throws<DomainException>(() =>
            Ingredient.Create(" ", "Name", IngredientUnit.Gram, 1, null)).Details, d => d.Field == "ingredientCode");
        Assert.Contains(Assert.Throws<DomainException>(() =>
            Ingredient.Create(new string('c', 25), "Name", IngredientUnit.Gram, 1, null)).Details,
            d => d.Field == "ingredientCode");
        Assert.Contains(Assert.Throws<DomainException>(() =>
            Ingredient.Create("ING-X", new string('n', 121), IngredientUnit.Gram, 1, null)).Details,
            d => d.Field == "name");
        Assert.Contains(Assert.Throws<DomainException>(() =>
            Ingredient.Create("ING-X", "Name", IngredientUnit.Gram, 1, new string('s', 256))).Details,
            d => d.Field == "storageRule");
    }

    [Fact]
    public void Ingredient_update_that_fails_validation_changes_nothing()
    {
        var ingredient = Ingredient.Create("ING-MILK", "Fresh milk", IngredientUnit.Millilitre, 4, "Chilled");

        Assert.Throws<DomainException>(() => ingredient.Update("", IngredientUnit.Gram, 0, null));

        Assert.Equal("Fresh milk", ingredient.Name);
        Assert.Equal(IngredientUnit.Millilitre, ingredient.Unit);
        Assert.Equal(4, ingredient.ShelfLifeHours);
    }

    // ---------------------------------------------------------------- equipment

    [Fact]
    public void Minimum_threshold_may_not_exceed_the_maximum()
    {
        var refusal = Assert.Throws<DomainException>(() =>
            StandardEquipment.Create("EQ-X", "X", minThreshold: 25.001m, maxThreshold: 25m, DosingUnit.Gram));

        Assert.Contains(refusal.Details, d => d.Field == "maxThreshold");
    }

    [Fact]
    public void Equal_thresholds_are_allowed_as_the_schema_allows_them()
    {
        var equipment = StandardEquipment.Create("EQ-X", "X", 18m, 18m, DosingUnit.Gram);

        Assert.Equal(equipment.MinThreshold, equipment.MaxThreshold);
    }

    [Fact]
    public void Negative_threshold_is_rejected()
    {
        var refusal = Assert.Throws<DomainException>(() =>
            StandardEquipment.Create("EQ-X", "X", -1m, 5m, DosingUnit.Millilitre));

        Assert.Contains(refusal.Details, d => d.Field == "minThreshold");
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public void Every_role_has_a_permission_set_and_only_admin_manages_users()
    {
        foreach (var role in Enum.GetValues<RoleName>())
        {
            Assert.NotEmpty(Permissions.DefaultsFor(role));
        }

        var managers = Enum.GetValues<RoleName>()
            .Where(role => Permissions.DefaultsFor(role).Contains(Permissions.UsersManage));
        Assert.Equal([RoleName.Admin], managers);
    }
}
