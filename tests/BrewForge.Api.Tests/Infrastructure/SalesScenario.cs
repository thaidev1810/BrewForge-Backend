using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using BrewForge.Application.Launch;
using BrewForge.Domain.Audit;
using BrewForge.Domain.Launch;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>A drink that is on sale at a branch.</summary>
public sealed record LiveDrink(long BranchId, string BranchCode, long RecipeId, string RecipeCode, long VersionId,
    long LaunchStatusId);

/// <summary>Puts drinks on sale at branches and records their sales, the way a branch manager would.</summary>
public static class SalesScenario
{
    public const string Sales = "/api/v1/sales";

    /// <summary>Today as a trading day: the calendar day in Vietnam.</summary>
    public static DateOnly Today => TradingCalendar.DateOf(DateTimeOffset.UtcNow);

    /// <summary>The Monday two weeks before this week's, so that two whole ISO weeks of it lie in the past.</summary>
    public static DateOnly MondayTwoWeeksAgo => Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7) - 14);

    /// <summary>A newly released recipe that went live at the branch some days ago, through the launch gate.</summary>
    public static async Task<LiveDrink> NewLiveDrinkAsync(this BrewForgeApiFactory factory, string branchCode = "B01",
        int liveDaysAgo = 30)
    {
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync();
        return await factory.GoLiveAsync(branchCode, recipeId, versionId, liveDaysAgo);
    }

    /// <summary>
    /// PREPARING, READY, LIVE: what the launch gate does to a branch. The
    /// gate's own endpoint is exercised by its own tests; here it is only
    /// the state a sales test starts from.
    /// </summary>
    public static async Task<LiveDrink> GoLiveAsync(this BrewForgeApiFactory factory, string branchCode, long recipeId,
        long versionId, int liveDaysAgo = 30)
    {
        var branchId = await factory.BranchIdAsync(branchCode);
        var recipeCode = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId)
            .Select(r => r.RecipeCode).SingleAsync());
        var launchId = await factory.WithDbAsync(async db =>
        {
            var launch = BranchLaunchStatus.Plan(branchId, recipeId, versionId, minCertifiedStaff: 2);
            launch.RecomputeCoverage(2);
            launch.GoLive(DateTimeOffset.UtcNow.AddDays(-liveDaysAgo));
            db.BranchLaunchStatuses.Add(launch);
            await db.SaveChangesAsync();
            return launch.Id;
        });
        return new LiveDrink(branchId, branchCode, recipeId, recipeCode, versionId, launchId);
    }

    /// <summary>
    /// Moves the day a drink went live back in time, at every branch or at
    /// one: the arrangement for "it has been on sale for a while", which no
    /// endpoint can make because going live always happens now.
    /// </summary>
    public static Task BackdateLaunchAsync(this BrewForgeApiFactory factory, long recipeId, int days, long? branchId = null) =>
        factory.WithDbAsync(async db =>
        {
            var only = branchId ?? -1; // -1 names no branch: every branch
            await db.Database.ExecuteSqlAsync(
                $"UPDATE branch_launch_status SET live_since = live_since - make_interval(days => {days}) WHERE recipe_id = {recipeId} AND ({only} = -1 OR branch_id = {only})");
            await db.Database.ExecuteSqlAsync(
                $"UPDATE pilot_branch SET went_live_at = went_live_at - make_interval(days => {days}) WHERE ({only} = -1 OR branch_id = {only}) AND pilot_program_id IN (SELECT p.id FROM pilot_program p JOIN recipe_version v ON v.id = p.recipe_version_id WHERE v.recipe_id = {recipeId})");
        });

    /// <summary>The branch moves to another version of the drink, now, and the move is recorded as the application records it.</summary>
    public static Task MoveToVersionAsync(this BrewForgeApiFactory factory, LiveDrink drink, long newVersionId) =>
        factory.WithDbAsync(async db =>
        {
            var launch = await db.BranchLaunchStatuses.SingleAsync(l => l.Id == drink.LaunchStatusId);
            var from = launch.RecipeVersionId;
            launch.MoveToVersion(newVersionId, certifiedCountOnThatVersion: 0);
            new LaunchHistory(db).RecordVersionMove(launch, from);
            await db.SaveChangesAsync();
        });

    /// <summary>
    /// The drink is withdrawn at the branch. Today, it is recorded as the
    /// application records it; for a withdrawal that "happened" some days
    /// ago the entry is written with that date, which only a test can do.
    /// </summary>
    public static Task WithdrawAsync(this BrewForgeApiFactory factory, LiveDrink drink, int daysAgo = 0) =>
        factory.WithDbAsync(async db =>
        {
            var launch = await db.BranchLaunchStatuses.SingleAsync(l => l.Id == drink.LaunchStatusId);
            launch.Withdraw();
            if (daysAgo == 0) new LaunchHistory(db).RecordWithdrawal(launch);
            else
            {
                db.AuditLogs.Add(new AuditLog(null, "BranchLaunchStatus", launch.Id, "WITHDRAW", null,
                    DateTimeOffset.UtcNow.AddDays(-daysAgo)));
            }
            await db.SaveChangesAsync();
        });

    public static string ManagerOf(string branchCode) =>
        branchCode == "B01" ? TestUsers.BranchManager : TestUsers.BranchManagerB02;

    /// <summary>The manager of the drink's branch enters a day by hand.</summary>
    public static async Task<HttpResponseMessage> PostSaleAsync(this BrewForgeApiFactory factory, LiveDrink drink,
        DateOnly tradingDate, int cupsSold, string? username = null)
    {
        using var manager = await factory.ClientForAsync(username ?? ManagerOf(drink.BranchCode));
        return await manager.PostAsJsonAsync(Sales,
            new { branchId = drink.BranchId, recipeId = drink.RecipeId, tradingDate, cupsSold });
    }

    public static async Task<JsonElement> RecordSaleAsync(this BrewForgeApiFactory factory, LiveDrink drink,
        DateOnly tradingDate, int cupsSold) =>
        await (await factory.PostSaleAsync(drink, tradingDate, cupsSold)).ShouldBeAsync(HttpStatusCode.Created);

    // ---------------------------------------------------------------- POS files

    public const string Header = "branch_code,drink_code,trading_date,quantity";

    public static string Line(LiveDrink drink, DateOnly tradingDate, object quantity) =>
        $"{drink.BranchCode},{drink.RecipeCode},{tradingDate:yyyy-MM-dd},{quantity}";

    /// <summary>A CSV file in the supported layout holding the given lines.</summary>
    public static byte[] Csv(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join("\n", lines.Prepend(Header)) + "\n");

    public static async Task<HttpResponseMessage> ImportAsync(this BrewForgeApiFactory factory, byte[] content,
        string fileName = "pos.csv", string username = TestUsers.BranchManager, string? idempotencyKey = null)
    {
        using var manager = await factory.ClientForAsync(username);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                : "text/csv");
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Sales}/import") { Content = form };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await manager.SendAsync(request);
    }

    public static async Task<JsonElement> ImportedAsync(this BrewForgeApiFactory factory, params string[] lines) =>
        await (await factory.ImportAsync(Csv(lines))).ShouldBeAsync(HttpStatusCode.OK);

    public static IReadOnlyList<(int Row, string Code, string Message)> Errors(this JsonElement importResult) =>
    [
        .. importResult.GetProperty("errors").EnumerateArray().Select(error => (error.GetProperty("row").GetInt32(),
            error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!)),
    ];

    /// <summary>
    /// A workbook with one sheet, as a spreadsheet program writes it: text in
    /// the shared-string table (or inline), numbers as numbers, and a date as
    /// the day serial of a date cell.
    /// </summary>
    public static byte[] Xlsx(IEnumerable<object?[]> rows, bool inlineStrings = false)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            sheet.Append($"""<row r="{rowNumber}">""");
            for (var column = 0; column < row.Length; column++)
            {
                var reference = $"{(char)('A' + column)}{rowNumber}";
                switch (row[column])
                {
                    case null:
                        break; // a spreadsheet simply leaves an empty cell out
                    case string text when inlineStrings:
                        sheet.Append($"""<c r="{reference}" t="inlineStr"><is><t>{SecurityElement.Escape(text)}</t></is></c>""");
                        break;
                    case string text:
                        if (!shared.Contains(text)) shared.Add(text);
                        sheet.Append($"""<c r="{reference}" t="s"><v>{shared.IndexOf(text)}</v></c>""");
                        break;
                    case DateOnly date:
                        sheet.Append($"""<c r="{reference}" s="1"><v>{(int)date.ToDateTime(TimeOnly.MinValue).ToOADate()}</v></c>""");
                        break;
                    case var number:
                        sheet.Append($"""<c r="{reference}"><v>{Convert.ToString(number, System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                }
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        var strings = new StringBuilder(
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{shared.Count}" uniqueCount="{shared.Count}">""");
        foreach (var text in shared) strings.Append($"<si><t>{SecurityElement.Escape(text)}</t></si>");
        strings.Append("</sst>");

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string path, string xml)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            Part("[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>""");
            Part("_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="POS" sheetId="1" r:id="rId7"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId8" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>""");
            Part("xl/worksheets/sheet1.xml", sheet.ToString());
            if (!inlineStrings) Part("xl/sharedStrings.xml", strings.ToString());
        }
        return buffer.ToArray();
    }
}
