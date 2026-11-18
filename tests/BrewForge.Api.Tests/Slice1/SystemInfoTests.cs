using System.Net;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>
/// BE-04: GET /system/info tells which build is running and whether its database has every schema script, so
/// that a 200 from the API is not mistaken for "the right commit with every migration".
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SystemInfoTests(BrewForgeApiFactory factory)
{
    private const string Info = "/api/v1/system/info";

    [Fact]
    public async Task Anyone_reads_the_build_and_a_database_with_every_script_is_ready()
    {
        using var anonymous = factory.CreateClient();
        var info = await (await anonymous.GetAsync(Info)).ShouldBeAsync(HttpStatusCode.OK);

        var scripts = DatabaseInitializer.ReadScripts();
        var database = info.GetProperty("database");
        Assert.Equal(("BrewForge.Api", true, true), (info.GetProperty("application").GetString(),
            info.GetProperty("ready").GetBoolean(), database.GetProperty("reachable").GetBoolean()));
        Assert.False(string.IsNullOrEmpty(info.GetProperty("version").GetString()));
        Assert.Equal((scripts[^1].Name, scripts.Count, scripts.Count, 0, 0), (database.GetProperty("latestScript").GetString(),
            database.GetProperty("expectedScripts").GetInt32(), database.GetProperty("appliedScripts").GetInt32(),
            database.GetProperty("missing").GetArrayLength(), database.GetProperty("changed").GetArrayLength()));

        // Nothing secret: no connection string, password or host.
        var raw = info.GetRawText();
        Assert.DoesNotContain("Password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_database_missing_a_script_or_holding_an_older_one_is_not_ready_and_says_which()
    {
        var scripts = DatabaseInitializer.ReadScripts();
        var (missing, changed) = (scripts[^1].Name, scripts[^2].Name);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM schema_script_log WHERE name = {missing}");
            await db.Database.ExecuteSqlAsync($"UPDATE schema_script_log SET checksum = {new string('0', 64)} WHERE name = {changed}");
        });
        try
        {
            using var anonymous = factory.CreateClient();
            var info = await (await anonymous.GetAsync(Info)).ShouldBeAsync(HttpStatusCode.OK);

            Assert.False(info.GetProperty("ready").GetBoolean());
            var database = info.GetProperty("database");
            Assert.Equal([missing], database.GetProperty("missing").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal([changed], database.GetProperty("changed").EnumerateArray().Select(s => s.GetString()));
        }
        finally
        {
            // What every start of the API does: apply the scripts and record them again.
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().ApplySchemaAsync();
        }

        using var again = factory.CreateClient();
        Assert.True((await (await again.GetAsync(Info)).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("ready").GetBoolean());
    }
}
