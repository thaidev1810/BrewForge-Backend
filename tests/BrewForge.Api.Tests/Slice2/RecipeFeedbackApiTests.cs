using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.RecipeScenario;

namespace BrewForge.Api.Tests.Slice2;

/// <summary>
/// Feedback on a recipe through the API: staff certified on a drink remark
/// on how it is made, and a branch writes down what a customer said.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecipeFeedbackApiTests(BrewForgeApiFactory factory)
{
    private const string Feedback = "/api/v1/recipe-feedback";

    /// <summary>Trainee of B02, where the drinks of these tests are not sold, and certified on none of them.</summary>
    private const string TraineeB02 = "trainee3";

    private static object StaffNote(string content = "Stir before the milk goes in, or it splits.", long? recipeStepId = null) =>
        new { source = "STAFF", category = "PREPARATION_NOTE", content, recipeStepId };

    private static object CustomerRemark(int? rating = 2, string content = "Too sweet for me.") =>
        new { source = "CUSTOMER", category = "TASTE", content, rating };

    private async Task<HttpResponseMessage> SubmitAsync(string username, long recipeId, object body)
    {
        using var client = await factory.ClientForAsync(username);
        return await client.PostAsJsonAsync($"{Recipes}/{recipeId}/feedback", body);
    }

    private async Task<JsonElement> SubmittedAsync(string username, long recipeId, object body) =>
        await (await SubmitAsync(username, recipeId, body)).ShouldBeAsync(HttpStatusCode.Created);

    private async Task<int> SaidAsync(string username, string subject, long recipeId)
    {
        var userId = await factory.UserIdAsync(username);
        return await factory.WithDbAsync(async db =>
        {
            var name = await db.Recipes.Where(r => r.Id == recipeId).Select(r => r.Name).SingleAsync();
            return await db.Notifications.CountAsync(n => n.UserId == userId && n.Subject == subject && n.Message.Contains(name));
        });
    }

    // ---------------------------------------------------------------- staff

    [Fact]
    public async Task Staff_certified_on_a_drink_send_a_remark_about_one_of_its_steps()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var step = (await factory.GetVersionAsync(drink.VersionId)).GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("stepOrder").GetInt32() == 2);

        var created = await SubmittedAsync(TestUsers.Trainee, drink.RecipeId,
            StaffNote("Pour the water down the side, not onto the leaf.", step.Id()));

        Assert.Equal((drink.RecipeId, drink.VersionId), (created.GetProperty("recipeId").GetInt64(), created.GetProperty("recipeVersionId").GetInt64()));
        Assert.Equal(drink.RecipeCode, created.GetProperty("recipeCode").GetString());
        Assert.Equal((1, 2), (created.GetProperty("versionNo").GetInt32(), created.GetProperty("stepOrder").GetInt32()));
        Assert.Equal(("STAFF", "PREPARATION_NOTE", "NEW"), (created.GetProperty("source").GetString(),
            created.GetProperty("category").GetString(), created.GetProperty("state").GetString()));
        Assert.Equal("B01", created.GetProperty("branchCode").GetString());
        Assert.Equal(await factory.UserIdAsync(TestUsers.Trainee), created.GetProperty("submittedBy").GetInt64());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("rating").ValueKind);

        // The author of the recipe hears of it.
        Assert.Equal(1, await SaidAsync(TestUsers.RdSpecialist, "New feedback on a recipe", drink.RecipeId));
        Assert.Equal(["CREATE"], await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "RecipeFeedback" && a.EntityId == created.Id()).Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task Staff_feedback_is_refused_from_somebody_who_was_not_certified_on_the_drink()
    {
        var drink = await factory.NewTaughtDrinkAsync();

        // Neither a trainee of another branch nor a trainer who never took the course.
        await (await SubmitAsync(TraineeB02, drink.RecipeId, StaffNote()))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "FEEDBACK_REQUIRES_TRAINING");
        await (await SubmitAsync(TestUsers.Trainer, drink.RecipeId, StaffNote()))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "FEEDBACK_REQUIRES_TRAINING");
    }

    [Fact]
    public async Task Staff_feedback_is_validated()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var other = await factory.NewReleasedRecipeAsync();

        Assert.Contains("content", (await (await SubmitAsync(TestUsers.Trainee, drink.RecipeId, StaffNote(" ")))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        Assert.Contains("rating", (await (await SubmitAsync(TestUsers.Trainee, drink.RecipeId,
                new { source = "STAFF", category = "TASTE", content = "Fine.", rating = 5 }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        Assert.Contains("recipeStepId", (await (await SubmitAsync(TestUsers.Trainee, drink.RecipeId, StaffNote(recipeStepId: 424242)))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        // A version of some other drink.
        Assert.Contains("recipeVersionId", (await (await SubmitAsync(TestUsers.Trainee, drink.RecipeId,
                new { source = "STAFF", category = "OTHER", content = "Fine.", recipeVersionId = other.VersionId }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        Assert.Contains("source", (await (await SubmitAsync(TestUsers.Trainee, drink.RecipeId, new { content = "Fine." }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        await (await SubmitAsync(TestUsers.Trainee, 999999999, StaffNote())).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- customers

    [Fact]
    public async Task Branch_writes_down_what_a_customer_said_about_the_version_it_sells()
    {
        var drink = await factory.NewTaughtDrinkAsync(); // on sale at B01

        var created = await SubmittedAsync(TestUsers.BranchManager, drink.RecipeId, CustomerRemark(rating: 2));

        Assert.Equal(("CUSTOMER", "TASTE", 2), (created.GetProperty("source").GetString(),
            created.GetProperty("category").GetString(), created.GetProperty("rating").GetInt32()));
        // The version is the one on sale there; nobody had to say which.
        Assert.Equal(drink.VersionId, created.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal((drink.BranchId, "B01"), (created.GetProperty("branchId").GetInt64(), created.GetProperty("branchCode").GetString()));

        // Whoever served the customer can write it down, certified or not.
        await SubmittedAsync("trainee2", drink.RecipeId, CustomerRemark(rating: 5, content: "Best oolong in town."));
    }

    [Fact]
    public async Task Customer_feedback_needs_a_rating_and_a_branch_that_sells_the_drink()
    {
        var drink = await factory.NewTaughtDrinkAsync(); // on sale at B01 only

        Assert.Contains("rating", (await (await SubmitAsync(TestUsers.BranchManager, drink.RecipeId, CustomerRemark(rating: null)))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        Assert.Contains("rating", (await (await SubmitAsync(TestUsers.BranchManager, drink.RecipeId, CustomerRemark(rating: 6)))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());

        await (await SubmitAsync(TestUsers.BranchManagerB02, drink.RecipeId, CustomerRemark()))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "FEEDBACK_NOT_ON_SALE");
        // A trainer works at no branch, and serves no customers.
        Assert.Contains("source", (await (await SubmitAsync(TestUsers.Trainer, drink.RecipeId, CustomerRemark()))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
    }
}
