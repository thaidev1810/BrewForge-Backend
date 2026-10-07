using BrewForge.Domain.Recipes;

namespace BrewForge.Infrastructure.Persistence.Seed;

/// <summary>
/// Ten reference recipes entered as structured data, each of which passes the
/// three validator checks against the seeded catalogue, plus one draft that
/// deliberately fails all three so the validation and repair screens have
/// something to show.
/// </summary>
public static class SeedRecipes
{
    public sealed record RecipeSeed(string Code, string Name, RecipeCategory Category, RecipeOrigin Origin,
        IReadOnlyList<StepSeed> Steps);

    public sealed record StepSeed(string Action, string? Equipment, string? Gate, int? Seconds,
        (string Code, decimal Quantity, string Unit)[] Uses, (int Step, DependencyType Type)[] After);

    /// <summary>The demonstration draft that fails validation. Never released.</summary>
    public const string BrokenDemoCode = "R99";

    private const DependencyType Then = DependencyType.FinishToStart;
    private const DependencyType Needs = DependencyType.RequiresOutput;

    private static StepSeed Step(string action, string? equipment = null, string? gate = null, int? seconds = null,
        (string, decimal, string)[]? uses = null, (int, DependencyType)[]? after = null) =>
        new(action, equipment, gate, seconds, uses ?? [], after ?? []);

    public static readonly IReadOnlyList<RecipeSeed> All =
    [
        new("R01", "Trà sữa Oolong", RecipeCategory.Tea, RecipeOrigin.New,
        [
            Step("Brew the oolong", "TEA_BREWER", "Water at 90 C", 480,
                [("ING-OOLONG", 18m, "g"), ("ING-WATER", 300m, "ml")]),
            Step("Add the milk base", gate: "Pour down the side of the cup", seconds: 20,
                uses: [("ING-MILKBASE", 120m, "ml")], after: [(1, Then)]),
            Step("Shake with syrup and ice", "SHAKER", "Shake 10 times with a firm wrist", 15,
                [("ING-SYRUP", 20m, "ml"), ("ING-ICE", 150m, "g")], [(2, Needs)]),
            Step("Add the tapioca pearls and serve", seconds: 20,
                uses: [("ING-TAPIOCA", 50m, "g")], after: [(3, Then)]),
        ]),

        new("R02", "Trà đào cam sả", RecipeCategory.Tea, RecipeOrigin.New,
        [
            Step("Brew the black tea", "TEA_BREWER", "Water at 95 C", 420,
                [("ING-BLACKTEA", 20m, "g"), ("ING-WATER", 350m, "ml")]),
            Step("Bruise the lemongrass and steep it in the tea", gate: "Bruise the stalk with the back of a knife",
                seconds: 120, uses: [("ING-LEMONGRASS", 2m, "pcs")], after: [(1, Needs)]),
            Step("Shake with syrup and ice", "SHAKER", "Shake until the shaker frosts", 15,
                [("ING-SYRUP", 30m, "ml"), ("ING-ICE", 180m, "g")], [(2, Needs)]),
            Step("Garnish with peach slices", seconds: 15,
                uses: [("ING-PEACH", 3m, "pcs")], after: [(3, Then)]),
        ]),

        new("R03", "Trà sữa hoa lài", RecipeCategory.Tea, RecipeOrigin.New,
        [
            Step("Brew the jasmine tea", "TEA_BREWER", "Water at 80 C, never boiling", 300,
                [("ING-JASMINE", 16m, "g"), ("ING-WATER", 300m, "ml")]),
            Step("Add the milk base", seconds: 20,
                uses: [("ING-MILKBASE", 100m, "ml")], after: [(1, Then)]),
            Step("Sweeten and shake with ice", "SHAKER", "Shake 10 times with a firm wrist", 15,
                [("ING-SYRUP", 15m, "ml"), ("ING-ICE", 150m, "g")], [(2, Needs)]),
        ]),

        new("R04", "Matcha latte nóng", RecipeCategory.Tea, RecipeOrigin.New,
        [
            Step("Whisk the matcha with hot water", gate: "Whisk in a W motion until a fine foam forms", seconds: 45,
                uses: [("ING-MATCHA", 4m, "g"), ("ING-WATER", 60m, "ml")]),
            Step("Steam the milk", "MILK_STEAMER", "Stretch the milk to 60-65 C", 40,
                [("ING-MILK", 200m, "ml")]),
            Step("Pour the milk over the matcha", gate: "Pour from 5 cm in a steady stream", seconds: 20,
                after: [(1, Needs), (2, Needs)]),
        ]),

        new("R05", "Espresso", RecipeCategory.Coffee, RecipeOrigin.Existing,
        [
            Step("Grind the beans", "COFFEE_GRINDER", seconds: 15,
                uses: [("ING-ESPBEAN", 18m, "g")]),
            Step("Tamp and extract", "ESPRESSO_MACHINE", "Tamp level before locking the portafilter", 28,
                [("ING-WATER", 40m, "ml")], [(1, Needs)]),
        ]),

        new("R06", "Cà phê latte", RecipeCategory.Coffee, RecipeOrigin.New,
        [
            Step("Grind the beans", "COFFEE_GRINDER", seconds: 15,
                uses: [("ING-ESPBEAN", 18m, "g")]),
            Step("Tamp and extract", "ESPRESSO_MACHINE", "Tamp level before locking the portafilter", 28,
                [("ING-WATER", 40m, "ml")], [(1, Needs)]),
            Step("Steam the milk", "MILK_STEAMER", "Stretch the milk to 60-65 C", 40,
                [("ING-MILK", 220m, "ml")]),
            Step("Pour the milk into the espresso", gate: "Latte art pour from 5 cm", seconds: 20,
                after: [(2, Needs), (3, Needs)]),
        ]),

        new("R07", "Cà phê sữa đá", RecipeCategory.Coffee, RecipeOrigin.Existing,
        [
            Step("Add the condensed milk to the glass", seconds: 10,
                uses: [("ING-CONDENSED", 30m, "ml")]),
            Step("Brew through the phin", "PHIN_FILTER", "Bloom for 30 s with 20 ml of water", 300,
                [("ING-PHIN", 22m, "g"), ("ING-WATER", 90m, "ml")], [(1, Then)]),
            Step("Stir and pour over ice", gate: "Stir until the condensed milk has dissolved", seconds: 30,
                uses: [("ING-ICE", 120m, "g")], after: [(2, Needs)]),
        ]),

        new("R08", "Bạc xỉu", RecipeCategory.Coffee, RecipeOrigin.Existing,
        [
            Step("Combine the condensed milk and the fresh milk", seconds: 15,
                uses: [("ING-CONDENSED", 35m, "ml"), ("ING-MILK", 80m, "ml")]),
            Step("Brew through the phin", "PHIN_FILTER", "Bloom for 30 s with 20 ml of water", 300,
                [("ING-PHIN", 18m, "g"), ("ING-WATER", 60m, "ml")]),
            Step("Layer the coffee over the milk and ice", gate: "Pour over the back of a spoon", seconds: 30,
                uses: [("ING-ICE", 120m, "g")], after: [(1, Needs), (2, Needs)]),
        ]),

        new("R09", "Cold brew cam sả", RecipeCategory.Coffee, RecipeOrigin.New,
        [
            Step("Steep the ground coffee in cold water", gate: "Stir once so no dry grounds remain", seconds: 43200,
                uses: [("ING-PHIN", 60m, "g"), ("ING-WATER", 600m, "ml")]),
            Step("Filter the concentrate", seconds: 300, after: [(1, Needs)]),
            Step("Shake with syrup, lemongrass and ice", "SHAKER", "Shake until the shaker frosts", 20,
                [("ING-SYRUP", 20m, "ml"), ("ING-LEMONGRASS", 1m, "pcs"), ("ING-ICE", 150m, "g")], [(2, Needs)]),
        ]),

        new("R10", "Matcha đá xay", RecipeCategory.Other, RecipeOrigin.New,
        [
            Step("Blend the milk, matcha, syrup and ice", "BLENDER", "Blend until no ice crystals remain", 45,
                [("ING-MILK", 150m, "ml"), ("ING-MATCHA", 5m, "g"), ("ING-SYRUP", 25m, "ml"), ("ING-ICE", 200m, "g")]),
            Step("Top with whipped cream", seconds: 15,
                uses: [("ING-CREAM", 30m, "ml")], after: [(1, Then)]),
        ]),

        // Fails all three checks: 40 g in a 15-25 g brewer, steps 2 and 3
        // depending on each other, and fresh milk left in use for five hours.
        new(BrokenDemoCode, "Oolong ủ lạnh (bản nháp minh hoạ lỗi)", RecipeCategory.Tea, RecipeOrigin.New,
        [
            Step("Brew a very strong oolong", "TEA_BREWER", "Water at 90 C", 480,
                [("ING-OOLONG", 40m, "g"), ("ING-MILK", 100m, "ml")]),
            Step("Cold steep", seconds: 18000, after: [(3, Then)]),
            Step("Shake with ice", "SHAKER", seconds: 15,
                uses: [("ING-ICE", 150m, "g")], after: [(2, Needs)]),
        ]),
    ];
}
