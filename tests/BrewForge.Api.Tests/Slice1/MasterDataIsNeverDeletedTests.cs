using System.Net;
using System.Net.Http.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Common;
using BrewForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>
/// BR-16: master data may be deactivated but never deleted. The rule is held
/// at three levels: the API offers no DELETE, the domain offers no way to
/// remove a record, and the persistence layer refuses a delete from any code
/// path that tries anyway.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MasterDataIsNeverDeletedTests(BrewForgeApiFactory factory)
{
    public static TheoryData<string> MasterDataResources => ["users", "branches", "ingredients", "equipment-classes"];

    [Theory]
    [MemberData(nameof(MasterDataResources))]
    public async Task Delete_is_405_and_the_record_survives(string resource)
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var first = (await (await admin.GetAsync($"/api/v1/{resource}?size=1")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items")[0].GetProperty("id").GetInt64();

        var response = await admin.DeleteAsync($"/api/v1/{resource}/{first}");

        await response.ShouldBeErrorAsync(HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
        await (await admin.GetAsync($"/api/v1/{resource}/{first}")).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ingredients", "ING-LEMONGRASS", "ingredientCode")]
    [InlineData("equipment-classes", "EQ-BLD-01", "equipmentCode")]
    public async Task Deactivation_changes_the_status_and_keeps_the_row(string resource, string code, string codeField)
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var page = await (await admin.GetAsync($"/api/v1/{resource}?q={code}")).ShouldBeAsync(HttpStatusCode.OK);
        var id = page.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty(codeField).GetString() == code).GetProperty("id").GetInt64();
        var totalBefore = await TotalAsync(admin, resource);

        var deactivated = await (await admin.PostAsync($"/api/v1/{resource}/{id}/deactivate", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("INACTIVE", deactivated.GetProperty("status").GetString());
        Assert.Equal(totalBefore, await TotalAsync(admin, resource));
        var fetched = await (await admin.GetAsync($"/api/v1/{resource}/{id}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("INACTIVE", fetched.GetProperty("status").GetString());

        // Deactivation is reversible, which a delete would not be.
        var body = resource == "ingredients"
            ? (object)new { name = "Lemongrass stalk", unit = "pcs", shelfLifeHours = 12, status = "ACTIVE" }
            : new { minThreshold = 10, maxThreshold = 600, dosingUnit = "ml", status = "ACTIVE" };
        var reactivated = await (await admin.PutAsJsonAsync($"/api/v1/{resource}/{id}", body))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("ACTIVE", reactivated.GetProperty("status").GetString());
    }

    public static TheoryData<string> MasterDataEntities => ["Role", "AppUser", "Branch", "Ingredient", "StandardEquipment"];

    [Theory]
    [MemberData(nameof(MasterDataEntities))]
    public async Task Persistence_refuses_to_delete_master_data_whoever_asks(string entity)
    {
        await factory.WithDbAsync(async db =>
        {
            object row = entity switch
            {
                "Role" => await db.Roles.FirstAsync(),
                "AppUser" => await db.Users.FirstAsync(),
                "Branch" => await db.Branches.FirstAsync(),
                "Ingredient" => await db.Ingredients.FirstAsync(),
                _ => await db.StandardEquipment.FirstAsync(),
            };
            var countBefore = await CountAsync(db, entity);

            db.Remove(row);
            var refusal = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());

            Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
            Assert.Equal("BR-16", refusal.Rule);
            db.ChangeTracker.Clear();
            Assert.Equal(countBefore, await CountAsync(db, entity));
        });
    }

    private static Task<int> CountAsync(BrewForgeDbContext db, string entity) => entity switch
    {
        "Role" => db.Roles.CountAsync(),
        "AppUser" => db.Users.CountAsync(),
        "Branch" => db.Branches.CountAsync(),
        "Ingredient" => db.Ingredients.CountAsync(),
        _ => db.StandardEquipment.CountAsync(),
    };

    private static async Task<int> TotalAsync(HttpClient client, string resource) =>
        (await (await client.GetAsync($"/api/v1/{resource}?size=1")).ShouldBeAsync(HttpStatusCode.OK))
        .GetProperty("total").GetInt32();
}
