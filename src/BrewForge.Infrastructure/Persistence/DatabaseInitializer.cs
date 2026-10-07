using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BrewForge.Infrastructure.Persistence;

/// <summary>
/// Brings an empty database to the schema of <c>db/migrations</c> and then
/// seeds it. Safe to run on every start and from several instances at once:
/// the scripts are applied only when the schema is missing, under an advisory
/// lock, and the seed only inserts what is not there yet.
/// </summary>
public sealed class DatabaseInitializer(BrewForgeDbContext db, DataSeeder seeder,
    ILogger<DatabaseInitializer> logger)
{
    private const long AdvisoryLockKey = 0x42726577466F7267; // "BrewForg"
    private const string ResourcePrefix = "migrations/";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ApplySchemaAsync(cancellationToken);
        await seeder.SeedAsync(cancellationToken);
    }

    public async Task ApplySchemaAsync(CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync($"SELECT pg_advisory_lock({AdvisoryLockKey})");
            try
            {
                if (await SchemaExistsAsync()) return;

                foreach (var (name, sql) in ReadScripts())
                {
                    logger.LogInformation("Applying database script {Script}", name);
                    await ExecuteAsync(sql);
                }
            }
            finally
            {
                await ExecuteAsync($"SELECT pg_advisory_unlock({AdvisoryLockKey})");
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        async Task<bool> SchemaExistsAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('public.role') IS NOT NULL";
            return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
    }

    /// <summary>The embedded copies of <c>db/migrations/*.sql</c>, in name order.</summary>
    public static IReadOnlyList<(string Name, string Sql)> ReadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(name =>
                {
                    using var stream = assembly.GetManifestResourceStream(name)!;
                    using var reader = new StreamReader(stream);
                    return (name[ResourcePrefix.Length..], reader.ReadToEnd());
                }),
        ];
    }
}
