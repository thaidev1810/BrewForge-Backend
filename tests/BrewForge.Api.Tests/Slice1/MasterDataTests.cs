using System.Net;
using System.Net.Http.Json;
using BrewForge.Api.Tests.Infrastructure;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>UC-02 to UC-04: API contract section 3.</summary>
[Collection(ApiCollection.Name)]
public sealed class MasterDataTests(BrewForgeApiFactory factory)
{
    private const string Ingredients = "/api/v1/ingredients";
    private const string Equipment = "/api/v1/equipment-classes";
    private const string Branches = "/api/v1/branches";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)].ToUpperInvariant();

    // ---------------------------------------------------------------- ingredients

    [Fact]
    public async Task Ingredient_is_created_in_the_shape_of_the_contract()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique("ING");

        var response = await admin.PostAsJsonAsync(Ingredients, new
        {
            ingredientCode = code, name = "Earl Grey tea leaf", unit = "g", shelfLifeHours = 8,
            storageRule = "Sealed, 18-24 C",
        });

        var created = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.True(created.GetProperty("id").GetInt64() > 0);
        Assert.Equal(code, created.GetProperty("ingredientCode").GetString());
        Assert.Equal("Earl Grey tea leaf", created.GetProperty("name").GetString());
        Assert.Equal("g", created.GetProperty("unit").GetString());
        Assert.Equal(8, created.GetProperty("shelfLifeHours").GetInt32());
        Assert.Equal("Sealed, 18-24 C", created.GetProperty("storageRule").GetString());
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());
        Assert.EndsWith($"{Ingredients}/{created.GetProperty("id").GetInt64()}", response.Headers.Location!.ToString());

        // A head-office role other than ADMIN can read it back.
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        var fetched = await (await specialist.GetAsync(response.Headers.Location)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(code, fetched.GetProperty("ingredientCode").GetString());
    }

    [Fact]
    public async Task Ingredient_list_is_paged_filtered_and_sorted()
    {
        using var client = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var page = await (await client.GetAsync($"{Ingredients}?page=1&size=5&sort=ingredientCode,desc&unit=ml"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(5, page.GetProperty("size").GetInt32());
        Assert.True(page.GetProperty("total").GetInt32() >= 5);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, items.Count);
        Assert.All(items, item => Assert.Equal("ml", item.GetProperty("unit").GetString()));
        var codes = items.Select(item => item.GetProperty("ingredientCode").GetString()!).ToList();
        Assert.Equal(codes.OrderByDescending(c => c, StringComparer.Ordinal), codes);

        var search = await (await client.GetAsync($"{Ingredients}?q=oolong")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(search.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("ingredientCode").GetString() == "ING-OOLONG");
    }

    [Theory]
    [InlineData("size=0", "size")]
    [InlineData("size=101", "size")]
    [InlineData("page=0", "page")]
    [InlineData("sort=passwordHash,asc", "sort")]
    [InlineData("sort=name,sideways", "sort")]
    [InlineData("status=DELETED", "status")]
    public async Task Invalid_list_parameters_are_400_naming_the_parameter(string query, string field)
    {
        using var client = await factory.ClientForAsync(TestUsers.Admin);

        var envelope = await (await client.GetAsync($"{Ingredients}?{query}"))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains(field, envelope.DetailFields());
    }

    [Fact]
    public async Task Ingredient_update_changes_the_rule_but_never_the_code()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique("ING");
        var created = await (await admin.PostAsJsonAsync(Ingredients,
                new { ingredientCode = code, name = "Coconut milk", unit = "ml", shelfLifeHours = 6 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var url = $"{Ingredients}/{created.GetProperty("id").GetInt64()}";

        var updated = await (await admin.PutAsJsonAsync(url,
                new { ingredientCode = code, name = "Coconut milk", unit = "ml", shelfLifeHours = 4, storageRule = "Chilled" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(4, updated.GetProperty("shelfLifeHours").GetInt32());
        Assert.Equal("Chilled", updated.GetProperty("storageRule").GetString());

        var envelope = await (await admin.PutAsJsonAsync(url,
                new { ingredientCode = code + "X", name = "Coconut milk", unit = "ml", shelfLifeHours = 4 }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("ingredientCode", envelope.DetailFields());
    }

    [Fact]
    public async Task Duplicate_ingredient_code_is_409()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Ingredients,
            new { ingredientCode = "ING-OOLONG", name = "Another oolong", unit = "g", shelfLifeHours = 8 });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "UNIQUE");
        Assert.Contains("ingredientCode", envelope.DetailFields());
    }

    [Fact]
    public async Task Invalid_ingredient_is_400_listing_every_bad_field()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Ingredients,
            new { ingredientCode = Unique("ING"), name = "", unit = "g", shelfLifeHours = 0 });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Contains("name", envelope.DetailFields());
        Assert.Contains("shelfLifeHours", envelope.DetailFields());
    }

    [Fact]
    public async Task Unit_outside_the_enumeration_is_400_naming_the_field()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Ingredients,
            new { ingredientCode = Unique("ING"), name = "Sugar", unit = "cup", shelfLifeHours = 8 });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Contains("unit", envelope.DetailFields());
    }

    [Fact]
    public async Task Value_longer_than_its_column_is_400_with_MSG_E11()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Ingredients,
            new { ingredientCode = Unique("ING"), name = new string('x', 121), unit = "g", shelfLifeHours = 8 });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "MSG-E11");
        Assert.Equal("Exceed max length of 120.",
            envelope.GetProperty("details")[0].GetProperty("issue").GetString());
    }

    // ---------------------------------------------------------------- equipment classes

    [Fact]
    public async Task Equipment_class_is_created_in_the_shape_of_the_contract()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique("EQ");

        var created = await (await admin.PostAsJsonAsync(Equipment, new
            {
                equipmentCode = code, equipmentClass = $"COLD_DRIP_{code}", minThreshold = 40.5, maxThreshold = 60,
                dosingUnit = "g",
            }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(code, created.GetProperty("equipmentCode").GetString());
        Assert.Equal($"COLD_DRIP_{code}", created.GetProperty("equipmentClass").GetString());
        Assert.Equal(40.5m, created.GetProperty("minThreshold").GetDecimal());
        Assert.Equal(60m, created.GetProperty("maxThreshold").GetDecimal());
        Assert.Equal("g", created.GetProperty("dosingUnit").GetString());
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Equipment_thresholds_can_be_changed_but_not_inverted()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique("EQ");
        var created = await (await admin.PostAsJsonAsync(Equipment,
                new { equipmentCode = code, equipmentClass = $"CLS_{code}", minThreshold = 10, maxThreshold = 20, dosingUnit = "ml" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var url = $"{Equipment}/{created.GetProperty("id").GetInt64()}";

        var updated = await (await admin.PutAsJsonAsync(url, new { minThreshold = 12, maxThreshold = 18, dosingUnit = "ml" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(12m, updated.GetProperty("minThreshold").GetDecimal());
        Assert.Equal(18m, updated.GetProperty("maxThreshold").GetDecimal());

        var envelope = await (await admin.PutAsJsonAsync(url, new { minThreshold = 30, maxThreshold = 18, dosingUnit = "ml" }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("maxThreshold", envelope.DetailFields());
    }

    [Fact]
    public async Task Duplicate_equipment_class_is_409()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Equipment,
            new { equipmentCode = Unique("EQ"), equipmentClass = "TEA_BREWER", minThreshold = 1, maxThreshold = 2, dosingUnit = "g" });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "UNIQUE");
        Assert.Contains("equipmentClass", envelope.DetailFields());
    }

    [Fact]
    public async Task Seeded_catalogue_is_enough_for_the_validator()
    {
        using var client = await factory.ClientForAsync(TestUsers.RdManager);

        var ingredients = await (await client.GetAsync($"{Ingredients}?size=100")).ShouldBeAsync(HttpStatusCode.OK);
        var equipment = await (await client.GetAsync($"{Equipment}?size=100")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.True(ingredients.GetProperty("total").GetInt32() >= 8);
        Assert.True(equipment.GetProperty("total").GetInt32() >= 4);
        var brewer = equipment.GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("equipmentClass").GetString() == "TEA_BREWER");
        Assert.Equal(15m, brewer.GetProperty("minThreshold").GetDecimal());
        Assert.Equal(25m, brewer.GetProperty("maxThreshold").GetDecimal());
    }

    // ---------------------------------------------------------------- branches

    [Fact]
    public async Task Branch_is_created_updated_and_closed()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique("B");

        var created = await (await admin.PostAsJsonAsync(Branches,
                new { branchCode = code, name = "BrewForge Test Outlet", address = "1 Test Street" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var url = $"{Branches}/{created.GetProperty("id").GetInt64()}";
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());

        var updated = await (await admin.PutAsJsonAsync(url, new { name = "BrewForge Renamed Outlet" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("BrewForge Renamed Outlet", updated.GetProperty("name").GetString());
        Assert.Equal(code, updated.GetProperty("branchCode").GetString());

        var closed = await (await admin.PostAsync($"{url}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());

        var duplicate = await admin.PostAsJsonAsync(Branches, new { branchCode = code, name = "Again" });
        await duplicate.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "UNIQUE");
    }
}
