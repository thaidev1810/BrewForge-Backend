using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>
/// The variants of a recipe version through the API: kept with the draft,
/// served quantity by quantity, validated one by one (BR-08), frozen on
/// release in the application and in the database (BR-01), and carried over
/// to the next version.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecipeVariantApiTests(BrewForgeApiFactory factory)
{
    private static object Variant(string code, decimal scale, params (string Code, decimal Scale)[] ingredients) => new
    {
        code, name = $"Serving {code}", scale,
        ingredients = ingredients.Select(i => new { ingredientCode = i.Code, scale = i.Scale }),
    };

    private async Task<HttpResponseMessage> PutVariantsAsync(long versionId, params object[] variants)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        return await specialist.PutAsJsonAsync($"{Versions}/{versionId}/variants", new { variants });
    }

    private async Task<JsonElement> ValidateAsync(long versionId)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        return await (await specialist.PostAsync($"{Versions}/{versionId}/validate", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    // ValidContent: 18 g of oolong in the 15-25 g brewer, 300 ml of water, 120 ml of milk base, 2 slices of peach.

    [Fact]
    public async Task Variants_are_kept_with_the_draft_and_served_quantity_by_quantity()
    {
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        var saved = await (await PutVariantsAsync(versionId,
            Variant("s", 0.8m, ("ING-OOLONG", 1m)), Variant("L-HOT", 1.25m, ("ING-PEACH", 0m)))).ShouldBeAsync(HttpStatusCode.OK);

        // Kept by code, upper case, in the order of the codes.
        var variants = saved.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal([("L-HOT", 1.25m), ("S", 0.8m)], variants.Select(v => (v.GetProperty("code").GetString(), v.GetProperty("scale").GetDecimal())));
        var peach = Assert.Single(variants[0].GetProperty("ingredients").EnumerateArray());
        Assert.Equal(("ING-PEACH", 0m), (peach.GetProperty("ingredientCode").GetString(), peach.GetProperty("scale").GetDecimal()));
        Assert.Equal("DRAFT", saved.GetProperty("state").GetString());

        // As served: every step is there, with what this variant uses beside what the version prescribes.
        var served = await (await manager.GetAsync($"{Versions}/{versionId}/variants/l-hot")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(("L-HOT", "Serving L-HOT", 1.25m), (served.GetProperty("variantCode").GetString(),
            served.GetProperty("variantName").GetString(), served.GetProperty("scale").GetDecimal()));
        var steps = served.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["Brew the oolong", "Add the milk base", "Garnish with peach"], steps.Select(s => s.GetProperty("actionText").GetString()));
        Assert.Equal([("ING-OOLONG", 22.5m, 18m), ("ING-WATER", 375m, 300m)], steps[0].GetProperty("ingredients").EnumerateArray()
            .Select(i => (i.GetProperty("ingredientCode").GetString(), i.GetProperty("quantity").GetDecimal(), i.GetProperty("baseQuantity").GetDecimal())));
        Assert.Equal(150m, steps[1].GetProperty("ingredients")[0].GetProperty("quantity").GetDecimal());
        // Hot, so no garnish: the step is still done, with nothing in it.
        Assert.Empty(steps[2].GetProperty("ingredients").EnumerateArray());
        Assert.Equal("Water at 90 C", steps[0].GetProperty("techniqueGate").GetString());

        await (await manager.GetAsync($"{Versions}/{versionId}/variants/XL")).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // An empty list removes them.
        var cleared = await (await PutVariantsAsync(versionId)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(cleared.GetProperty("variants").EnumerateArray());
    }

    [Fact]
    public async Task BR_08_a_version_passes_only_if_every_way_it_is_served_passes()
    {
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        Assert.True((await ValidateAsync(versionId)).GetProperty("passed").GetBoolean());

        // Half as much again of everything is 27 g of leaf in a brewer that takes 25.
        await (await PutVariantsAsync(versionId, Variant("M", 1m), Variant("L", 1.5m))).ShouldBeAsync(HttpStatusCode.OK);
        var failed = await ValidateAsync(versionId);

        Assert.False(failed.GetProperty("passed").GetBoolean());
        var equipment = failed.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("checkType").GetString() == "EQUIPMENT");
        var violation = Assert.Single(equipment.GetProperty("violations").EnumerateArray());
        Assert.Equal(("BR-10", "L", 1, "MSG-E10"), (violation.GetProperty("rule").GetString(), violation.GetProperty("variant").GetString(),
            violation.GetProperty("stepOrder").GetInt32(), violation.GetProperty("code").GetString()));
        Assert.StartsWith("Variant L: 27.0 g is outside", violation.GetProperty("message").GetString());
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, rule: "BR-08");

        // The large one is more milk and water, not more leaf: now every serving is within the machine.
        await (await PutVariantsAsync(versionId, Variant("M", 1m), Variant("L", 1.5m, ("ING-OOLONG", 1m)))).ShouldBeAsync(HttpStatusCode.OK);
        var passed = await ValidateAsync(versionId);
        Assert.True(passed.GetProperty("passed").GetBoolean());
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BR_01_the_variants_of_a_released_version_are_frozen_in_the_application_and_in_the_database()
    {
        var (_, draftId) = await factory.NewDraftAsync(ValidContent());
        await (await PutVariantsAsync(draftId, Variant("L", 1.2m))).ShouldBeAsync(HttpStatusCode.OK);
        var hashBefore = (await ReleaseAsync(draftId)).GetProperty("version").GetProperty("contentHash").GetString();

        await (await PutVariantsAsync(draftId, Variant("XL", 1.3m))).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E06", "BR-01");
        var released = await factory.GetVersionAsync(draftId);
        Assert.Equal(["L"], released.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("code").GetString()));
        Assert.Equal(hashBefore, released.GetProperty("contentHash").GetString());

        // Whatever goes around the application meets the trigger.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO recipe_variant (recipe_version_id, variant_code, name, scale) VALUES ({draftId}, 'XL', 'Extra large', 1.3)")));
        var update = await Assert.ThrowsAsync<PostgresException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE recipe_variant SET scale = 2 WHERE recipe_version_id = {draftId}")));
        Assert.All(new[] { insert, update }, exception => Assert.Contains("BR-01", exception.MessageText));
    }

    [Fact]
    public async Task Content_hash_of_a_release_covers_its_variants()
    {
        var (_, plain) = await factory.NewDraftAsync(ValidContent());
        var (_, withVariant) = await factory.NewDraftAsync(ValidContent());
        await (await PutVariantsAsync(withVariant, Variant("L", 1.2m))).ShouldBeAsync(HttpStatusCode.OK);

        var plainHash = (await ReleaseAsync(plain)).GetProperty("version").GetProperty("contentHash").GetString();
        var variantHash = (await ReleaseAsync(withVariant)).GetProperty("version").GetProperty("contentHash").GetString();

        Assert.NotEqual(plainHash, variantHash);
    }

    [Fact]
    public async Task Variants_are_carried_over_to_a_copy_and_to_a_rollback()
    {
        var (recipeId, firstId) = await factory.NewDraftAsync(ValidContent());
        await (await PutVariantsAsync(firstId, Variant("L", 1.2m), Variant("HOT", 1m, ("ING-PEACH", 0m)))).ShouldBeAsync(HttpStatusCode.OK);
        await ReleaseAsync(firstId);
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        static IEnumerable<string?> Codes(JsonElement version) => version.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("code").GetString());

        var copy = await (await specialist.PostAsJsonAsync($"{Recipes}/{recipeId}/versions", new { copyFromVersionId = firstId }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var rollback = await (await manager.PostAsync($"{Versions}/{firstId}/rollback", null)).ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(["HOT", "L"], Codes(copy));
        Assert.Equal(["HOT", "L"], Codes(rollback));
        // The copy is a draft of its own: its variants can change, and those of the released version do not.
        await (await PutVariantsAsync(copy.Id())).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(["HOT", "L"], Codes(await factory.GetVersionAsync(firstId)));
    }

    [Fact]
    public async Task Variant_that_is_not_a_serving_of_this_recipe_is_refused_with_the_field_named()
    {
        var (_, versionId) = await factory.NewDraftAsync(ValidContent());
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);

        var sameCode = await (await PutVariantsAsync(versionId, Variant("L", 1.2m), Variant("l", 1.3m))).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noScale = await (await PutVariantsAsync(versionId, new { code = "L", name = "Large" })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var tooBig = await (await PutVariantsAsync(versionId, Variant("L", 9m))).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var notUsed = await (await PutVariantsAsync(versionId, Variant("L", 1.2m, ("ING-MATCHA", 1m)))).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var unknown = await (await PutVariantsAsync(versionId, Variant("L", 1.2m, ("ING-NOT-THERE", 1m)))).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var missing = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}/variants", new { })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("variants", sameCode.DetailFields());
        Assert.Contains("variants[0].scale", noScale.DetailFields());
        Assert.Contains("variants[0].scale", tooBig.DetailFields());
        Assert.Contains("variants[0].ingredients[0].ingredientId", notUsed.DetailFields());
        Assert.Contains("variants[0].ingredients[0].ingredientId", unknown.DetailFields());
        Assert.Contains("variants", missing.DetailFields());
        Assert.Empty((await factory.GetVersionAsync(versionId)).GetProperty("variants").EnumerateArray());
    }

    /// <summary>Submits the draft as the specialist and releases it as the manager.</summary>
    private async Task<JsonElement> ReleaseAsync(long versionId)
    {
        using var specialist = await factory.ClientForAsync(TestUsers.RdSpecialist);
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        await (await specialist.PostAsync($"{Versions}/{versionId}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        return await (await manager.PostAsync($"{Versions}/{versionId}/release", null)).ShouldBeAsync(HttpStatusCode.OK);
    }
}
