using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice9;

/// <summary>
/// What follows a recipe change for the people certified on the old version:
/// the two versions are compared, a recertification course is built from
/// what differs, those people take it, and the certificate they earn on the
/// new version replaces the one that was flagged.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecertificationApiTests(BrewForgeApiFactory factory)
{
    private const string Versions = "/api/v1/recipe-versions";

    /// <summary>A drink taught on version 1 and since changed: version 2 is released and version 1 superseded.</summary>
    private sealed record ChangedDrink(long RecipeId, long FirstVersionId, long SecondVersionId, long FirstCourseId,
        long TraineeId, long FirstCertificateId);

    [Fact]
    public async Task Two_versions_of_a_recipe_are_compared_step_by_step()
    {
        var drink = await NewChangedDrinkAsync();
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);

        var diff = await (await manager.GetAsync($"{Versions}/{drink.SecondVersionId}/diff")).ShouldBeAsync(HttpStatusCode.OK);

        // Without ?against it is compared with the version that was in production before it.
        Assert.Equal((drink.RecipeId, drink.FirstVersionId, 1, "SUPERSEDED", drink.SecondVersionId, 2, "RELEASED"),
            (diff.GetProperty("recipeId").GetInt64(), diff.GetProperty("before").Id(), diff.GetProperty("before").GetProperty("versionNo").GetInt32(),
                diff.GetProperty("before").GetProperty("state").GetString(), diff.GetProperty("after").Id(),
                diff.GetProperty("after").GetProperty("versionNo").GetInt32(), diff.GetProperty("after").GetProperty("state").GetString()));
        Assert.Equal((true, 0, 1, 2, 0), (diff.GetProperty("hasChanges").GetBoolean(), diff.GetProperty("added").GetInt32(),
            diff.GetProperty("removed").GetInt32(), diff.GetProperty("changed").GetInt32(), diff.GetProperty("unchanged").GetInt32()));

        var steps = diff.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["CHANGED", "CHANGED", "REMOVED"], steps.Select(step => step.GetProperty("kind").GetString()));

        // The brewing was reworded and brews less, for a shorter time, in cooler water.
        var brew = steps[0];
        Assert.Equal(("Brew the oolong", "Brew a lighter oolong"), (brew.GetProperty("before").GetProperty("actionText").GetString(),
            brew.GetProperty("after").GetProperty("actionText").GetString()));
        Assert.Equal(["actionText", "techniqueGate", "durationSeconds", "ingredients"],
            brew.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));
        var oolong = brew.GetProperty("ingredientChanges").EnumerateArray().Single(i => i.GetProperty("ingredientCode").GetString() == "ING-OOLONG");
        Assert.Equal((18m, 16m, "g"), (oolong.GetProperty("quantityBefore").GetDecimal(), oolong.GetProperty("quantityAfter").GetDecimal(),
            oolong.GetProperty("unitAfter").GetString()));
        // The water is no longer measured in that step at all.
        var water = brew.GetProperty("ingredientChanges").EnumerateArray().Single(i => i.GetProperty("ingredientCode").GetString() == "ING-WATER");
        Assert.Equal((300m, JsonValueKind.Null), (water.GetProperty("quantityBefore").GetDecimal(), water.GetProperty("quantityAfter").ValueKind));

        // The garnish is gone.
        Assert.Equal(("Garnish with peach", JsonValueKind.Null), (steps[2].GetProperty("before").GetProperty("actionText").GetString(),
            steps[2].GetProperty("after").ValueKind));

        // Compared the other way round, on request, the same step was added.
        var reverse = await (await manager.GetAsync($"{Versions}/{drink.FirstVersionId}/diff?against={drink.SecondVersionId}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal((1, 0), (reverse.GetProperty("added").GetInt32(), reverse.GetProperty("removed").GetInt32()));
    }

    [Fact]
    public async Task Version_with_nothing_before_it_and_versions_of_two_recipes_are_not_compared()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var (_, only) = await factory.NewReleasedRecipeAsync();
        var (_, other) = await factory.NewReleasedRecipeAsync();

        await (await manager.GetAsync($"{Versions}/{only}/diff")).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "NO_EARLIER_VERSION");
        var mixed = await (await manager.GetAsync($"{Versions}/{only}/diff?against={other}")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        await (await manager.GetAsync($"{Versions}/{only}/diff?against=999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        Assert.Contains("against", mixed.DetailFields());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A drink with a published course on version 1 and the seeded trainee
    /// certified on it, then changed: version 2 is lighter, shorter and has
    /// no garnish.
    /// </summary>
    private async Task<ChangedDrink> NewChangedDrinkAsync()
    {
        var (recipeId, firstVersion) = await factory.NewReleasedRecipeAsync();
        var firstCourse = (await factory.NewPublishedCourseAsync(firstVersion)).Id();
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);
        await factory.CertifyAsync(traineeId, firstCourse, firstVersion);
        var certificateId = await factory.WithDbAsync(db => db.Certificates
            .Where(c => c.UserId == traineeId && c.RecipeVersionId == firstVersion).Select(c => c.Id).SingleAsync());

        var secondVersion = await factory.ReleaseNewVersionAsync(recipeId, RecipeScenario.LighterContent());
        return new ChangedDrink(recipeId, firstVersion, secondVersion, firstCourse, traineeId, certificateId);
    }

    private async Task<JsonElement> NewRecertificationCourseAsync(ChangedDrink drink)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.PostAsJsonAsync(Courses, new
        {
            recipeVersionId = drink.SecondVersionId, courseType = "RECERTIFICATION", title = $"What changed {Guid.NewGuid():N}"[..24],
        })).ShouldBeAsync(HttpStatusCode.Created);
    }

    private async Task<JsonElement> PublishAsync(JsonElement course)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);
        await factory.CompleteAuthoringAsync(course.Id());
        await (await trainer.PostAsync($"{Courses}/{course.Id()}/submit", null)).ShouldBeAsync(HttpStatusCode.OK);
        return await (await manager.PostAsync($"{Courses}/{course.Id()}/approve", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    private async Task<long> NewClassAsync(long courseId, string branchCode = "B01")
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return (await (await trainer.PostAsJsonAsync(Classes, new
        {
            courseId, branchId = await factory.BranchIdAsync(branchCode), name = $"Recert {Guid.NewGuid():N}"[..16],
            startDate = Today, endDate = Today.AddDays(7),
        })).ShouldBeAsync(HttpStatusCode.Created)).Id();
    }

    private async Task<List<JsonElement>> NeedsAsync(long courseId)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return [.. (await (await trainer.GetAsync($"/api/v1/training-needs?courseId={courseId}")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()];
    }

    private Task<CertificateStatus> StatusAsync(long certificateId) =>
        factory.WithDbAsync(db => db.Certificates.Where(c => c.Id == certificateId).Select(c => c.Status).SingleAsync());
}
