using System.Net;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice7;

/// <summary>
/// UC-23 through the API: a POS export in the one fixed layout, judged line
/// by line. Accepted lines are applied even when others are rejected, every
/// rejection names its row and its reason, and a day imported again replaces
/// the record of that day (BR-25).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PosImportApiTests(BrewForgeApiFactory factory)
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Samples", "pos-import-sample.csv");

    // ---------------------------------------------------------------- the sample file

    [Fact]
    public async Task Sample_file_applies_its_20_good_lines_and_reports_its_4_bad_ones_each_with_its_own_reason()
    {
        var b01 = await factory.BranchIdAsync("B01");
        var seeded = await factory.WithDbAsync(db => db.Recipes.Where(r => new[] { "R05", "R07", "R08" }.Contains(r.RecipeCode))
            .ToDictionaryAsync(r => r.RecipeCode, r => r.Id));

        var result = await (await factory.ImportAsync(await File.ReadAllBytesAsync(SamplePath), "pos-import-sample.csv"))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("pos-import-sample.csv", result.GetProperty("fileName").GetString());
        Assert.Equal((20, 4, 0), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32(),
            result.GetProperty("replaced").GetInt32()));
        Assert.Equal(
        [
            (6, "MSG-E21", "Unknown drink code TRA-99"),
            (12, "MSG-E22", "Branch B01 was not live on 2025-12-31 for R07"),
            (18, "MSG-E23", "Duplicate day for B01 / R05 / 2026-09-01"),
            (22, "IMPORT_INVALID_QUANTITY", "Quantity 'abc' is not a whole number"),
        ], result.Errors());

        // The accepted lines were applied although others failed.
        var stored = await factory.WithDbAsync(db => db.SalesRecords.AsNoTracking()
            .Where(s => s.BranchId == b01 && seeded.Values.Contains(s.RecipeId)
                        && s.TradingDate >= new DateOnly(2026, 9, 1) && s.TradingDate <= new DateOnly(2026, 9, 7))
            .ToListAsync());
        Assert.Equal(20, stored.Count);
        Assert.All(stored, record => Assert.Equal(SalesSource.PosImport, record.Source));
        Assert.All(stored, record => Assert.NotNull(record.RecipeVersionId)); // resolved by the server (BR-24)
        // Of the duplicated day the first line stands; the line with the bad quantity left no record.
        Assert.Equal(112, stored.Single(s => s.RecipeId == seeded["R05"] && s.TradingDate == new DateOnly(2026, 9, 1)).CupsSold);
        Assert.DoesNotContain(stored, s => s.RecipeId == seeded["R08"] && s.TradingDate == new DateOnly(2026, 9, 6));
        Assert.Equal(2406, stored.Sum(s => s.CupsSold));

        // The result can be fetched again by the branch that imported, and by no other.
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        using var otherManager = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        var jobId = result.GetProperty("jobId").GetString();
        var fetched = await (await manager.GetAsync($"{Sales}/import/{jobId}")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(result.GetRawText(), fetched.GetRawText());
        await (await otherManager.GetAsync($"{Sales}/import/{jobId}")).ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.GetAsync($"{Sales}/import/not-a-job")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- BR-25

    [Fact]
    public async Task BR_25_a_day_imported_again_replaces_its_record_and_the_replacement_is_in_the_audit_log()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var (monday, tuesday, wednesday) = (Today.AddDays(-5), Today.AddDays(-4), Today.AddDays(-3));
        await factory.RecordSaleAsync(drink, wednesday, 70); // entered by hand before any import
        var first = await factory.ImportedAsync(Line(drink, monday, 100), Line(drink, tuesday, 120));
        Assert.Equal((2, 0, 0), (first.GetProperty("accepted").GetInt32(), first.GetProperty("rejected").GetInt32(),
            first.GetProperty("replaced").GetInt32()));

        // The corrected export: Monday unchanged, Tuesday corrected, Wednesday now from the POS.
        var second = await factory.ImportedAsync(Line(drink, monday, 100), Line(drink, tuesday, 135), Line(drink, wednesday, 75));

        Assert.Equal((3, 0, 2), (second.GetProperty("accepted").GetInt32(), second.GetProperty("rejected").GetInt32(),
            second.GetProperty("replaced").GetInt32()));
        var stored = await factory.WithDbAsync(db => db.SalesRecords.AsNoTracking().Where(s => s.RecipeId == drink.RecipeId)
            .OrderBy(s => s.TradingDate).ToListAsync());
        Assert.Equal([(monday, 100), (tuesday, 135), (wednesday, 75)], stored.Select(s => (s.TradingDate, s.CupsSold)));
        Assert.All(stored, record => Assert.Equal(SalesSource.PosImport, record.Source));

        var storedIds = stored.Select(s => s.Id).ToList();
        var replacements = (await factory.WithDbAsync(db => db.AuditLogs
                .Where(a => a.EntityType == "SalesRecord" && a.Action == "REPLACE_SALES" && storedIds.Contains(a.EntityId))
                .OrderBy(a => a.Id).Select(a => new { a.EntityId, a.PayloadJson }).ToListAsync()))
            .Select(a => (a.EntityId, Payload: JsonSerializer.Deserialize<JsonElement>(a.PayloadJson!))).ToList();
        Assert.Equal([stored[1].Id, stored[2].Id], replacements.Select(r => r.EntityId));
        var corrected = replacements[0].Payload;
        Assert.Equal(second.GetProperty("jobId").GetString(), corrected.GetProperty("jobId").GetString());
        Assert.Equal((120, "POS_IMPORT"), (corrected.GetProperty("from").GetProperty("cupsSold").GetInt32(), corrected.GetProperty("from").GetProperty("source").GetString()));
        Assert.Equal(135, corrected.GetProperty("to").GetProperty("cupsSold").GetInt32());
        var fromManual = replacements[1].Payload;
        Assert.Equal((70, "MANUAL"), (fromManual.GetProperty("from").GetProperty("cupsSold").GetInt32(), fromManual.GetProperty("from").GetProperty("source").GetString()));
        Assert.Equal((75, "POS_IMPORT"), (fromManual.GetProperty("to").GetProperty("cupsSold").GetInt32(), fromManual.GetProperty("to").GetProperty("source").GetString()));
    }

    [Fact]
    public async Task BR_25_a_day_twice_in_one_file_is_a_row_error_and_not_a_500()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var day = Today.AddDays(-2);

        var result = await factory.ImportedAsync(Line(drink, day, 100), Line(drink, Today.AddDays(-1), 90), Line(drink, day, 110));

        Assert.Equal((2, 1), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32()));
        Assert.Equal([(4, "MSG-E23", $"Duplicate day for B01 / {drink.RecipeCode} / {day:yyyy-MM-dd}")], result.Errors());
        Assert.Equal(100, await factory.WithDbAsync(db => db.SalesRecords
            .Where(s => s.RecipeId == drink.RecipeId && s.TradingDate == day).Select(s => s.CupsSold).SingleAsync()));
    }

    // ---------------------------------------------------------------- BR-24 and the other reasons

    [Fact]
    public async Task Every_kind_of_bad_line_is_rejected_with_its_own_reason_and_the_good_line_is_still_applied()
    {
        var drink = await factory.NewLiveDrinkAsync(liveDaysAgo: 5);
        var atB02 = await factory.NewLiveDrinkAsync(branchCode: "B02");
        var (notLaunched, _) = await factory.NewReleasedRecipeAsync();
        var notLaunchedCode = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == notLaunched).Select(r => r.RecipeCode).SingleAsync());
        var day = Today.AddDays(-1);

        var result = await factory.ImportedAsync(
            Line(drink, day, 50),                                              // row 2: good
            $"B01,NO-SUCH-DRINK,{day:yyyy-MM-dd},5",                           // row 3
            Line(drink, Today.AddDays(-20), 5),                                // row 4: before the branch went live
            $"B01,{notLaunchedCode},{day:yyyy-MM-dd},5",                       // row 5: never launched at B01
            Line(drink, day, "many"),                                          // row 6
            Line(drink, day, -4),                                              // row 7
            $"B01,{drink.RecipeCode},06/10/2026,5",                            // row 8
            Line(drink, Today.AddDays(1), 5),                                  // row 9
            $"B77,{drink.RecipeCode},{day:yyyy-MM-dd},5",                      // row 10
            Line(atB02, day, 5),                                               // row 11: another manager's branch
            $"B01,,{day:yyyy-MM-dd},5");                                       // row 12

        Assert.Equal((1, 10), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32()));
        Assert.Equal(
        [
            (3, "MSG-E21", "Unknown drink code NO-SUCH-DRINK"),
            (4, "MSG-E22", $"Branch B01 was not live on {Today.AddDays(-20):yyyy-MM-dd} for {drink.RecipeCode}"),
            (5, "MSG-E22", $"Branch B01 was not live on {day:yyyy-MM-dd} for {notLaunchedCode}"),
            (6, "IMPORT_INVALID_QUANTITY", "Quantity 'many' is not a whole number"),
            (7, "IMPORT_INVALID_QUANTITY", "Quantity -4 is negative"),
            (8, "IMPORT_INVALID_DATE", "Trading date '06/10/2026' is not a date in the form YYYY-MM-DD"),
            (9, "IMPORT_INVALID_DATE", $"Trading date {Today.AddDays(1):yyyy-MM-dd} is in the future"),
            (10, "IMPORT_UNKNOWN_BRANCH", "Unknown branch code B77"),
            (11, "IMPORT_BRANCH_NOT_PERMITTED", "Branch B02 is not your branch"),
            (12, "IMPORT_MISSING_VALUE", "Missing drink_code"),
        ], result.Errors());

        var record = Assert.Single(await factory.WithDbAsync(db => db.SalesRecords.AsNoTracking()
            .Where(s => s.RecipeId == drink.RecipeId || s.RecipeId == atB02.RecipeId || s.RecipeId == notLaunched).ToListAsync()));
        Assert.Equal((day, 50, drink.VersionId), (record.TradingDate, record.CupsSold, record.RecipeVersionId!.Value));
    }

    [Fact]
    public async Task BR_24_an_imported_day_is_attached_to_the_version_that_was_live_on_it()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var newVersion = await factory.ReleaseNewVersionAsync(drink.RecipeId, RecipeScenario.LighterContent());
        await factory.MoveToVersionAsync(drink, newVersion); // the branch moves today

        // Last week's export, imported after the move.
        await factory.ImportedAsync(Line(drink, Today.AddDays(-3), 80), Line(drink, Today, 95));

        var versions = await factory.WithDbAsync(db => db.SalesRecords.Where(s => s.RecipeId == drink.RecipeId)
            .OrderBy(s => s.TradingDate).Select(s => s.RecipeVersionId).ToListAsync());
        Assert.Equal([drink.VersionId, newVersion], versions);
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public async Task Workbook_is_imported_like_a_csv_with_its_date_cells_and_number_cells()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var (first, second) = (Today.AddDays(-2), Today.AddDays(-1));
        var workbook = Xlsx(
        [
            ["branch_code", "drink_code", "trading_date", "quantity"],
            ["B01", drink.RecipeCode, first, 143],                       // a date cell, a number cell
            ["B01", drink.RecipeCode, second.ToString("yyyy-MM-dd"), 151.0m], // a text date, a number written with a decimal
            ["B01", "TRA-99", first, 7],
            ["B01", drink.RecipeCode, first.AddDays(-1), "a dozen"],
        ]);

        var result = await (await factory.ImportAsync(workbook, "pos-2026-10.xlsx")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("pos-2026-10.xlsx", result.GetProperty("fileName").GetString());
        Assert.Equal((2, 2), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32()));
        Assert.Equal([(4, "MSG-E21"), (5, "IMPORT_INVALID_QUANTITY")], result.Errors().Select(e => (e.Row, e.Code)));
        Assert.Equal([(first, 143), (second, 151)], await factory.WithDbAsync(async db =>
            (await db.SalesRecords.Where(s => s.RecipeId == drink.RecipeId).OrderBy(s => s.TradingDate).ToListAsync())
            .Select(s => (s.TradingDate, s.CupsSold)).ToList()));
    }

    [Fact]
    public async Task Csv_written_with_semicolons_quotes_and_a_byte_order_mark_is_read()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var day = Today.AddDays(-1);
        var file = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            $"branch_code;drink_code;trading_date;quantity\r\n\"B01\";\"{drink.RecipeCode}\";{day:yyyy-MM-dd};\"88\"\r\n\r\n")).ToArray();

        var result = await (await factory.ImportAsync(file)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal((1, 0), (result.GetProperty("accepted").GetInt32(), result.GetProperty("rejected").GetInt32()));
        Assert.Equal(88, await factory.WithDbAsync(db => db.SalesRecords.Where(s => s.RecipeId == drink.RecipeId)
            .Select(s => s.CupsSold).SingleAsync()));
    }

    [Fact]
    public async Task File_that_is_not_in_the_layout_is_refused_as_a_whole()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var line = Line(drink, Today, 5);
        async Task<JsonElement> Refused(byte[] content, string fileName = "pos.csv") =>
            await (await factory.ImportAsync(content, fileName)).ShouldBeErrorAsync(HttpStatusCode.BadRequest, "IMPORT_LAYOUT");

        // There is no column mapping: other names, another order or no header are not guessed at.
        Assert.Contains("file", (await Refused(Encoding.UTF8.GetBytes($"branch,drink,date,qty\n{line}\n"))).DetailFields());
        await Refused(Encoding.UTF8.GetBytes($"drink_code,branch_code,trading_date,quantity\n{line}\n"));
        await Refused(Encoding.UTF8.GetBytes($"{line}\n"));
        await Refused([]);
        await Refused([0x00, 0x01, 0x02, 0xFF, 0x00], "pos.bin");
        await Refused(Encoding.UTF8.GetBytes("this is not a workbook"), "pos.xlsx");
        await Refused([(byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0, 0, 0], "broken.xlsx");

        Assert.Equal(0, await factory.WithDbAsync(db => db.SalesRecords.CountAsync(s => s.RecipeId == drink.RecipeId)));
    }

    [Fact]
    public async Task Request_without_a_file_is_400()
    {
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        using var form = new MultipartFormDataContent { { new StringContent("nothing"), "note" } };

        var envelope = await (await manager.PostAsync($"{Sales}/import", form)).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("file", envelope.DetailFields());
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Repeating_an_idempotency_key_returns_the_original_result_and_imports_nothing_twice()
    {
        var drink = await factory.NewLiveDrinkAsync();
        var day = Today.AddDays(-1);
        var key = Guid.NewGuid().ToString();

        var original = await (await factory.ImportAsync(Csv(Line(drink, day, 100)), idempotencyKey: key)).ShouldBeAsync(HttpStatusCode.OK);
        // The retry, and a different file sent under the same key by mistake: neither is acted on.
        var retry = await (await factory.ImportAsync(Csv(Line(drink, day, 100)), idempotencyKey: key)).ShouldBeAsync(HttpStatusCode.OK);
        var other = await (await factory.ImportAsync(Csv(Line(drink, day, 999)), idempotencyKey: key)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.False(original.GetProperty("replayed").GetBoolean());
        Assert.All(new[] { retry, other }, replay =>
        {
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(original.GetProperty("jobId").GetString(), replay.GetProperty("jobId").GetString());
            Assert.Equal(original.GetProperty("importedAt").GetString(), replay.GetProperty("importedAt").GetString());
            Assert.Equal(1, replay.GetProperty("accepted").GetInt32());
        });
        Assert.Equal(100, await factory.WithDbAsync(db => db.SalesRecords.Where(s => s.RecipeId == drink.RecipeId)
            .Select(s => s.CupsSold).SingleAsync()));
        Assert.Equal(1, await factory.WithDbAsync(async db => (await db.AuditLogs
            .Where(a => a.EntityType == "SalesImport" && a.Action == "POS_IMPORT").Select(a => a.PayloadJson).ToListAsync())
            .Count(payload => payload!.Contains(key))));

        // The key belongs to the user who used it: the same key from another manager is a new import.
        var atB02 = await factory.NewLiveDrinkAsync(branchCode: "B02");
        var theirs = await (await factory.ImportAsync(Csv(Line(atB02, day, 40)), username: TestUsers.BranchManagerB02, idempotencyKey: key))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.False(theirs.GetProperty("replayed").GetBoolean());
        Assert.NotEqual(original.GetProperty("jobId").GetString(), theirs.GetProperty("jobId").GetString());
    }
}
