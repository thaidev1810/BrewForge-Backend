using System.Net;
using System.Text.Json;
using BrewForge.Api.Scheduling;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Launch;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BrewForge.Api.Tests.Infrastructure.PilotScenario;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice8;

/// <summary>
/// The people who have to act next in a drink's launch are told so: training for a planned branch,
/// a branch ready to open, a drink on sale or withdrawn, a pilot that ended, and missing sales.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LaunchNoticeApiTests(BrewForgeApiFactory factory)
{
    [Fact]
    public async Task Starting_a_pilot_asks_for_training_where_staff_are_short_and_reports_branches_already_ready()
    {
        var pilot = await factory.NewPilotAsync(["B01", "B02"]);
        await factory.CertifyStaffAsync(pilot, "B01");

        await factory.StartPilotAsync(pilot.PilotId);

        // B02 has nobody certified: its manager and the training manager are asked to train.
        Assert.Equal(1, await ToldAsync(TestUsers.TrainingManager, "Training needed for a drink", pilot.RecipeId, pilot.BranchIds[1]));
        Assert.Equal(1, await ToldAsync(TestUsers.BranchManagerB02, "Training needed for a drink", pilot.RecipeId, pilot.BranchIds[1]));
        // B01 already has its two: no training asked, and the R&D managers learn it can be opened.
        Assert.Equal(0, await ToldAsync(TestUsers.BranchManager, "Training needed for a drink", pilot.RecipeId, pilot.BranchIds[0]));
        Assert.Equal(1, await ToldAsync(TestUsers.RdManager, "Branch ready to open a drink", pilot.RecipeId, pilot.BranchIds[0]));
        Assert.Equal(0, await ToldAsync(TestUsers.RdManager, "Branch ready to open a drink", pilot.RecipeId, pilot.BranchIds[1]));
    }

    [Fact]
    public async Task A_branch_that_reaches_its_threshold_is_reported_ready_to_the_rd_managers()
    {
        var pilot = await factory.NewPilotAsync(["B01"]);
        await factory.StartPilotAsync(pilot.PilotId);
        Assert.Equal(0, await ToldAsync(TestUsers.RdManager, "Branch ready to open a drink", pilot.RecipeId, pilot.BranchIds[0]));

        // Two certificates arrive; the readiness checker recounts as it does when one is issued.
        await factory.CertifyStaffAsync(pilot, "B01");
        var adminId = await factory.UserIdAsync(TestUsers.Admin);
        await using (var db = factory.CreateDbContextAs(new FakeCurrentUser(adminId, RoleName.Admin, null)))
        {
            var readiness = new LaunchReadinessService(db, new LaunchHistory(db), TimeProvider.System);
            await readiness.RecomputeAsync(pilot.BranchIds[0], pilot.RecipeId, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        Assert.Equal("READY", (await factory.LaunchStatusAsync(pilot.RecipeId, "B01")).GetProperty("status").GetString());
        Assert.Equal(1, await ToldAsync(TestUsers.RdManager, "Branch ready to open a drink", pilot.RecipeId, pilot.BranchIds[0]));
    }

    [Fact]
    public async Task Branch_manager_is_told_when_the_drink_goes_on_sale_and_when_it_is_withdrawn()
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var pilot = await factory.NewRunningPilotAsync(["B01"]);
        Assert.Equal(1, await ToldAsync(TestUsers.BranchManager, "Drink on sale", pilot.RecipeId, pilot.BranchIds[0]));

        var live = await factory.LaunchStatusAsync(pilot.RecipeId, "B01");
        await (await manager.PostAsync($"{LaunchStatuses}/{live.Id()}/withdraw", null)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, await ToldAsync(TestUsers.BranchManager, "Drink withdrawn", pilot.RecipeId, pilot.BranchIds[0]));
        Assert.Equal(0, await ToldAsync(TestUsers.BranchManagerB02, "Drink on sale", pilot.RecipeId, pilot.BranchIds[0]));
    }

    [Fact]
    public async Task Discontinuing_a_pilot_tells_its_branches_the_drink_is_withdrawn()
    {
        var pilot = await factory.NewFinishedPilotAsync([Absolute(40)], cupsAtB01: 12, cupsAtB02: 9);

        await (await factory.DecideAsync(pilot.PilotId, "DISCONTINUE")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, await ToldAsync(TestUsers.BranchManager, "Drink withdrawn", pilot.RecipeId, pilot.BranchIds[0]));
        Assert.Equal(1, await ToldAsync(TestUsers.BranchManagerB02, "Drink withdrawn", pilot.RecipeId, pilot.BranchIds[1]));
    }

    [Fact]
    public async Task Rollout_asks_for_training_at_the_branches_that_now_prepare_the_drink()
    {
        var pilot = await factory.NewRunningPilotAsync(["B01"], [Absolute(40)]);
        await factory.TimePassesAsync(pilot);
        await factory.SellAsync(pilot.DrinkAt("B01"), Today.AddDays(-14), Today.AddDays(-1), 60);
        var b02 = await factory.BranchIdAsync("B02");

        await (await factory.DecideAsync(pilot.PilotId, "ROLLOUT")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, await ToldAsync(TestUsers.BranchManagerB02, "Training needed for a drink", pilot.RecipeId, b02));
        Assert.Equal(1, await ToldAsync(TestUsers.TrainingManager, "Training needed for a drink", pilot.RecipeId, b02));
    }

    [Fact]
    public async Task A_pilot_that_ends_is_reported_once_to_the_rd_managers()
    {
        var pilot = await factory.NewRunningPilotAsync();
        await factory.TimePassesAsync(pilot);
        var scheduler = factory.Services.GetRequiredService<PilotEndScheduler>();

        await scheduler.RunOnceAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, await ToldOfPilotAsync(TestUsers.RdManager, "Pilot ended", pilot.PilotId));
        Assert.Equal(1, await ToldOfPilotAsync("rdmanager2", "Pilot ended", pilot.PilotId));
        Assert.Equal(0, await ToldOfPilotAsync(TestUsers.BranchManager, "Pilot ended", pilot.PilotId));
    }

    [Fact]
    public async Task Missing_sales_days_of_a_running_pilot_are_reminded_once_a_day_to_the_branch_manager()
    {
        // Five days into the pilot, the first three days entered and the last two not.
        var pilot = await factory.NewRunningPilotAsync(["B01"]);
        await factory.TimePassesAsync(pilot, days: 5);
        await factory.SellAsync(pilot.DrinkAt("B01"), Today.AddDays(-5), Today.AddDays(-3), 45);
        var scheduler = factory.Services.GetRequiredService<PilotEndScheduler>();

        await scheduler.RunOnceAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None);

        var reminders = await NoticesAsync(TestUsers.BranchManager, "Sales missing",
            n => n.TryGetProperty("pilotId", out var id) && id.GetInt64() == pilot.PilotId);
        var reminder = Assert.Single(reminders);
        var tradingYesterday = TradingCalendar.DateOf(DateTimeOffset.UtcNow).AddDays(-1);
        var message = reminder.GetProperty("message").GetString()!;
        Assert.Contains(tradingYesterday.ToString("yyyy-MM-dd"), message);
        Assert.DoesNotContain(Today.AddDays(-4).ToString("yyyy-MM-dd"), message); // a day that was entered

        // Once every day is in, nothing is said.
        var other = await factory.NewRunningPilotAsync(["B02"]);
        await factory.TimePassesAsync(other, days: 3);
        await factory.SellAsync(other.DrinkAt("B02"), Today.AddDays(-3), TradingCalendar.DateOf(DateTimeOffset.UtcNow), 45);
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Empty(await NoticesAsync(TestUsers.BranchManagerB02, "Sales missing",
            n => n.TryGetProperty("pilotId", out var id) && id.GetInt64() == other.PilotId));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<int> ToldAsync(string username, string subject, long recipeId, long branchId) =>
        (await NoticesAsync(username, subject, n =>
            n.TryGetProperty("recipeId", out var recipe) && recipe.GetInt64() == recipeId
            && n.TryGetProperty("branchId", out var branch) && branch.GetInt64() == branchId)).Count;

    private async Task<int> ToldOfPilotAsync(string username, string subject, long pilotId) =>
        (await NoticesAsync(username, subject,
            n => n.TryGetProperty("pilotId", out var id) && id.GetInt64() == pilotId)).Count;

    /// <summary>The notifications a user was sent with a subject, read from their audit entries, which carry the context.</summary>
    private async Task<List<JsonElement>> NoticesAsync(string username, string subject, Func<JsonElement, bool> about)
    {
        var userId = await factory.UserIdAsync(username);
        var payloads = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "AppUser" && a.EntityId == userId && a.Action == "NOTIFY")
            .Select(a => a.PayloadJson).ToListAsync());
        return
        [
            .. payloads.Select(p => JsonSerializer.Deserialize<JsonElement>(p!))
                .Where(n => n.GetProperty("subject").GetString() == subject && about(n)),
        ];
    }
}
