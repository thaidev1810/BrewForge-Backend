using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>
/// A drink that everything depends on: made with an ingredient and a machine
/// of its own, released, taught by a published course, with certified staff
/// at B01 where it is on sale.
/// </summary>
public sealed record TaughtDrink(long IngredientId, long EquipmentId, long RecipeId, string RecipeCode, long VersionId,
    long CourseId, IReadOnlyList<long> CertificateIds, IReadOnlyList<long> StaffIds, long BranchId, long LaunchStatusId);

public static class ImpactScenario
{
    public const string Impact = "/api/v1/impact-analysis";
    public const string AuditLog = "/api/v1/audit-log";

    /// <summary>
    /// The ingredient and the equipment class are created for this drink
    /// alone, so that an analysis triggered by either reaches this drink and
    /// nothing any other test built.
    /// </summary>
    public static async Task<TaughtDrink> NewTaughtDrinkAsync(this BrewForgeApiFactory factory, int certifiedStaff = 2)
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var ingredient = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = $"ING-{tag}", name = $"Special leaf {tag}", unit = "g", shelfLifeHours = 24 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var equipment = await (await admin.PostAsJsonAsync("/api/v1/equipment-classes", new
            {
                equipmentCode = $"EQ-{tag}", equipmentClass = $"DOSER_{tag}", minThreshold = 5, maxThreshold = 50, dosingUnit = "g",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);

        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync(new
        {
            steps = new[]
            {
                RecipeScenario.Step(1, "Dose the special leaf", $"DOSER_{tag}", 30,
                    [RecipeScenario.Use($"ING-{tag}", 20m, "g")], gate: "Level the dose with one tap"),
                RecipeScenario.Step(2, "Add the water", seconds: 20, uses: [RecipeScenario.Use("ING-WATER", 200m, "ml")],
                    dependsOn: [1]),
            },
        });
        var recipeCode = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId).Select(r => r.RecipeCode).SingleAsync());
        var courseId = (await factory.NewPublishedCourseAsync(versionId)).Id();

        var staffIds = new List<long>();
        foreach (var username in new[] { TestUsers.Trainee, "trainee2" }.Take(certifiedStaff))
        {
            staffIds.Add(await factory.UserIdAsync(username));
            await factory.CertifyAsync(staffIds[^1], courseId, versionId);
        }
        var certificateIds = await factory.WithDbAsync(db => db.Certificates.Where(c => c.RecipeVersionId == versionId)
            .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync());

        var live = await factory.GoLiveAsync("B01", recipeId, versionId, liveDaysAgo: 10);
        return new TaughtDrink(ingredient.Id(), equipment.Id(), recipeId, recipeCode, versionId, courseId, certificateIds,
            staffIds, live.BranchId, live.LaunchStatusId);
    }

    public static async Task<JsonElement> AnalyzeAsync(this BrewForgeApiFactory factory, string entityType, long entityId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await (await manager.PostAsJsonAsync(Impact, new { entityType, entityId })).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<HttpResponseMessage> CommitAsync(this BrewForgeApiFactory factory, string runId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await manager.PostAsync($"{Impact}/{runId}/commit", null);
    }

    /// <summary>The number of rows of every table of the schema.</summary>
    public static Task<Dictionary<string, long>> RowCountsAsync(this BrewForgeApiFactory factory) =>
        factory.WithDbAsync(async db =>
        {
            var tables = await db.Database.SqlQuery<string>(
                    $"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'")
                .ToListAsync();
            var counts = new Dictionary<string, long>();
            foreach (var table in tables.Order())
            {
                // The names come from the catalogue of the database itself, not from anyone's input.
#pragma warning disable EF1002
                counts[table] = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"{table}\"").SingleAsync();
#pragma warning restore EF1002
            }
            return counts;
        });

    public static string StateOf(this JsonElement element) => element.GetProperty("state").GetString()!;
}
