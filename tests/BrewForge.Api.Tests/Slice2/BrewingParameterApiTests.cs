using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>
/// The temperature and the pressure of a step through the API: stored with
/// the draft, checked against a machine set by temperature (BR-10) and
/// against the window a leaf is brewed in (BR-11), and the window itself as
/// master data.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BrewingParameterApiTests : IDisposable
{
    private readonly BrewForgeApiFactory _factory;

    public BrewingParameterApiTests(BrewForgeApiFactory factory)
    {
        _factory = factory;
        _factory.DraftModel.Reset();
    }

    public void Dispose() => _factory.DraftModel.Reset();

    /// <summary>A kettle set between 80 and 100 C and a green tea brewed at 75 to 85 C, made for one test.</summary>
    private sealed record TeaSet(string Kettle, string Leaf, long LeafId);

    private async Task<TeaSet> NewTeaSetAsync()
    {
        using var admin = await _factory.ClientForAsync(TestUsers.Admin);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await (await admin.PostAsJsonAsync("/api/v1/equipment-classes", new
        {
            equipmentCode = $"EQ-{tag}", equipmentClass = $"KETTLE_{tag}", minThreshold = 80, maxThreshold = 100, dosingUnit = "degC",
        })).ShouldBeAsync(HttpStatusCode.Created);
        var leaf = await (await admin.PostAsJsonAsync("/api/v1/ingredients", new
        {
            ingredientCode = $"ING-{tag}", name = $"Green tea leaf {tag}", unit = "g", shelfLifeHours = 6,
            brewTempMinC = 75, brewTempMaxC = 85,
        })).ShouldBeAsync(HttpStatusCode.Created);
        return new TeaSet($"KETTLE_{tag}", $"ING-{tag}", leaf.Id());
    }

    private static object Content(TeaSet tea, decimal? heatTo, decimal? brewAt) => new
    {
        steps = new object[]
        {
            new
            {
                stepOrder = 1, actionText = "Heat the water", equipmentClass = tea.Kettle, durationSeconds = 120,
                temperatureC = heatTo, ingredients = new[] { Use("ING-WATER", 300m, "ml") },
            },
            new
            {
                stepOrder = 2, actionText = "Brew the green tea", durationSeconds = 180, temperatureC = brewAt,
                ingredients = new[] { Use(tea.Leaf, 6m, "g") }, dependsOn = new[] { new { stepOrder = 1, type = "REQUIRES_OUTPUT" } },
            },
        },
    };

    private async Task<JsonElement> ValidateAsync(object content)
    {
        using var specialist = await _factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await _factory.NewDraftAsync(content);
        return await (await specialist.PostAsync($"{Versions}/{versionId}/validate", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    private static JsonElement CheckOf(JsonElement result, string type) =>
        result.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("checkType").GetString() == type);

    // ---------------------------------------------------------------- the settings of a step

    [Fact]
    public async Task Draft_keeps_the_temperature_and_the_pressure_its_steps_state()
    {
        var tea = await NewTeaSetAsync();
        var (_, versionId) = await _factory.NewDraftAsync(new
        {
            steps = new object[]
            {
                new { stepOrder = 1, actionText = "Heat the water", equipmentClass = tea.Kettle, temperatureC = 82.5, ingredients = Array.Empty<object>() },
                new { stepOrder = 2, actionText = "Extract", pressureBar = 9, ingredients = Array.Empty<object>() },
                new { stepOrder = 3, actionText = "Serve", ingredients = Array.Empty<object>() },
            },
        });

        var steps = (await _factory.GetVersionAsync(versionId)).GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal((82.5m, JsonValueKind.Null), (steps[0].GetProperty("temperatureC").GetDecimal(), steps[0].GetProperty("pressureBar").ValueKind));
        Assert.Equal((JsonValueKind.Null, 9m), (steps[1].GetProperty("temperatureC").ValueKind, steps[1].GetProperty("pressureBar").GetDecimal()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (steps[2].GetProperty("temperatureC").ValueKind, steps[2].GetProperty("pressureBar").ValueKind));
    }

    [Fact]
    public async Task Setting_that_no_recipe_could_mean_is_refused_with_the_field_named()
    {
        using var specialist = await _factory.ClientForAsync(TestUsers.RdSpecialist);
        var (_, versionId) = await _factory.NewDraftAsync();
        object Step(object setting) => new { steps = new[] { setting } };

        var tooHot = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
            Step(new { stepOrder = 1, actionText = "Boil", temperatureC = 140, ingredients = Array.Empty<object>() })))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        var noPressure = await (await specialist.PutAsJsonAsync($"{Versions}/{versionId}",
            Step(new { stepOrder = 1, actionText = "Extract", pressureBar = 0, ingredients = Array.Empty<object>() })))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("steps[0].temperatureC", tooHot.DetailFields());
        Assert.Contains("steps[0].pressureBar", noPressure.DetailFields());
    }

    [Fact]
    public async Task BR_10_a_machine_set_by_temperature_is_checked_against_the_temperature_of_the_step()
    {
        var tea = await NewTeaSetAsync();

        var untold = await ValidateAsync(Content(tea, heatTo: null, brewAt: null));
        var tooCold = await ValidateAsync(Content(tea, heatTo: 70m, brewAt: null));
        var right = await ValidateAsync(Content(tea, heatTo: 80m, brewAt: 80m));

        var missing = Assert.Single(CheckOf(untold, "EQUIPMENT").GetProperty("violations").EnumerateArray());
        Assert.Equal(("BR-10", "no temperature", 1), (missing.GetProperty("rule").GetString(), missing.GetProperty("actual").GetString(),
            missing.GetProperty("stepOrder").GetInt32()));
        var outside = Assert.Single(CheckOf(tooCold, "EQUIPMENT").GetProperty("violations").EnumerateArray());
        Assert.Equal(("MSG-E10", "80.0 - 100.0", "70.0"), (outside.GetProperty("code").GetString(),
            outside.GetProperty("expected").GetString(), outside.GetProperty("actual").GetString()));
        Assert.True(right.GetProperty("passed").GetBoolean());
    }

    // ---------------------------------------------------------------- the brewing window

    [Fact]
    public async Task BR_11_a_leaf_is_held_to_the_window_it_is_brewed_in_when_the_step_states_its_temperature()
    {
        var tea = await NewTeaSetAsync();

        var scalded = await ValidateAsync(Content(tea, heatTo: 100m, brewAt: 100m));
        var unstated = await ValidateAsync(Content(tea, heatTo: 100m, brewAt: null));

        // The kettle is within its range; it is the leaf that cannot take boiling water. One failed check fails the whole (BR-08).
        Assert.False(scalded.GetProperty("passed").GetBoolean());
        Assert.True(CheckOf(scalded, "EQUIPMENT").GetProperty("passed").GetBoolean());
        var violation = Assert.Single(CheckOf(scalded, "INGREDIENT").GetProperty("violations").EnumerateArray());
        Assert.Equal(("BR-11", 2, "75 - 85", "100"), (violation.GetProperty("rule").GetString(), violation.GetProperty("stepOrder").GetInt32(),
            violation.GetProperty("expected").GetString(), violation.GetProperty("actual").GetString()));
        Assert.Contains(tea.Leaf, violation.GetProperty("message").GetString());

        // A step that states no temperature is not held to the window.
        Assert.True(unstated.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Brewing_window_of_a_leaf_is_master_data_the_admin_keeps()
    {
        using var admin = await _factory.ClientForAsync(TestUsers.Admin);
        var tea = await NewTeaSetAsync();
        var url = $"/api/v1/ingredients/{tea.LeafId}";
        object Leaf(decimal? min, decimal? max) => new { name = "Green tea leaf", unit = "g", shelfLifeHours = 6, brewTempMinC = min, brewTempMaxC = max };

        var created = await (await admin.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((75m, 85m), (created.GetProperty("brewTempMinC").GetDecimal(), created.GetProperty("brewTempMaxC").GetDecimal()));

        var widened = await (await admin.PutAsJsonAsync(url, Leaf(70m, 90m))).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((70m, 90m), (widened.GetProperty("brewTempMinC").GetDecimal(), widened.GetProperty("brewTempMaxC").GetDecimal()));

        // Half a window, or one upside down, is not a window.
        Assert.Contains("brewTempMaxC", (await (await admin.PutAsJsonAsync(url, Leaf(70m, null))).ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        Assert.Contains("brewTempMinC", (await (await admin.PutAsJsonAsync(url, Leaf(95m, 80m))).ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());

        var removed = await (await admin.PutAsJsonAsync(url, Leaf(null, null))).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (removed.GetProperty("brewTempMinC").ValueKind, removed.GetProperty("brewTempMaxC").ValueKind));
    }

    // ---------------------------------------------------------------- seed

    [Fact]
    public async Task Seeded_teas_state_the_temperature_of_the_water_and_their_leaves_have_a_window()
    {
        var leaves = await _factory.WithDbAsync(db => db.Ingredients
            .Where(i => i.BrewTempMinC != null).ToDictionaryAsync(i => i.IngredientCode, i => (i.BrewTempMinC, i.BrewTempMaxC)));
        Assert.Equal((85m, 95m), leaves["ING-OOLONG"]);
        Assert.Equal((90m, 100m), leaves["ING-BLACKTEA"]);
        Assert.Equal((75m, 85m), leaves["ING-JASMINE"]);
        Assert.Equal((70m, 80m), leaves["ING-MATCHA"]);

        // What the gate of the first step says in words, the step now says as a figure, inside the window of its leaf.
        var brewing = await _factory.WithDbAsync(db => db.RecipeVersions
            .Where(v => db.Recipes.Any(r => r.Id == v.RecipeId && r.RecipeCode == "R01"))
            .SelectMany(v => v.Steps).Where(s => s.StepOrder == 1).SingleAsync());
        Assert.Equal(("Water at 90 C", 90m), (brewing.TechniqueGate, brewing.TemperatureC));
    }

    // ---------------------------------------------------------------- the language model

    [Fact]
    public async Task Model_answer_may_state_the_temperature_of_a_step_and_it_is_checked_like_any_other()
    {
        var tea = await NewTeaSetAsync();
        var (_, versionId) = await _factory.NewDraftAsync();
        using var specialist = await _factory.ClientForAsync(TestUsers.RdSpecialist);
        _factory.DraftModel.Answer(JsonSerializer.Serialize(new
        {
            drinkName = "Green tea", category = "TEA",
            steps = new object[]
            {
                new
                {
                    stepOrder = 1, actionText = "Heat the water", equipmentClass = tea.Kettle, techniqueGate = (string?)null, durationSeconds = 120,
                    temperatureC = 82.46, pressureBar = (decimal?)null,
                    ingredients = new[] { new { ingredientCode = "ING-WATER", quantity = 300, unit = "ml" } }, dependsOnSteps = Array.Empty<object>(),
                },
            },
        }));

        var result = await (await specialist.PostAsJsonAsync($"{Versions}/{versionId}/generate-draft", new { description = "A plain green tea" }))
            .ShouldBeAsync(HttpStatusCode.OK);

        // Stored to one decimal place, and within the range of the kettle.
        Assert.Equal(82.5m, result.GetProperty("version").GetProperty("steps")[0].GetProperty("temperatureC").GetDecimal());
        Assert.True(result.GetProperty("validation").GetProperty("passed").GetBoolean());
        // The model was told about the brewing window of the leaf.
        Assert.Contains($"{tea.Leaf} | Green tea leaf", Assert.Single(_factory.DraftModel.Calls).UserPrompt);
        Assert.Contains("| 75-85", _factory.DraftModel.Calls[0].UserPrompt);
        Assert.Contains("temperatureC", _factory.DraftModel.Calls[0].JsonSchema);
    }
}
