using System.Net;
using System.Net.Http.Json;
using BrewForge.Api.Tests.Infrastructure;

namespace BrewForge.Api.Tests.Slice10;

/// <summary>The categories an administrator maintains.</summary>
[Collection(ApiCollection.Name)]
public sealed class CategoryApiTests(BrewForgeApiFactory factory)
{
    private const string Categories = "/api/v1/categories";

    private static string Unique() => $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    [Fact]
    public async Task Admin_creates_a_category_and_everyone_can_read_it()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique();

        var response = await admin.PostAsJsonAsync(Categories, new { kind = "DRINK", code = code.ToLowerInvariant(), name = "Smoothie" });

        var created = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("DRINK", created.GetProperty("kind").GetString());
        Assert.Equal(code, created.GetProperty("code").GetString());
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());

        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var fetched = await (await trainee.GetAsync(response.Headers.Location)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Smoothie", fetched.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Code_is_unique_within_a_kind_but_may_repeat_across_kinds()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var code = Unique();
        await (await admin.PostAsJsonAsync(Categories, new { kind = "DRINK", code, name = "One" })).ShouldBeAsync(HttpStatusCode.Created);

        var duplicate = await admin.PostAsJsonAsync(Categories, new { kind = "DRINK", code, name = "Two" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        await (await admin.PostAsJsonAsync(Categories, new { kind = "TRAINING", code, name = "Three" }))
            .ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task List_filters_by_kind()
    {
        using var client = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var page = await (await client.GetAsync($"{Categories}?kind=EQUIPMENT&size=100")).ShouldBeAsync(HttpStatusCode.OK);

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal("EQUIPMENT", item.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Category_is_deactivated_never_deleted_and_its_kind_and_code_are_fixed()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var created = await (await admin.PostAsJsonAsync(Categories, new { kind = "INGREDIENT", code = Unique(), name = "Herb" }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        var code = created.GetProperty("code").GetString();

        var renamed = await (await admin.PutAsJsonAsync($"{Categories}/{id}", new { kind = "INGREDIENT", code, name = "Fresh herb" }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Fresh herb", renamed.GetProperty("name").GetString());

        var otherKind = await admin.PutAsJsonAsync($"{Categories}/{id}", new { kind = "DRINK", code, name = "Herb" });
        Assert.Equal(HttpStatusCode.BadRequest, otherKind.StatusCode);
        var otherCode = await admin.PutAsJsonAsync($"{Categories}/{id}", new { kind = "INGREDIENT", code = "OTHER", name = "Herb" });
        Assert.Equal(HttpStatusCode.BadRequest, otherCode.StatusCode);

        var off = await (await admin.PostAsync($"{Categories}/{id}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("INACTIVE", off.GetProperty("status").GetString());
        Assert.False((await admin.DeleteAsync($"{Categories}/{id}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Missing_fields_are_400()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Categories, new { name = "No kind" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
