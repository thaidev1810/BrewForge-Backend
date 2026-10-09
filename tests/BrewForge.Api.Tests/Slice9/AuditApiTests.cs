using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Audit;
using BrewForge.Infrastructure.Files;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BrewForge.Api.Tests.Infrastructure.ImpactScenario;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Slice9;

/// <summary>
/// UC-20 and UC-31 through the API: the audit log and the trace of a recipe
/// version, the compliance export, and the course effectiveness dashboard.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditApiTests(BrewForgeApiFactory factory)
{
    // ---------------------------------------------------------------- the log

    [Fact]
    public async Task Audit_log_is_read_filtered_by_entity_actor_action_branch_and_period()
    {
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var drink = await factory.NewLiveDrinkAsync();
        var sale = await factory.RecordSaleAsync(drink, Today.AddDays(-1), 40);
        var managerId = await factory.UserIdAsync(TestUsers.BranchManager);
        var rdManagerId = await factory.UserIdAsync(TestUsers.RdManager);
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task<JsonElement> Log(string query) => await (await auditor.GetAsync($"{AuditLog}?{query}")).ShouldBeAsync(HttpStatusCode.OK);

        // By entity: everything that happened to the released version, latest first.
        var ofVersion = await Log($"entityType=RecipeVersion&entityId={drink.VersionId}");
        var actions = ofVersion.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal("RELEASE", actions[0]);
        Assert.Contains("SUBMIT", actions);
        Assert.Contains("VALIDATE", actions); // the release validated it again first
        var release = ofVersion.GetProperty("items")[0];
        Assert.Equal((rdManagerId, TestUsers.RdManager, "RecipeVersion", drink.VersionId), (release.GetProperty("userId").GetInt64(),
            release.GetProperty("username").GetString(), release.GetProperty("entityType").GetString(), release.GetProperty("entityId").GetInt64()));
        Assert.Equal(1, release.GetProperty("payload").GetProperty("versionNo").GetInt32()); // the payload is an object, not a string
        Assert.NotEqual(JsonValueKind.Null, release.GetProperty("createdAt").ValueKind);

        // By actor and action, and by the branch of the actor.
        var bySale = await Log($"entityType=SalesRecord&entityId={sale.Id()}&userId={managerId}&action=CREATE");
        Assert.Equal(1, bySale.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Log($"entityType=SalesRecord&entityId={sale.Id()}&branchId={drink.BranchId}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Log($"entityType=SalesRecord&entityId={sale.Id()}&branchId={await factory.BranchIdAsync("B02")}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Log($"entityType=SalesRecord&entityId={sale.Id()}&userId={rdManagerId}")).GetProperty("total").GetInt32());

        // By period, in whole days, and paged.
        Assert.Equal(1, (await Log($"entityType=SalesRecord&entityId={sale.Id()}&from={todayUtc:yyyy-MM-dd}&to={todayUtc:yyyy-MM-dd}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Log($"entityType=SalesRecord&entityId={sale.Id()}&to={todayUtc.AddDays(-1):yyyy-MM-dd}")).GetProperty("total").GetInt32());
        var page = await Log("size=3&page=2&sort=id,asc");
        Assert.Equal((2, 3, 3), (page.GetProperty("page").GetInt32(), page.GetProperty("size").GetInt32(), page.GetProperty("items").GetArrayLength()));
        Assert.True(page.GetProperty("total").GetInt32() > 6);
        await (await auditor.GetAsync($"{AuditLog}?from=2026-10-02&to=2026-10-01")).ShouldBeErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Audit_log_offers_no_way_to_change_or_remove_an_entry()
    {
        // Every route of the log only reads.
        var methods = factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.Contains("audit-log", StringComparison.OrdinalIgnoreCase))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods).Distinct().ToList();
        Assert.Equal(["GET"], methods);

        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var entry = (await (await admin.GetAsync($"{AuditLog}?size=1")).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("items")[0];
        var before = await factory.WithDbAsync(db => db.AuditLogs.CountAsync());

        // Not even for an administrator.
        foreach (var response in new[]
                 {
                     await admin.PutAsJsonAsync($"{AuditLog}/{entry.Id()}", new { action = "NOTHING_HAPPENED" }),
                     await admin.DeleteAsync($"{AuditLog}/{entry.Id()}"),
                     await admin.PostAsJsonAsync(AuditLog, new { action = "INVENTED" }),
                 })
        {
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
        Assert.Equal(before, await factory.WithDbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task Trace_follows_a_version_from_author_to_approver_to_validation_to_courses_to_certified_staff()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        var live = new LiveDrink(drink.BranchId, "B01", drink.RecipeId, drink.RecipeCode, drink.VersionId, drink.LaunchStatusId);
        await factory.RecordSaleAsync(live, Today.AddDays(-1), 64);
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        async Task<JsonElement> Trace(string entityType, long id) =>
            await (await auditor.GetAsync($"{AuditLog}/trace/{entityType}/{id}")).ShouldBeAsync(HttpStatusCode.OK);

        var trace = await Trace("RecipeVersion", drink.VersionId);

        var version = trace.GetProperty("version");
        Assert.Equal((drink.VersionId, drink.RecipeCode, 1, "RELEASED"), (version.Id(), version.GetProperty("recipeCode").GetString(),
            version.GetProperty("versionNo").GetInt32(), version.GetProperty("state").GetString()));
        Assert.Equal(64, version.GetProperty("contentHash").GetString()!.Length);
        // Author, then approver: two different people (BR-12).
        Assert.Equal((TestUsers.RdSpecialist, "RD_SPECIALIST"), (version.GetProperty("author").GetProperty("username").GetString(),
            version.GetProperty("author").GetProperty("role").GetString()));
        Assert.Equal(TestUsers.RdManager, version.GetProperty("approver").GetProperty("username").GetString());
        Assert.NotEqual(JsonValueKind.Null, version.GetProperty("releasedAt").ValueKind);
        // The validation result it was released on.
        Assert.Equal([("EQUIPMENT", true), ("INGREDIENT", true), ("ORDERING", true)], trace.GetProperty("validation").EnumerateArray()
            .Select(c => (c.GetProperty("checkType").GetString()!, c.GetProperty("passed").GetBoolean())).Order());
        // The course built from it, by whom, approved by whom.
        var course = Assert.Single(trace.GetProperty("courses").EnumerateArray());
        Assert.Equal((drink.CourseId, "PUBLISHED", TestUsers.Trainer, TestUsers.TrainingManager), (course.Id(), course.GetProperty("state").GetString(),
            course.GetProperty("createdBy").GetProperty("username").GetString(), course.GetProperty("approvedBy").GetProperty("username").GetString()));
        // The staff certified against it, and where it is sold.
        Assert.Equal([(TestUsers.Trainee, "B01", "VALID"), ("trainee2", "B01", "VALID")], trace.GetProperty("certifiedStaff").EnumerateArray()
            .Select(c => (c.GetProperty("username").GetString()!, c.GetProperty("branchCode").GetString()!, c.GetProperty("status").GetString()!)).Order());
        var branch = Assert.Single(trace.GetProperty("branches").EnumerateArray());
        Assert.Equal(("B01", "LIVE"), (branch.GetProperty("branchCode").GetString(), branch.GetProperty("status").GetString()));
        Assert.Equal(64, trace.GetProperty("cupsSold").GetInt32());
        // Everything the log holds about it, in the order it happened, ending with its release.
        var events = trace.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal("RELEASE", events[^1]);
        Assert.True(events.IndexOf("SUBMIT") >= 0 && events.IndexOf("SUBMIT") < events.Count - 1);

        // The same trace is reached from the course, from a certificate and from the recipe.
        foreach (var (entityType, id) in new[] { ("Course", drink.CourseId), ("Certificate", drink.CertificateIds[0]), ("Recipe", drink.RecipeId) })
        {
            Assert.Equal(drink.VersionId, (await Trace(entityType, id)).GetProperty("version").Id());
        }

        // Superseded, the version keeps its whole history: who was certified on it is still on record.
        await factory.ReleaseNewVersionAsync(drink.RecipeId, RecipeScenario.LighterContent());
        var later = await Trace("RecipeVersion", drink.VersionId);
        Assert.Equal("SUPERSEDED", later.GetProperty("version").GetProperty("state").GetString());
        Assert.Equal(["NEEDS_RECERT", "NEEDS_RECERT"], later.GetProperty("certifiedStaff").EnumerateArray().Select(c => c.GetProperty("status").GetString()));
        Assert.Equal("OUT_OF_DATE", later.GetProperty("courses")[0].GetProperty("state").GetString());

        Assert.Contains("entityType", (await (await auditor.GetAsync($"{AuditLog}/trace/Branch/1")).ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());
        await (await auditor.GetAsync($"{AuditLog}/trace/RecipeVersion/999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- the compliance export

    [Fact]
    public async Task Compliance_report_is_exported_as_csv_and_as_a_workbook_with_one_line_per_certificate()
    {
        var drink = await factory.NewTaughtDrinkAsync();
        using var auditor = await factory.ClientForAsync(TestUsers.Auditor);
        var auditorId = await factory.UserIdAsync(TestUsers.Auditor);
        var url = $"/api/v1/reports/compliance?courseId={drink.CourseId}";

        var csv = await auditor.GetAsync($"{url}&format=csv");

        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^brewforge-compliance-\d{8}-\d{4}\.csv$", csv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3)); // so that a spreadsheet reads the Vietnamese names
        var lines = PosFileReader.ReadCsv(bytes);
        Assert.Equal(ComplianceReportService.Columns, lines[0].Cells);
        Assert.Equal(3, lines.Count); // the heading and the two certificates of this course
        var first = lines[1].Cells;
        Assert.Equal(("B01", TestUsers.Trainee, "Bùi Anh Khoa", drink.RecipeCode, "1", "RELEASED", "VALID"),
            (first[0], first[2], first[3], first[5], first[7], first[8], first[10]));
        Assert.Equal(drink.CertificateIds[0].ToString(), first[9]);

        // The workbook holds the same table.
        var xlsx = await auditor.GetAsync($"{url}&format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        var sheet = PosFileReader.ReadXlsx(await xlsx.Content.ReadAsByteArrayAsync());
        Assert.Equal(lines.Select(l => string.Join("|", l.Cells.Take(12))), sheet.Select(r => string.Join("|", r.Cells.Take(12))));

        // Narrowed to another branch, or to a day before anything was issued, it is the heading alone.
        Assert.Single(PosFileReader.ReadCsv(await (await auditor.GetAsync($"{url}&format=csv&branchId={await factory.BranchIdAsync("B02")}")).Content.ReadAsByteArrayAsync()));
        Assert.Single(PosFileReader.ReadCsv(await (await auditor.GetAsync($"{url}&format=csv&to=2026-01-01")).Content.ReadAsByteArrayAsync()));
        // PDF is out of scope.
        Assert.Contains("format", (await (await auditor.GetAsync($"{url}&format=pdf")).ShouldBeErrorAsync(HttpStatusCode.BadRequest)).DetailFields());

        // Who exported what is on the trail.
        var exports = await factory.WithDbAsync(db => db.AuditLogs.Where(a => a.Action == "EXPORT_COMPLIANCE" && a.UserId == auditorId)
            .OrderByDescending(a => a.Id).Select(a => a.PayloadJson).ToListAsync());
        Assert.Contains(exports.Select(p => JsonSerializer.Deserialize<JsonElement>(p!)), export =>
            export.GetProperty("format").GetString() == "xlsx" && export.GetProperty("courseId").GetInt64() == drink.CourseId
            && export.GetProperty("rows").GetInt32() == 2);
    }

    [Fact]
    public void Exported_text_is_never_run_as_a_formula_and_survives_the_round_trip()
    {
        var exporter = new ReportExporter();
        var report = new TabularReport("Certification: 2026/10 [draft]", ["Name", "Note", "Count"],
        [
            ["=HYPERLINK(\"http://example.test\",\"Click\")", "+1 \"quoted\", with a comma", 7],
            ["Ngô Phương Thảo", "two\nlines", 12.5m],
            ["@cmd", null, -3],
        ]);

        var csv = Encoding.UTF8.GetString(exporter.ToCsv(report));
        // A text cell that begins like a formula is written as text.
        Assert.Contains("'=HYPERLINK(", csv);
        Assert.Contains("\"'+1 \"\"quoted\"\", with a comma\"", csv);
        Assert.Contains("'@cmd,,-3", csv); // a number stays a number
        var lines = PosFileReader.ReadCsv(exporter.ToCsv(report));
        Assert.Equal(["Name", "Note", "Count"], lines[0].Cells);
        Assert.Equal(["Ngô Phương Thảo", "two\nlines", "12.5"], lines[2].Cells);

        // In the workbook text is an inline string, which a spreadsheet never evaluates, and numbers are numbers.
        var sheet = PosFileReader.ReadXlsx(exporter.ToXlsx(report));
        Assert.Equal(["=HYPERLINK(\"http://example.test\",\"Click\")", "+1 \"quoted\", with a comma", "7"], sheet[1].Cells);
        Assert.Equal(["Ngô Phương Thảo", "two\nlines", "12.5"], sheet[2].Cells);
        Assert.Equal(["@cmd", "", "-3"], sheet[3].Cells);
    }

    // ---------------------------------------------------------------- UC-31

    [Fact]
    public async Task Course_effectiveness_shows_the_pass_rate_per_module_beside_the_cups_sold_where_staff_completed_the_course()
    {
        var setup = await factory.NewClassAsync(sessions: 5);
        await factory.OpenClassAsync(setup.ClassId); // trainee and trainee2 of B01
        var first = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var second = await factory.EnrollmentIdAsync(setup.ClassId, "trainee2");
        // One absence out of ten: 90 percent attendance, and both still at or above the minimum of 80.
        for (var i = 0; i < 5; i++)
        {
            await (await factory.RecordAttendanceAsync(setup.SessionIds[i], (first, "PRESENT"), (second, i == 0 ? "ABSENT" : "PRESENT")))
                .ShouldBeAsync(HttpStatusCode.OK);
        }
        var trainee = await factory.StudyAsync(setup, TestUsers.Trainee);
        var trainee2 = await factory.StudyAsync(setup, "trainee2");
        // The first passes at once, wrong only on the technique. The second fails on technique and SOP, and retakes.
        await (await factory.AttemptQuizAsync(trainee, "TECHNIQUE")).ShouldBeAsync(HttpStatusCode.OK);
        await (await factory.AttemptQuizAsync(trainee2, "TECHNIQUE", "SOP")).ShouldBeAsync(HttpStatusCode.OK);
        await factory.CompleteModulesAsync("trainee2", second, setup.ModuleIds);
        await (await factory.AttemptQuizAsync(trainee2)).ShouldBeAsync(HttpStatusCode.OK);
        await (await factory.EvaluatePracticalAsync(trainee)).ShouldBeAsync(HttpStatusCode.OK);
        await (await factory.EvaluatePracticalAsync(trainee2)).ShouldBeAsync(HttpStatusCode.OK);
        // The drink is sold at B01, where they work, and at B02, where nobody took the course.
        var recipeId = await factory.WithDbAsync(db => db.RecipeVersions.Where(v => v.Id == setup.RecipeVersionId).Select(v => v.RecipeId).SingleAsync());
        var atB01 = await factory.GoLiveAsync("B01", recipeId, setup.RecipeVersionId);
        var atB02 = await factory.GoLiveAsync("B02", recipeId, setup.RecipeVersionId);
        await factory.RecordSaleAsync(atB01, Today.AddDays(-2), 50);
        await factory.RecordSaleAsync(atB01, Today.AddDays(-1), 70);
        await factory.RecordSaleAsync(atB02, Today.AddDays(-1), 999);
        using var manager = await factory.ClientForAsync(TestUsers.TrainingManager);

        var course = Assert.Single((await (await manager.GetAsync($"/api/v1/dashboards/course-effectiveness?courseId={setup.CourseId}"))
            .ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray());

        Assert.Equal((setup.CourseId, setup.RecipeVersionId, 2, 2, 0), (course.GetProperty("courseId").GetInt64(),
            course.GetProperty("recipeVersionId").GetInt64(), course.GetProperty("enrollments").GetInt32(),
            course.GetProperty("passed").GetInt32(), course.GetProperty("locked").GetInt32()));
        // The quiz as a whole: one of two passed first time, and one retake was needed.
        Assert.Equal((2, 50.0m, 1, 1), (course.GetProperty("firstAttempts").GetInt32(), course.GetProperty("firstAttemptPassRatePct").GetDecimal(),
            course.GetProperty("retakeAttempts").GetInt32(), course.GetProperty("learnersWhoRetook").GetInt32()));
        Assert.Equal(90.0m, course.GetProperty("attendanceRatePct").GetDecimal());
        Assert.Equal(100.0m, course.GetProperty("onTimeCompletionRatePct").GetDecimal());
        Assert.InRange(course.GetProperty("avgDaysToCertification").GetDecimal(), 0m, 0.1m);

        // Per module: the technique, which nobody got right first time, stands out.
        var modules = course.GetProperty("modules").EnumerateArray().ToDictionary(m => m.GetProperty("moduleType").GetString()!);
        Assert.Equal(7, modules.Count);
        Assert.Equal((2, 0, 0.0m, true), (modules["TECHNIQUE"].GetProperty("firstAttempts").GetInt32(), modules["TECHNIQUE"].GetProperty("passed").GetInt32(),
            modules["TECHNIQUE"].GetProperty("passRatePct").GetDecimal(), modules["TECHNIQUE"].GetProperty("highlighted").GetBoolean()));
        Assert.Equal(50.0m, modules["SOP"].GetProperty("passRatePct").GetDecimal());
        Assert.All(modules.Where(m => m.Key is not ("TECHNIQUE" or "SOP")).Select(m => m.Value), module =>
            Assert.Equal((100.0m, false), (module.GetProperty("passRatePct").GetDecimal(), module.GetProperty("highlighted").GetBoolean())));

        // Beside it: the cups of the bound version at the branch whose staff completed the course, and only there.
        Assert.Equal(120, course.GetProperty("cupsSold").GetInt32());
        var branch = Assert.Single(course.GetProperty("salesByBranch").EnumerateArray());
        Assert.Equal(("B01", 2, 120, 2, 60.0m), (branch.GetProperty("branchCode").GetString(), branch.GetProperty("certifiedStaff").GetInt32(),
            branch.GetProperty("cups").GetInt32(), branch.GetProperty("tradingDays").GetInt32(), branch.GetProperty("cupsPerDay").GetDecimal()));

        await (await manager.GetAsync("/api/v1/dashboards/course-effectiveness?courseId=999999999")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }
}
