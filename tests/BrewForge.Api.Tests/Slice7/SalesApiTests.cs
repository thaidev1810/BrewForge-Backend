using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice7;

/// <summary>
/// UC-22 through the API: daily sales entered by hand, attached to the
/// version the server resolves (BR-24), one record per day (BR-25), and the
/// aggregates and the branch dashboard built from them.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SalesApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- BR-24

    [Fact]
    public async Task BR_24_the_server_resolves_the_version_and_ignores_one_sent_by_the_client()
    {
        var drink = await factory.NewLiveDrinkAsync();
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var managerId = await factory.UserIdAsync(TestUsers.BranchManager);
        var day = Today.AddDays(-2);

        var created = await (await manager.PostAsJsonAsync(Sales, new
            {
                branchId = drink.BranchId, recipeId = drink.RecipeId, tradingDate = day, cupsSold = 143,
                recipeVersionId = 999999999, // a client that sends it is not listened to
            }))
            .ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(drink.VersionId, created.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(1, created.GetProperty("versionNo").GetInt32());
        Assert.Equal(drink.BranchId, created.GetProperty("branchId").GetInt64());
        Assert.Equal("B01", created.GetProperty("branchCode").GetString());
        Assert.Equal(drink.RecipeCode, created.GetProperty("recipeCode").GetString());
        Assert.Equal(day.ToString("yyyy-MM-dd"), created.GetProperty("tradingDate").GetString());
        Assert.Equal(143, created.GetProperty("cupsSold").GetInt32());
        Assert.Equal("MANUAL", created.GetProperty("source").GetString());
        Assert.Equal(managerId, created.GetProperty("recordedBy").GetInt64());

        var stored = await factory.WithDbAsync(db => db.SalesRecords.SingleAsync(s => s.Id == created.Id()));
        Assert.Equal(drink.VersionId, stored.RecipeVersionId);
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "SalesRecord" && a.EntityId == stored.Id && a.Action == "CREATE" && a.UserId == managerId)));
    }

    [Fact]
    public async Task BR_24_a_sale_for_a_branch_that_was_not_live_that_day_is_rejected()
    {
        var b01 = await factory.BranchIdAsync("B01");
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        Task<HttpResponseMessage> Post(long recipeId, DateOnly day) => manager.PostAsJsonAsync(Sales,
            new { branchId = b01, recipeId, tradingDate = day, cupsSold = 10 });

        // A released drink that was never planned for the branch.
        var (neverPlanned, _) = await factory.NewReleasedRecipeAsync();
        var refusal = await (await Post(neverPlanned, Today)).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E22", "BR-24");
        Assert.Contains("tradingDate", refusal.DetailFields());

        // Planned, but the branch has not passed the launch gate.
        var (preparing, preparingVersion) = await factory.NewReleasedRecipeAsync();
        await factory.WithDbAsync(async db =>
        {
            db.BranchLaunchStatuses.Add(BranchLaunchStatus.Plan(b01, preparing, preparingVersion, minCertifiedStaff: 2));
            await db.SaveChangesAsync();
        });
        await (await Post(preparing, Today)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-24");

        // Live, but only since five days ago.
        var drink = await factory.NewLiveDrinkAsync(liveDaysAgo: 5);
        var wentLiveOn = await factory.WithDbAsync(async db => TradingCalendar.DateOf(
            (await db.BranchLaunchStatuses.SingleAsync(l => l.Id == drink.LaunchStatusId)).LiveSince!.Value));
        await (await Post(drink.RecipeId, wentLiveOn.AddDays(-1))).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-24");
        await (await Post(drink.RecipeId, wentLiveOn)).ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal(1, await factory.WithDbAsync(db => db.SalesRecords.CountAsync(s =>
            s.RecipeId == neverPlanned || s.RecipeId == preparing || s.RecipeId == drink.RecipeId)));
    }

    [Fact]
    public async Task BR_24_a_sale_entered_late_is_attached_to_the_version_that_was_live_on_its_day()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var newVersion = await factory.ReleaseNewVersionAsync(drink.RecipeId, RecipeScenario.LighterContent());
        await factory.MoveToVersionAsync(drink, newVersion); // the branch moves today

        var yesterday = await factory.RecordSaleAsync(drink, Today.AddDays(-1), 90);
        var today = await factory.RecordSaleAsync(drink, Today, 110);

        Assert.Equal(drink.VersionId, yesterday.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(1, yesterday.GetProperty("versionNo").GetInt32());
        Assert.Equal(newVersion, today.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(2, today.GetProperty("versionNo").GetInt32());
    }

    [Fact]
    public async Task BR_24_a_withdrawn_drink_takes_no_sale_after_the_day_it_was_withdrawn()
    {
        var drink = await factory.NewLiveDrinkAsync();
        await factory.WithdrawAsync(drink, daysAgo: 4);
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        async Task<bool> OnTheListAsync(DateOnly day) =>
            (await (await manager.GetAsync($"{Sales}/drinks?branchId={drink.BranchId}&tradingDate={day:yyyy-MM-dd}"))
                .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray().Any(d => d.GetProperty("recipeId").GetInt64() == drink.RecipeId);

        // A day of the live period that was not entered yet still can be.
        var late = await factory.RecordSaleAsync(drink, Today.AddDays(-6), 35);
        Assert.Equal(drink.VersionId, late.GetProperty("recipeVersionId").GetInt64());
        Assert.True(await OnTheListAsync(Today.AddDays(-6)));

        // A day after the withdrawal cannot.
        await (await factory.PostSaleAsync(drink, Today.AddDays(-2), 35)).ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E22", "BR-24");
        Assert.False(await OnTheListAsync(Today.AddDays(-2)));
        Assert.False(await OnTheListAsync(Today));
    }

    [Fact]
    public async Task Drink_list_holds_the_drinks_live_at_the_branch_on_the_selected_day()
    {
        var live = await factory.NewLiveDrinkAsync(liveDaysAgo: 10);
        var elsewhere = await factory.NewLiveDrinkAsync(branchCode: "B02", liveDaysAgo: 10);
        var (planned, plannedVersion) = await factory.NewReleasedRecipeAsync();
        await factory.WithDbAsync(async db =>
        {
            db.BranchLaunchStatuses.Add(BranchLaunchStatus.Plan(live.BranchId, planned, plannedVersion, minCertifiedStaff: 2));
            await db.SaveChangesAsync();
        });
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        async Task<List<JsonElement>> DrinksOn(DateOnly day) =>
            [.. (await (await manager.GetAsync($"{Sales}/drinks?branchId={live.BranchId}&tradingDate={day:yyyy-MM-dd}"))
                .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()];

        var today = await DrinksOn(Today);
        var entry = Assert.Single(today, d => d.GetProperty("recipeId").GetInt64() == live.RecipeId);
        Assert.Equal(live.RecipeCode, entry.GetProperty("recipeCode").GetString());
        Assert.Equal(live.VersionId, entry.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal(1, entry.GetProperty("versionNo").GetInt32());
        Assert.DoesNotContain(today, d => d.GetProperty("recipeId").GetInt64() == planned);
        Assert.DoesNotContain(today, d => d.GetProperty("recipeId").GetInt64() == elsewhere.RecipeId);
        // The drinks the chain already sold are live everywhere, with the coverage warning (BR-36).
        var espresso = Assert.Single(today, d => d.GetProperty("recipeCode").GetString() == "R05");
        Assert.False(espresso.GetProperty("coverageMet").GetBoolean());

        // Before it went live, and on a day that has not come, the drink is not on the list.
        Assert.DoesNotContain(await DrinksOn(Today.AddDays(-20)), d => d.GetProperty("recipeId").GetInt64() == live.RecipeId);
        Assert.Empty(await DrinksOn(Today.AddDays(1)));

        // A branch manager asks for their own branch only.
        await (await manager.GetAsync($"{Sales}/drinks?branchId={elsewhere.BranchId}")).ShouldBeErrorAsync(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- BR-25

    [Fact]
    public async Task BR_25_a_second_record_for_the_same_day_is_rejected_and_the_first_is_corrected_instead()
    {
        var drink = await factory.NewLiveDrinkAsync();
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var day = Today.AddDays(-1);
        var first = await factory.RecordSaleAsync(drink, day, 143);

        var refusal = await (await factory.PostSaleAsync(drink, day, 150))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, "MSG-E23", "BR-25");
        Assert.Contains("tradingDate", refusal.DetailFields());

        var corrected = await (await manager.PutAsJsonAsync($"{Sales}/{first.Id()}", new { cupsSold = 150 }))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(150, corrected.GetProperty("cupsSold").GetInt32());
        Assert.Equal(first.Id(), corrected.Id());
        // The same figure again is not a correction.
        await (await manager.PutAsJsonAsync($"{Sales}/{first.Id()}", new { cupsSold = 150 })).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, await factory.WithDbAsync(db => db.SalesRecords.CountAsync(s => s.RecipeId == drink.RecipeId)));
        var correction = JsonSerializer.Deserialize<JsonElement>(Assert.Single(await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "SalesRecord" && a.EntityId == first.Id() && a.Action == "CORRECT_SALES")
            .Select(a => a.PayloadJson).ToListAsync()))!);
        Assert.Equal((143, 150), (correction.GetProperty("from").GetInt32(), correction.GetProperty("to").GetInt32()));
    }

    [Fact]
    public async Task BR_25_the_unique_index_answers_a_race_with_the_same_clean_409()
    {
        // Two requests that both passed the check: the second insert meets the index of the database.
        var drink = await factory.NewLiveDrinkAsync();
        var managerId = await factory.UserIdAsync(TestUsers.BranchManager);
        var day = Today.AddDays(-1);
        await factory.RecordSaleAsync(drink, day, 143);

        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var launch = await db.BranchLaunchStatuses.AsNoTracking().SingleAsync(l => l.Id == drink.LaunchStatusId);
            db.SalesRecords.Add(SalesRecord.Record(launch, [], day, 150, SalesSource.Manual, managerId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }));

        var refusal = BrewForge.Infrastructure.Persistence.PostgresErrorTranslator.Translate(exception)!;
        Assert.Equal(("BR-25", "MSG-E23", BrewForge.Domain.Common.ErrorKind.RuleViolation), (refusal.Rule, refusal.Code, refusal.Kind));
    }

    [Fact]
    public async Task Sale_is_validated()
    {
        var drink = await factory.NewLiveDrinkAsync();
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);

        var empty = await (await manager.PostAsJsonAsync(Sales, new { })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Equal(["branchId", "cupsSold", "recipeId", "tradingDate"], empty.DetailFields().Order());

        var negative = await (await factory.PostSaleAsync(drink, Today, -1)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("cupsSold", negative.DetailFields());
        var future = await (await factory.PostSaleAsync(drink, Today.AddDays(1), 10)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("tradingDate", future.DetailFields());

        await (await manager.PostAsJsonAsync(Sales,
                new { branchId = drink.BranchId, recipeId = 999999999, tradingDate = Today, cupsSold = 1 }))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PutAsJsonAsync($"{Sales}/999999999", new { cupsSold = 1 })).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- branch scope

    [Fact]
    public async Task Branch_manager_records_and_sees_the_sales_of_their_own_branch_only()
    {
        var atB01 = await factory.NewLiveDrinkAsync(branchCode: "B01");
        var atB02 = await factory.GoLiveAsync("B02", atB01.RecipeId, atB01.VersionId);
        using var managerB01 = await factory.ClientForAsync(TestUsers.BranchManager);
        using var managerB02 = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        using var rdManager = await factory.ClientForAsync(TestUsers.RdManager);
        var own = await factory.RecordSaleAsync(atB01, Today, 80);
        var other = await factory.RecordSaleAsync(atB02, Today, 60);

        // Recording for another branch is forbidden, not merely invisible.
        await (await factory.PostSaleAsync(atB02, Today.AddDays(-1), 5, username: TestUsers.BranchManager))
            .ShouldBeErrorAsync(HttpStatusCode.Forbidden, "MSG-E01");
        await (await managerB01.PutAsJsonAsync($"{Sales}/{other.Id()}", new { cupsSold = 1 })).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        var url = $"{Sales}?recipeId={atB01.RecipeId}";
        var seenByB01 = await (await managerB01.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal([own.Id()], seenByB01.GetProperty("items").EnumerateArray().Select(s => s.Id()));
        // Even when asking for the other branch by id.
        Assert.Empty((await (await managerB01.GetAsync($"{url}&branchId={atB02.BranchId}")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());
        Assert.Equal([other.Id()], (await (await managerB02.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(s => s.Id()));

        // Head office sees both, and can narrow to a branch.
        var all = await (await rdManager.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Equal([other.Id()], (await (await rdManager.GetAsync($"{url}&branchId={atB02.BranchId}")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(s => s.Id()));
    }

    [Fact]
    public async Task Sales_list_is_filtered_by_period_and_paged_latest_day_first()
    {
        var drink = await factory.NewLiveDrinkAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        for (var daysAgo = 1; daysAgo <= 5; daysAgo++) await factory.RecordSaleAsync(drink, Today.AddDays(-daysAgo), 100 + daysAgo);

        var page = await (await auditor.GetAsync(
                $"{Sales}?recipeId={drink.RecipeId}&from={Today.AddDays(-4):yyyy-MM-dd}&to={Today.AddDays(-2):yyyy-MM-dd}&page=1&size=2"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal((1, 2, 3), (page.GetProperty("page").GetInt32(), page.GetProperty("size").GetInt32(), page.GetProperty("total").GetInt32()));
        Assert.Equal([102, 103], page.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("cupsSold").GetInt32()));

        var ascending = await (await auditor.GetAsync($"{Sales}?recipeId={drink.RecipeId}&sort=cupsSold,asc&size=1")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(101, ascending.GetProperty("items")[0].GetProperty("cupsSold").GetInt32());
    }

    // ---------------------------------------------------------------- aggregation

    [Fact]
    public async Task Aggregate_groups_a_drink_per_iso_week_with_trend_and_comparison_against_a_control_drink()
    {
        var monday = MondayTwoWeeksAgo;
        var drink = await factory.NewLiveDrinkAsync(liveDaysAgo: 40);
        var control = await factory.NewLiveDrinkAsync(liveDaysAgo: 40);
        // Week 1: 40 + 60 = 100. Week 2: 75. The control: 200, then 100, and one day the drink did not trade.
        await factory.RecordSaleAsync(drink, monday, 40);
        await factory.RecordSaleAsync(drink, monday.AddDays(2), 60);
        await factory.RecordSaleAsync(drink, monday.AddDays(8), 75);
        await factory.RecordSaleAsync(control, monday, 120);
        await factory.RecordSaleAsync(control, monday.AddDays(2), 80);
        await factory.RecordSaleAsync(control, monday.AddDays(8), 100);
        await factory.RecordSaleAsync(control, monday.AddDays(9), 999);
        using var rdManager = await factory.ClientForAsync(TestUsers.RdManager);

        var result = await (await rdManager.GetAsync(
                $"{Sales}/aggregate?recipeVersionId={drink.VersionId}&groupBy=week&controlRecipeId={control.RecipeId}"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(drink.RecipeId, result.GetProperty("recipeId").GetInt64());
        Assert.Equal(drink.VersionId, result.GetProperty("recipeVersionId").GetInt64());
        Assert.Equal("WEEK", result.GetProperty("groupBy").GetString());
        Assert.Equal((175, 3), (result.GetProperty("totalCups").GetInt32(), result.GetProperty("tradingDays").GetInt32()));
        Assert.Equal(58.3m, result.GetProperty("cupsPerDay").GetDecimal());
        Assert.Equal(monday.ToString("yyyy-MM-dd"), result.GetProperty("from").GetString());
        Assert.Equal(monday.AddDays(8).ToString("yyyy-MM-dd"), result.GetProperty("to").GetString());
        // The control is counted on the days the drink traded: 120 + 80 + 100, not the 999.
        Assert.Equal(300, result.GetProperty("controlCups").GetInt32());
        Assert.Equal(58.3m, result.GetProperty("percentOfControl").GetDecimal());

        var weeks = result.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal([SalesAggregator.WeekKey(monday), SalesAggregator.WeekKey(monday.AddDays(7))], weeks.Select(w => w.GetProperty("key").GetString()));
        Assert.Equal([100, 75], weeks.Select(w => w.GetProperty("cups").GetInt32()));
        Assert.Equal(monday.ToString("yyyy-MM-dd"), weeks[0].GetProperty("from").GetString());
        Assert.Equal(monday.AddDays(6).ToString("yyyy-MM-dd"), weeks[0].GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Null, weeks[0].GetProperty("changePct").ValueKind);
        Assert.Equal(-25.0m, weeks[1].GetProperty("changePct").GetDecimal());
        Assert.Equal([200, 100], weeks.Select(w => w.GetProperty("controlCups").GetInt32()));
        Assert.Equal([50.0m, 75.0m], weeks.Select(w => w.GetProperty("percentOfControl").GetDecimal()));

        var days = await (await rdManager.GetAsync($"{Sales}/aggregate?recipeId={drink.RecipeId}&groupBy=day&from={monday:yyyy-MM-dd}&to={monday.AddDays(6):yyyy-MM-dd}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal([monday.ToString("yyyy-MM-dd"), monday.AddDays(2).ToString("yyyy-MM-dd")],
            days.GetProperty("groups").EnumerateArray().Select(d => d.GetProperty("key").GetString()));
        Assert.Equal(100, days.GetProperty("totalCups").GetInt32());
        Assert.Equal(JsonValueKind.Null, days.GetProperty("controlCups").ValueKind);
    }

    [Fact]
    public async Task Aggregate_groups_a_drink_per_branch_and_keeps_versions_apart()
    {
        var atB01 = await factory.NewLiveDrinkAsync(branchCode: "B01");
        var atB02 = await factory.GoLiveAsync("B02", atB01.RecipeId, atB01.VersionId);
        await factory.RecordSaleAsync(atB01, Today.AddDays(-3), 50);
        await factory.RecordSaleAsync(atB01, Today.AddDays(-2), 70);
        await factory.RecordSaleAsync(atB02, Today.AddDays(-2), 30);
        // B01 then moves to a second version and sells on it today.
        var secondVersion = await factory.ReleaseNewVersionAsync(atB01.RecipeId, RecipeScenario.LighterContent());
        await factory.MoveToVersionAsync(atB01, secondVersion);
        await factory.RecordSaleAsync(atB01, Today, 90);
        using var trainingManager = await factory.ClientForAsync(TestUsers.TrainingManager);

        var first = await (await trainingManager.GetAsync($"{Sales}/aggregate?recipeVersionId={atB01.VersionId}&groupBy=branch"))
            .ShouldBeAsync(HttpStatusCode.OK);
        var branches = first.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("branchCode").GetString()!);
        Assert.Equal(["B01", "B02"], branches.Keys.Order());
        Assert.Equal((120, 2, 60.0m), (branches["B01"].GetProperty("cups").GetInt32(), branches["B01"].GetProperty("tradingDays").GetInt32(),
            branches["B01"].GetProperty("cupsPerDay").GetDecimal()));
        Assert.Equal(30, branches["B02"].GetProperty("cups").GetInt32());
        Assert.Equal(atB01.BranchId, branches["B01"].GetProperty("branchId").GetInt64());
        Assert.Equal(JsonValueKind.Null, branches["B01"].GetProperty("changePct").ValueKind); // branches have no order to trend over
        Assert.Equal(150, first.GetProperty("totalCups").GetInt32());

        // The cups sold on the second version are its own.
        var second = await (await trainingManager.GetAsync($"{Sales}/aggregate?recipeVersionId={secondVersion}&groupBy=branch"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(90, second.GetProperty("totalCups").GetInt32());
        // The drink as a whole, at one branch.
        var whole = await (await trainingManager.GetAsync($"{Sales}/aggregate?recipeId={atB01.RecipeId}&branchId={atB01.BranchId}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(210, whole.GetProperty("totalCups").GetInt32());
        Assert.Equal("WEEK", whole.GetProperty("groupBy").GetString()); // the default
    }

    [Fact]
    public async Task Aggregate_parameters_are_validated()
    {
        var drink = await factory.NewLiveDrinkAsync();
        using var rdManager = await factory.ClientForAsync(TestUsers.RdManager);

        var noDrink = await (await rdManager.GetAsync($"{Sales}/aggregate")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("recipeVersionId", noDrink.DetailFields());
        var grouping = await (await rdManager.GetAsync($"{Sales}/aggregate?recipeId={drink.RecipeId}&groupBy=month")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("groupBy", grouping.DetailFields());
        await (await rdManager.GetAsync($"{Sales}/aggregate?recipeVersionId=999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await rdManager.GetAsync($"{Sales}/aggregate?recipeId={drink.RecipeId}&controlRecipeId=999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);

        // A drink that has sold nothing aggregates to nothing, not to an error.
        var nothing = await (await rdManager.GetAsync($"{Sales}/aggregate?recipeId={drink.RecipeId}&controlRecipeId={drink.RecipeId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(0, nothing.GetProperty("totalCups").GetInt32());
        Assert.Empty(nothing.GetProperty("groups").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, nothing.GetProperty("percentOfControl").ValueKind);
    }

    // ---------------------------------------------------------------- UC-18

    [Fact]
    public async Task Branch_performance_shows_sales_beside_certificate_coverage_for_the_same_branch()
    {
        // An existing drink: live, selling, and not covered by certified staff (BR-36).
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync(origin: "EXISTING");
        var b01 = await factory.BranchIdAsync("B01");
        await factory.WithDbAsync(async db =>
        {
            db.BranchLaunchStatuses.Add(BranchLaunchStatus.LiveForExistingRecipe(b01, recipeId, versionId, minCertifiedStaff: 2,
                DateTimeOffset.UtcNow.AddDays(-60)));
            await db.SaveChangesAsync();
        });
        var recipeCode = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId).Select(r => r.RecipeCode).SingleAsync());
        var drink = new LiveDrink(b01, "B01", recipeId, recipeCode, versionId, 0);
        await factory.RecordSaleAsync(drink, Today.AddDays(-1), 60);
        await factory.RecordSaleAsync(drink, Today.AddDays(-3), 45);
        await factory.RecordSaleAsync(drink, Today.AddDays(-40), 500); // outside the default four weeks
        using var rdManager = await factory.ClientForAsync(TestUsers.RdManager);
        var url = $"/api/v1/dashboards/branch-performance?recipeId={recipeId}";

        var dashboard = await (await rdManager.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(Today.ToString("yyyy-MM-dd"), dashboard.GetProperty("to").GetString());
        Assert.Equal(Today.AddDays(-27).ToString("yyyy-MM-dd"), dashboard.GetProperty("from").GetString());
        var row = Assert.Single(dashboard.GetProperty("rows").EnumerateArray());
        Assert.Equal(("B01", recipeCode), (row.GetProperty("branchCode").GetString(), row.GetProperty("recipeCode").GetString()));
        Assert.Equal(versionId, row.GetProperty("recipeVersionId").GetInt64());
        // The sales...
        Assert.Equal((105, 2, 52.5m), (row.GetProperty("cupsTotal").GetInt32(), row.GetProperty("tradingDays").GetInt32(),
            row.GetProperty("cupsPerDay").GetDecimal()));
        Assert.Equal(Today.AddDays(-1).ToString("yyyy-MM-dd"), row.GetProperty("lastTradingDate").GetString());
        // ...beside the coverage.
        Assert.Equal("LIVE", row.GetProperty("status").GetString());
        Assert.Equal((2, 0, false), (row.GetProperty("minCertifiedStaff").GetInt32(), row.GetProperty("certifiedCount").GetInt32(),
            row.GetProperty("coverageMet").GetBoolean()));

        // A wider period takes in the older day.
        var wider = await (await rdManager.GetAsync($"{url}&from={Today.AddDays(-45):yyyy-MM-dd}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(605, wider.GetProperty("rows")[0].GetProperty("cupsTotal").GetInt32());

        // A branch manager is shown their own branch, and no other.
        using var ownManager = await factory.ClientForAsync(TestUsers.BranchManager);
        using var otherManager = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        Assert.Single((await (await ownManager.GetAsync(url)).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("rows").EnumerateArray());
        Assert.Empty((await (await otherManager.GetAsync($"{url}&branchId={b01}")).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("rows").EnumerateArray());
        // Unfiltered, the other manager sees the drinks of B02 and nothing of B01.
        var theirs = await (await otherManager.GetAsync("/api/v1/dashboards/branch-performance")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.All(theirs.GetProperty("rows").EnumerateArray(), r => Assert.Equal("B02", r.GetProperty("branchCode").GetString()));
        Assert.NotEmpty(theirs.GetProperty("rows").EnumerateArray());
    }
}
