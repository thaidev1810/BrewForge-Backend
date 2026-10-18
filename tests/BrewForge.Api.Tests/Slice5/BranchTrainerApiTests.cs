using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using static BrewForge.Api.Tests.Infrastructure.CourseScenario;
using static BrewForge.Api.Tests.Infrastructure.TrainingScenario;

namespace BrewForge.Api.Tests.Slice5;

/// <summary>
/// SRS 3.10.2: "a trainer sees only staff assigned to their own branch". A
/// trainer who belongs to a branch is confined to it; the trainer of the
/// whole chain, who has no branch, is not.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BranchTrainerApiTests(BrewForgeApiFactory factory)
{
    private const string TrainerOfB01 = "trainer1";

    [Fact]
    public async Task Branch_trainer_sees_and_runs_the_classes_of_their_own_branch_only()
    {
        using var branchTrainer = await factory.ClientForAsync(TrainerOfB01);
        using var chainTrainer = await factory.ClientForAsync(TestUsers.Trainer);
        var atB01 = await factory.NewClassAsync(branchCode: "B01");
        var atB02 = await factory.NewClassAsync(branchCode: "B02");

        var listed = (await (await branchTrainer.GetAsync($"{Classes}?size=100")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Contains(atB01.ClassId, listed);
        Assert.DoesNotContain(atB02.ClassId, listed);
        await (await branchTrainer.GetAsync($"{Classes}/{atB02.ClassId}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await branchTrainer.PostAsync($"{Classes}/{atB02.ClassId}/open", JsonContent.Create(new { })))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await branchTrainer.PostAsync($"{Classes}/{atB02.ClassId}/cancel", null)).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // A class is opened at their own branch, and nowhere else.
        object Body(string branchCode, long branchId) => new
        {
            courseId = atB01.CourseId, branchId, name = $"{branchCode} {Guid.NewGuid():N}"[..14],
            startDate = Today, endDate = Today.AddDays(7),
        };
        await (await branchTrainer.PostAsJsonAsync(Classes, Body("B01", await factory.BranchIdAsync("B01")))).ShouldBeAsync(HttpStatusCode.Created);
        var refused = await (await branchTrainer.PostAsJsonAsync(Classes, Body("B02", await factory.BranchIdAsync("B02"))))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("branchId", refused.DetailFields());

        // The trainer of the whole chain sees both.
        var everything = (await (await chainTrainer.GetAsync($"{Classes}?size=100")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Contains(atB01.ClassId, everything);
        Assert.Contains(atB02.ClassId, everything);
    }

    [Fact]
    public async Task Branch_trainer_sees_the_learners_of_their_own_branch_only()
    {
        using var branchTrainer = await factory.ClientForAsync(TrainerOfB01);
        var atB01 = await factory.NewClassAsync(branchCode: "B01");
        var atB02 = await factory.NewClassAsync(branchCode: "B02");
        await factory.OpenClassAsync(atB01.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        await factory.OpenClassAsync(atB02.ClassId, [await factory.UserIdAsync("trainee3")]);
        var own = await factory.EnrollmentIdAsync(atB01.ClassId, TestUsers.Trainee);
        var other = await factory.EnrollmentIdAsync(atB02.ClassId, "trainee3");

        await (await branchTrainer.GetAsync($"{Enrollments}/{own}")).ShouldBeAsync(HttpStatusCode.OK);
        await (await branchTrainer.GetAsync($"{Enrollments}/{other}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await branchTrainer.GetAsync($"{Enrollments}/{other}/quiz-attempts")).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // Their learning paths too.
        await (await branchTrainer.GetAsync($"/api/v1/learning-paths/{await factory.UserIdAsync(TestUsers.Trainee)}")).ShouldBeAsync(HttpStatusCode.OK);
        await (await branchTrainer.GetAsync($"/api/v1/learning-paths/{await factory.UserIdAsync("trainee3")}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await branchTrainer.PostAsync($"/api/v1/learning-paths/{await factory.UserIdAsync("trainee3")}/assign", null))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // And the training progress of their branch alone.
        var progress = await (await branchTrainer.GetAsync("/api/v1/dashboards/training-progress")).ShouldBeAsync(HttpStatusCode.OK);
        var branches = progress.EnumerateArray().Select(row => row.GetProperty("branchCode")).ToList();
        Assert.NotEmpty(branches);
        Assert.All(branches, code => Assert.Equal("B01", code.ValueKind == JsonValueKind.Null ? null : code.GetString()));
    }
}
