using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.ImpactScenario;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice7;

/// <summary>Periodic stock and ingredient loss control (not in the contract).</summary>
[Collection(ApiCollection.Name)]
public sealed class StockApiTests(BrewForgeApiFactory factory)
{
    private const string Receipts = "/api/v1/stock/receipts";
    private const string Counts = "/api/v1/stock/counts";

    [Fact]
    public async Task Loss_report_compares_the_stock_used_with_what_the_recipes_expected()
    {
        // A drink with an ingredient of its own (20 g per cup), live at B01: the ingredient is not used
        // by any other drink, so its line in the report is this test's alone on the shared database.
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 1);
        var from = Today.AddDays(-2);
        var to = Today.AddDays(-1);
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);

        // Opening 2.5 kg, a 500 g delivery, 100 cups sold (20 g each = 2000 g expected), closing 800 g.
        // Used = 2500 + 500 - 800 = 2200 g -> 200 g lost (10%), beyond the 5% threshold.
        await (await manager.PostAsJsonAsync(Counts, new { ingredientId = drink.IngredientId, quantity = 2.5m, unit = "kg", countedOn = from }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await manager.PostAsJsonAsync("/api/v1/sales",
                new { branchId = drink.BranchId, recipeId = drink.RecipeId, tradingDate = to, cupsSold = 100 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await manager.PostAsJsonAsync(Receipts, new { ingredientId = drink.IngredientId, quantity = 500m, unit = "g", receivedOn = to }))
            .ShouldBeAsync(HttpStatusCode.Created);
        await (await manager.PostAsJsonAsync(Counts, new { ingredientId = drink.IngredientId, quantity = 800m, unit = "g", countedOn = to }))
            .ShouldBeAsync(HttpStatusCode.Created);

        var report = await (await manager.GetAsync($"/api/v1/stock/loss-report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("B01", report.GetProperty("branchCode").GetString());
        var line = report.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("ingredientId").GetInt64() == drink.IngredientId);
        Assert.Equal("g", line.GetProperty("unit").GetString());
        Assert.Equal(2500m, line.GetProperty("openingStock").GetDecimal());   // 2.5 kg -> 2500 g
        Assert.Equal(500m, line.GetProperty("received").GetDecimal());
        Assert.Equal(800m, line.GetProperty("closingStock").GetDecimal());
        Assert.Equal(2200m, line.GetProperty("actualUsed").GetDecimal());     // 2500 + 500 - 800
        Assert.Equal(2000m, line.GetProperty("theoreticalUsed").GetDecimal()); // 100 cups x 20 g
        Assert.Equal(200m, line.GetProperty("loss").GetDecimal());
        Assert.Equal(10m, line.GetProperty("variancePct").GetDecimal());
        Assert.True(line.GetProperty("flagged").GetBoolean());
    }

    [Fact]
    public async Task A_quantity_in_a_unit_that_does_not_fit_the_ingredient_is_refused()
    {
        var drink = await factory.NewTaughtDrinkAsync(certifiedStaff: 1);
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);

        // The ingredient is measured in grams; millilitres cannot be converted to it.
        var error = await (await manager.PostAsJsonAsync(Receipts,
                new { ingredientId = drink.IngredientId, quantity = 10m, unit = "ml", receivedOn = Today }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Contains("unit", error.DetailFields());
    }
}
