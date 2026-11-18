using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Infrastructure.Persistence;

/// <summary>The schema scripts the build carries, against those the database has recorded as applied.</summary>
public sealed record SchemaInfoDto(bool Reachable, string? LatestScript, int ExpectedScripts, int AppliedScripts,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Changed);

/// <summary>
/// BE-04: what is running - the build, its commit, and whether the database has every schema script of that
/// build. <c>Ready</c> is true only when the database answers and holds every script, unchanged. Nothing
/// secret is in it: no connection string, no configuration value, no host name.
/// </summary>
public sealed record SystemInfoDto(string Application, string Version, string? Commit, string Environment,
    DateTimeOffset StartedAt, SchemaInfoDto Database, bool Ready);

public sealed class SystemInfoReader(BrewForgeDbContext db)
{
    /// <summary>Set by the build (a CI or Docker build argument) when the source has no .git to read the commit from.</summary>
    public const string CommitVariable = "BREWFORGE_COMMIT";

    private static readonly DateTimeOffset StartedAt =
        new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

    public async Task<SystemInfoDto> ReadAsync(string environmentName, CancellationToken cancellationToken)
    {
        var assembly = typeof(SystemInfoReader).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        var version = plus < 0 ? informational : informational[..plus];
        var commit = Environment.GetEnvironmentVariable(CommitVariable) is { Length: > 0 } fromBuild
            ? fromBuild
            : plus < 0 ? null : informational[(plus + 1)..];

        var schema = await ReadSchemaAsync(cancellationToken);
        return new SystemInfoDto("BrewForge.Api", version, commit, environmentName, StartedAt, schema,
            schema.Reachable && schema.Missing.Count == 0 && schema.Changed.Count == 0);
    }

    private async Task<SchemaInfoDto> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        var expected = DatabaseInitializer.ReadScripts();
        Dictionary<string, string> applied;
        try
        {
            applied = await db.Database
                .SqlQueryRaw<ScriptRow>("SELECT name AS \"Name\", checksum AS \"Checksum\" FROM schema_script_log")
                .ToDictionaryAsync(row => row.Name, row => row.Checksum.Trim(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Unreachable, or a database from before 022: either way, nothing can be vouched for.
            return new SchemaInfoDto(await CanConnectAsync(cancellationToken), expected.LastOrDefault().Name,
                expected.Count, 0, [.. expected.Select(script => script.Name)], []);
        }

        var missing = expected.Where(script => !applied.ContainsKey(script.Name)).Select(script => script.Name).ToList();
        var changed = expected.Where(script => applied.TryGetValue(script.Name, out var sum) && sum != Checksum(script.Sql))
            .Select(script => script.Name).ToList();
        return new SchemaInfoDto(true, expected.LastOrDefault().Name, expected.Count,
            expected.Count(script => applied.ContainsKey(script.Name)), missing, changed);
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try { return await db.Database.CanConnectAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return false; }
    }

    /// <summary>The checksum the initializer records for a script: SHA-256 of its text, line endings normalised.</summary>
    public static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));

    private sealed class ScriptRow
    {
        public string Name { get; set; } = "";
        public string Checksum { get; set; } = "";
    }
}
