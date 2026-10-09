using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.SalesScenario;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>A pilot of a newly released drink, with what a test needs to refer to it.</summary>
public sealed record PilotSetup(long PilotId, long RecipeId, string RecipeCode, long VersionId,
    IReadOnlyList<long> BranchIds, IReadOnlyList<string> BranchCodes, long CourseId)
{
    public string Url => $"{PilotScenario.Pilots}/{PilotId}";

    public LiveDrink DrinkAt(string branchCode) =>
        new(BranchIds[BranchCodes.ToList().IndexOf(branchCode)], branchCode, RecipeId, RecipeCode, VersionId, 0);
}

/// <summary>Sets up and runs pilots through the API, the way an R&amp;D manager would.</summary>
public static class PilotScenario
{
    public const string Pilots = "/api/v1/pilots";
    public const string LaunchStatuses = "/api/v1/branch-launch-status";

    /// <summary>The staff of each seeded branch that can be certified.</summary>
    private static readonly Dictionary<string, string[]> Staff = new()
    {
        ["B01"] = [TestUsers.Trainee, "trainee2"],
        ["B02"] = ["trainee3", "trainee4"],
    };

    public static object Absolute(decimal cupsPerDayPerBranch) => new { type = "ABSOLUTE", cupsPerDayPerBranch };

    public static object Relative(long controlRecipeId, decimal minPercentOfControl) =>
        new { type = "RELATIVE", controlRecipeId, minPercentOfControl };

    public static object Retention(decimal maxDropSecondHalfPct) => new { type = "RETENTION", maxDropSecondHalfPct };

    /// <summary>The body of a pilot of fourteen days starting today, unless said otherwise.</summary>
    public static object Body(long recipeVersionId, IEnumerable<long> branchIds, object[]? criteria = null,
        DateOnly? startDate = null, DateOnly? endDate = null, int minCertifiedStaff = 2, string? name = null) => new
    {
        recipeVersionId,
        name = name ?? $"Pilot {Guid.NewGuid():N}"[..18],
        startDate = startDate ?? Today,
        endDate = endDate ?? (startDate ?? Today).AddDays(13),
        minCertifiedStaff,
        branchIds,
        criteria = criteria ?? [Absolute(40)],
    };

    /// <summary>A DRAFT pilot of a newly released drink at the given branches.</summary>
    public static async Task<PilotSetup> NewPilotAsync(this BrewForgeApiFactory factory, string[]? branches = null,
        object[]? criteria = null, int minCertifiedStaff = 2, bool withCourse = false)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        var (recipeId, versionId) = await factory.NewReleasedRecipeAsync();
        var codes = branches ?? ["B01"];
        var branchIds = new List<long>();
        foreach (var code in codes) branchIds.Add(await factory.BranchIdAsync(code));

        var created = await (await manager.PostAsJsonAsync(Pilots,
                Body(versionId, branchIds, criteria, minCertifiedStaff: minCertifiedStaff)))
            .ShouldBeAsync(HttpStatusCode.Created);
        var recipeCode = await factory.WithDbAsync(db => db.Recipes.Where(r => r.Id == recipeId)
            .Select(r => r.RecipeCode).SingleAsync());
        var courseId = withCourse ? (await factory.NewPublishedCourseAsync(versionId)).Id() : 0;
        return new PilotSetup(created.Id(), recipeId, recipeCode, versionId, branchIds, codes, courseId);
    }

    public static async Task<JsonElement> GetPilotAsync(this BrewForgeApiFactory factory, long pilotId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await (await manager.GetAsync($"{Pilots}/{pilotId}")).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<JsonElement> StartPilotAsync(this BrewForgeApiFactory factory, long pilotId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await (await manager.PostAsync($"{Pilots}/{pilotId}/start", null)).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<HttpResponseMessage> GoLiveAsync(this BrewForgeApiFactory factory, long pilotId, long branchId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await manager.PostAsync($"{Pilots}/{pilotId}/branches/{branchId}/go-live", null);
    }

    public static async Task<JsonElement> EvaluationAsync(this BrewForgeApiFactory factory, long pilotId)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await (await manager.GetAsync($"{Pilots}/{pilotId}/evaluation")).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<HttpResponseMessage> DecideAsync(this BrewForgeApiFactory factory, long pilotId,
        string decision, string? note = null)
    {
        using var manager = await factory.ClientForAsync(TestUsers.RdManager);
        return await manager.PostAsJsonAsync($"{Pilots}/{pilotId}/decision", new { decision, note });
    }

    public static async Task<List<JsonElement>> LaunchStatusesAsync(this BrewForgeApiFactory factory, long recipeId,
        string username = TestUsers.RdManager)
    {
        using var client = await factory.ClientForAsync(username);
        return [.. (await (await client.GetAsync($"{LaunchStatuses}?recipeId={recipeId}")).ShouldBeAsync(HttpStatusCode.OK)).EnumerateArray()];
    }

    public static async Task<JsonElement> LaunchStatusAsync(this BrewForgeApiFactory factory, long recipeId, string branchCode) =>
        (await factory.LaunchStatusesAsync(recipeId)).Single(row => row.GetProperty("branchCode").GetString() == branchCode);

    /// <summary>
    /// Arranges certificates that "were issued earlier" for the first staff
    /// of a branch on the pilot's version, as <see cref="TrainingScenario.CertifyAsync"/>
    /// does. Nothing recounts on such an arrangement; the gate counts for itself.
    /// </summary>
    public static async Task CertifyStaffAsync(this BrewForgeApiFactory factory, PilotSetup pilot, string branchCode,
        int count = 2)
    {
        var courseId = pilot.CourseId != 0
            ? pilot.CourseId
            : await factory.WithDbAsync(db => db.Courses.Where(c => c.RecipeVersionId == pilot.VersionId)
                .Select(c => (long?)c.Id).SingleOrDefaultAsync())
              ?? (await factory.NewPublishedCourseAsync(pilot.VersionId)).Id();
        foreach (var username in Staff[branchCode].Take(count))
        {
            var userId = await factory.UserIdAsync(username);
            var already = await factory.WithDbAsync(db => db.Certificates.AnyAsync(c =>
                c.UserId == userId && c.RecipeVersionId == pilot.VersionId));
            if (!already) await factory.CertifyAsync(userId, courseId, pilot.VersionId);
        }
    }

    /// <summary>
    /// A RUNNING pilot whose branches have all passed the gate and gone live
    /// today, the first day of its fourteen.
    /// </summary>
    public static async Task<PilotSetup> NewRunningPilotAsync(this BrewForgeApiFactory factory, string[]? branches = null,
        object[]? criteria = null)
    {
        var pilot = await factory.NewPilotAsync(branches, criteria);
        foreach (var code in pilot.BranchCodes) await factory.CertifyStaffAsync(pilot, code);
        await factory.StartPilotAsync(pilot.PilotId);
        foreach (var branchId in pilot.BranchIds)
        {
            await (await factory.GoLiveAsync(pilot.PilotId, branchId)).ShouldBeAsync(HttpStatusCode.OK);
        }
        return pilot;
    }

    /// <summary>
    /// Moves a pilot and everything that happened in it back in time, so
    /// that the pilot that began today began that many days ago. Fourteen
    /// days puts the whole period in the past: the pilot is over.
    /// </summary>
    public static async Task TimePassesAsync(this BrewForgeApiFactory factory, PilotSetup pilot, int days = 14)
    {
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE pilot_program SET start_date = start_date - {days}, end_date = end_date - {days} WHERE id = {pilot.PilotId}"));
        await factory.BackdateLaunchAsync(pilot.RecipeId, days);
    }

    /// <summary>The managers of the pilot branches import the same cups for every day of a range of the period.</summary>
    public static async Task SellAsync(this BrewForgeApiFactory factory, LiveDrink drink, DateOnly from, DateOnly to,
        int cupsPerDay)
    {
        var lines = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(i => Line(drink, from.AddDays(i), cupsPerDay)).ToArray();
        var result = await (await factory.ImportAsync(Csv(lines), username: ManagerOf(drink.BranchCode)))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(result.GetProperty("rejected").GetInt32() == 0, result.GetRawText());
    }

    /// <summary>
    /// A pilot at B01 and B02 that ran its fourteen days and is over: both
    /// branches live from the first day and selling the given cups every day.
    /// </summary>
    public static async Task<PilotSetup> NewFinishedPilotAsync(this BrewForgeApiFactory factory, object[]? criteria = null,
        int cupsAtB01 = 50, int cupsAtB02 = 45)
    {
        var pilot = await factory.NewRunningPilotAsync(["B01", "B02"], criteria);
        await factory.TimePassesAsync(pilot);
        await factory.SellAsync(pilot.DrinkAt("B01"), Today.AddDays(-14), Today.AddDays(-1), cupsAtB01);
        await factory.SellAsync(pilot.DrinkAt("B02"), Today.AddDays(-14), Today.AddDays(-1), cupsAtB02);
        return pilot;
    }
}
