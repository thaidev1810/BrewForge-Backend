using BrewForge.Domain.Courses;
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
    /// The water temperature each tea leaf of the catalogue is brewed at, in
    /// degrees Celsius. A step that brews the leaf and states its temperature
    /// is held to this window by the ingredient check.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (decimal MinC, decimal MaxC)> BrewingWindows =
        new Dictionary<string, (decimal, decimal)>
        {
            ["ING-OOLONG"] = (85m, 95m),
            ["ING-BLACKTEA"] = (90m, 100m),
            ["ING-JASMINE"] = (75m, 85m),
            ["ING-MATCHA"] = (70m, 80m),
        };

    /// <summary>
    /// The induction course every new member of staff takes first: one or two
    /// lessons for each of the seven modules. It is built from no recipe, so
    /// the lessons of its TECHNIQUE module are its practical checklist.
    /// </summary>
    public static readonly IReadOnlyDictionary<ModuleType, (string Title, string Content)[]> InductionLessons =
        new Dictionary<ModuleType, (string, string)[]>
        {
            [ModuleType.ProductOverview] =
            [
                ("What the chain serves", "Specialty tea and coffee, made to one recipe at every branch. A guest should not be able to tell which branch made their drink."),
            ],
            [ModuleType.Ingredients] =
            [
                ("How ingredients are stored and dated", "Every opened, brewed or cooked ingredient is labelled with the time it was prepared. Past its shelf life it is discarded, not topped up."),
            ],
            [ModuleType.Equipment] =
            [
                ("The standard equipment of a branch", "Every branch has the same machines, set to the same doses. Report a machine that doses outside its range; do not adjust the recipe to it."),
            ],
            [ModuleType.Sop] =
            [
                ("Opening and closing the counter", "Clean, stock and check dates before the first order; clean, cover and log before leaving. The checklist at the counter is the order to do it in."),
            ],
            [ModuleType.Technique] =
            [
                ("Wash hands and prepare the station", "Twenty seconds with soap, up to the wrist, before every shift and after every break. Tools in their place before the first order."),
                ("Greet the guest and repeat the order", "Greet within five seconds, take the order, and say it back before making anything."),
            ],
            [ModuleType.CommonMistakes] =
            [
                ("What new staff get wrong", "Pouring by eye, skipping the date label, and changing a recipe because a guest asked. Measure, label, and offer what the menu offers."),
            ],
            [ModuleType.ExceptionHandling] =
            [
                ("When something goes wrong", "A spill, a machine fault or a complaint: stop, make it safe, tell the shift lead, and remake the drink. Never serve a drink you are unsure of."),
            ],
        };

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

    /// <summary>The recipe the seeded PRODUCT course teaches: a drink every branch already sells.</summary>
    public const string ProductCourseRecipe = "R07";

    /// <summary>
    /// What the trainer writes for the PRODUCT course of R07. The first four
    /// modules are generated from the recipe and need nothing here; the
    /// technique module gets prose for each of its gates.
    /// </summary>
    public static readonly IReadOnlyDictionary<ModuleType, (string Title, string Content)[]> ProductCourseLessons =
        new Dictionary<ModuleType, (string, string)[]>
        {
            [ModuleType.CommonMistakes] =
            [
                ("Coffee that is too thin or too bitter", "Too little coffee in the phin, or water off the boil, gives a thin cup; pressing the filter down hard gives a bitter one. Weigh the 22 g and let the water do the work."),
                ("Undissolved condensed milk", "Pouring over ice before stirring leaves the milk at the bottom of the glass. Stir in the glass until it is one colour, then pour over the ice."),
            ],
            [ModuleType.ExceptionHandling] =
            [
                ("The phin drips too fast or stops", "Too fast: the grind is coarse or the press too light; tell the shift lead and brew again. Stopped: the grind is too fine; do not shake the phin, start again with a new dose."),
                ("A guest asks for less sweet", "Offer the menu option with less condensed milk; do not change the dose by eye."),
            ],
        };

    /// <summary>The technique gate lessons of the R07 course, in step order.</summary>
    public static readonly IReadOnlyList<string> ProductCourseTechnique =
    [
        "Pour 20 ml of water first and wait 30 seconds while the coffee blooms. Then fill the phin and close the lid; it should drip for about four to five minutes.",
        "Stir in the glass until no streak of condensed milk is left, then pour over the ice in one movement.",
    ];

    /// <summary>One question per module of the R07 course; the correct answer is always "A".</summary>
    public static readonly IReadOnlyDictionary<ModuleType, (string Question, string Right, string Wrong1, string Wrong2)> ProductCourseQuiz =
        new Dictionary<ModuleType, (string, string, string, string)>
        {
            [ModuleType.ProductOverview] = ("What is Cà phê sữa đá?", "Phin coffee with condensed milk, served over ice", "Espresso with steamed milk", "Cold brew with fresh milk"),
            [ModuleType.Ingredients] = ("How much condensed milk goes into one cup?", "30 ml", "15 ml", "60 ml"),
            [ModuleType.Equipment] = ("Which equipment brews the coffee?", "The phin filter", "The espresso machine", "The tea brewer"),
            [ModuleType.Sop] = ("What comes first in the glass?", "The condensed milk", "The ice", "The coffee"),
            [ModuleType.Technique] = ("How long does the coffee bloom?", "30 seconds, with 20 ml of water", "No bloom: fill the phin at once", "5 minutes, with all the water"),
            [ModuleType.CommonMistakes] = ("Why is condensed milk left at the bottom of the glass?", "It was poured over ice before being stirred", "There was too much coffee", "The phin dripped too slowly"),
            [ModuleType.ExceptionHandling] = ("The phin has stopped dripping. What do you do?", "Start again with a new dose", "Shake the phin until it drips", "Serve what has dripped"),
        };
}
