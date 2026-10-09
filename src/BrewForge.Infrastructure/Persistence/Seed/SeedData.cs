using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;

namespace BrewForge.Infrastructure.Persistence.Seed;

/// <summary>
/// The reference data every slice is tested and demonstrated against: three
/// branches, at least one user per role, the ingredient catalogue and the
/// chain-wide standard equipment profile.
/// </summary>
public static class SeedData
{
    public sealed record BranchSeed(string Code, string Name, string Address);

    public sealed record UserSeed(string Username, string FullName, RoleName Role, string? BranchCode)
    {
        public string Email => $"{Username}@brewforge.local";
    }

    public sealed record IngredientSeed(string Code, string Name, IngredientUnit Unit, int ShelfLifeHours,
        string StorageRule);

    public sealed record EquipmentSeed(string Code, string Class, decimal Min, decimal Max, DosingUnit Unit);

    public static readonly IReadOnlyList<BranchSeed> Branches =
    [
        new("B01", "BrewForge Nguyễn Huệ", "12 Nguyễn Huệ, Quận 1, TP. Hồ Chí Minh"),
        new("B02", "BrewForge Thảo Điền", "45 Xuân Thủy, Thảo Điền, TP. Thủ Đức"),
        new("B03", "BrewForge Phú Mỹ Hưng", "88 Nguyễn Đức Cảnh, Quận 7, TP. Hồ Chí Minh"),
    ];

    /// <summary>
    /// One user per role, plus a second branch manager and enough trainees to
    /// exercise branch scoping and the certified-staff threshold later on.
    /// </summary>
    public static readonly IReadOnlyList<UserSeed> Users =
    [
        new("admin", "Nguyễn Minh Quản", RoleName.Admin, null),
        new("rdspec", "Trần Thu Hà", RoleName.RdSpecialist, null),
        new("rdmanager", "Lê Hoàng Nam", RoleName.RdManager, null),
        // A second manager, because the one who edits a draft may not release it (BR-12).
        new("rdmanager2", "Trịnh Bảo Châu", RoleName.RdManager, null),
        new("trainer", "Phạm Lê Nhật Huy", RoleName.Trainer, null),
        new("auditor", "Đỗ Thị Kim Anh", RoleName.QualityAuditor, null),
        new("trainingmgr", "Võ Thanh Tùng", RoleName.TrainingManager, null),
        new("branchmgr", "Huỳnh Gia Bảo", RoleName.BranchManager, "B01"),
        new("branchmgr2", "Đặng Mỹ Linh", RoleName.BranchManager, "B02"),
        new("trainee", "Bùi Anh Khoa", RoleName.Trainee, "B01"),
        new("trainee2", "Ngô Phương Thảo", RoleName.Trainee, "B01"),
        new("trainee3", "Lý Quốc Đạt", RoleName.Trainee, "B02"),
        new("trainee4", "Mai Ngọc Hân", RoleName.Trainee, "B02"),
    ];

    /// <summary>
    /// Shelf life is in hours from preparation (BR-11): how long the
    /// ingredient may stay in use once it has been opened, brewed or cooked.
    /// </summary>
    public static readonly IReadOnlyList<IngredientSeed> Ingredients =
    [
        new("ING-OOLONG", "Oolong tea leaf", IngredientUnit.Gram, 8, "Sealed, 18-24 C"),
        new("ING-BLACKTEA", "Assam black tea leaf", IngredientUnit.Gram, 8, "Sealed, 18-24 C"),
        new("ING-JASMINE", "Jasmine green tea leaf", IngredientUnit.Gram, 6, "Sealed, 18-24 C, away from light"),
        new("ING-MATCHA", "Matcha powder", IngredientUnit.Gram, 4, "Sealed, chilled 2-6 C"),
        new("ING-ESPBEAN", "Espresso blend coffee bean", IngredientUnit.Gram, 72, "Sealed hopper, away from light"),
        new("ING-PHIN", "Robusta ground coffee for phin", IngredientUnit.Gram, 24, "Airtight container"),
        new("ING-WATER", "Filtered water", IngredientUnit.Millilitre, 24, "Covered dispenser"),
        new("ING-MILK", "Fresh milk", IngredientUnit.Millilitre, 4, "Chilled 2-6 C"),
        new("ING-MILKBASE", "Milk tea base", IngredientUnit.Millilitre, 8, "Chilled 2-6 C"),
        new("ING-CONDENSED", "Sweetened condensed milk", IngredientUnit.Millilitre, 48, "Covered, chilled after opening"),
        new("ING-CREAM", "Whipping cream", IngredientUnit.Millilitre, 4, "Chilled 2-6 C"),
        new("ING-SYRUP", "Cane sugar syrup", IngredientUnit.Millilitre, 72, "Covered pump bottle"),
        new("ING-TAPIOCA", "Cooked tapioca pearl", IngredientUnit.Gram, 4, "Kept warm in syrup"),
        new("ING-ICE", "Ice cube", IngredientUnit.Gram, 2, "Ice bin, lid closed"),
        new("ING-PEACH", "Canned peach slice", IngredientUnit.Piece, 24, "Chilled after opening"),
        new("ING-LEMONGRASS", "Lemongrass stalk", IngredientUnit.Piece, 12, "Chilled 2-6 C"),
    ];

    /// <summary>
    /// The Min-Max range is the dose one step may put through the machine,
    /// in the machine's dosing unit (BR-10).
    /// </summary>
    public static readonly IReadOnlyList<EquipmentSeed> Equipment =
    [
        new("EQ-BREW-01", "TEA_BREWER", 15.000m, 25.000m, DosingUnit.Gram),
        new("EQ-ESP-01", "ESPRESSO_MACHINE", 16.000m, 20.000m, DosingUnit.Gram),
        new("EQ-GRD-01", "COFFEE_GRINDER", 14.000m, 22.000m, DosingUnit.Gram),
        new("EQ-PHIN-01", "PHIN_FILTER", 18.000m, 25.000m, DosingUnit.Gram),
        new("EQ-STM-01", "MILK_STEAMER", 100.000m, 350.000m, DosingUnit.Millilitre),
        new("EQ-SHK-01", "SHAKER", 10.000m, 500.000m, DosingUnit.Millilitre),
        new("EQ-BLD-01", "BLENDER", 10.000m, 600.000m, DosingUnit.Millilitre),
    ];
}
