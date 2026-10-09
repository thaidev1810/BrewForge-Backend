using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real API in-process against a real PostgreSQL 16 database that
/// is created for the test run and dropped afterwards. The schema is the one
/// in db/migrations, so the triggers and partial unique indexes that enforce
/// business rules are in play exactly as in production.
///
/// The server is taken from BREWFORGE_TEST_DB, or localhost:5432 with the
/// docker-compose credentials. Start it with `docker compose up -d` or
/// `scripts/start-db.ps1`.
/// </summary>
public sealed class BrewForgeApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Password of every seeded user in the test database. Never used outside it.</summary>
    public const string SeedPassword = "Test-Only-Passw0rd";

    private const string DefaultServer = "Host=localhost;Port=5432;Username=brewforge;Password=brewforge";

    private readonly string _server = Environment.GetEnvironmentVariable("BREWFORGE_TEST_DB") ?? DefaultServer;
    private readonly string _database = $"brewforge_test_{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, string> _accessTokens = new();

    /// <summary>Where the recordings uploaded in this run are kept. Removed with the database.</summary>
    public string VideoRoot { get; } = Path.Combine(Path.GetTempPath(), $"brewforge-test-videos-{Guid.NewGuid():N}");

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    private string ConnectionString =>
        new NpgsqlConnectionStringBuilder(_server) { Database = _database, IncludeErrorDetail = true }.ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:BrewForge", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789-abcdefghij");
        builder.UseSetting("Database:InitializeOnStartup", "true");
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:DefaultPassword", SeedPassword);
        // Cheap parameters keep the suite fast; the algorithm is still Argon2id.
        builder.UseSetting("Argon2:MemoryKib", "8192");
        builder.UseSetting("Argon2:Iterations", "1");
        // The scheduler does not run on its own in tests; a test runs it when it wants it to.
        builder.UseSetting("Scheduler:PilotEndIntervalMinutes", "0");
        builder.UseSetting("Scheduler:NotificationIntervalSeconds", "0");
        builder.UseSetting("Storage:PracticalVideoRoot", VideoRoot);

        // No test ever reaches a real language model.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRecipeDraftModel>();
            services.AddSingleton<IRecipeDraftModel>(DraftModel);
            // Nor a real mail server or push service.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Push);
        });
    }

    /// <summary>The scripted language model the API talks to in tests.</summary>
    public FakeDraftModel DraftModel { get; } = new();

    /// <summary>The mail server the API sends through in tests.</summary>
    public FakeEmailSender Email { get; } = new();

    /// <summary>The push services the API sends to in tests.</summary>
    public FakePushSender Push { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            await ExecuteOnServerAsync($"CREATE DATABASE \"{_database}\"");
        }
        catch (Exception exception) when (exception is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                "The integration tests need PostgreSQL 16. Start it with 'docker compose up -d' or " +
                "'scripts/start-db.ps1', or point BREWFORGE_TEST_DB at a server. " + exception.Message, exception);
        }

        _ = Server; // starts the host, which applies the schema and the seed
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        if (Directory.Exists(VideoRoot)) Directory.Delete(VideoRoot, recursive: true);
    }

    /// <summary>A client carrying a valid access token of the seeded user.</summary>
    public async Task<HttpClient> ClientForAsync(string username)
    {
        if (!_accessTokens.TryGetValue(username, out var token))
        {
            token = (await LoginAsync(username)).AccessToken;
            _accessTokens[username] = token;
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task<HttpClient> ClientForAsync(RoleName role) => ClientForAsync(TestUsers.For(role));

    public async Task<LoginResult> LoginAsync(string username, string password = SeedPassword)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResult>(Json))!;
    }

    /// <summary>Runs against the database with no caller, that is, unrestricted.</summary>
    public async Task<T> WithDbAsync<T>(Func<BrewForgeDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<BrewForgeDbContext>());
    }

    public Task WithDbAsync(Func<BrewForgeDbContext, Task> work) =>
        WithDbAsync(async db =>
        {
            await work(db);
            return true;
        });

    /// <summary>A context that sees the database as the given caller would.</summary>
    public BrewForgeDbContext CreateDbContextAs(ICurrentUser caller)
    {
        var options = Services.GetRequiredService<DbContextOptions<BrewForgeDbContext>>();
        return new BrewForgeDbContext(options, caller, TimeProvider.System);
    }

    public Task<long> BranchIdAsync(string branchCode) =>
        WithDbAsync(db => db.Branches.Where(b => b.BranchCode == branchCode).Select(b => b.Id).SingleAsync());

    public Task<long> UserIdAsync(string username) =>
        WithDbAsync(db => db.Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync());

    private async Task ExecuteOnServerAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(_server) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ToString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record LoginResult(string AccessToken, string RefreshToken, JsonElement User);

/// <summary>The seeded user that represents each role in the tests.</summary>
public static class TestUsers
{
    public const string Admin = "admin";
    public const string RdSpecialist = "rdspec";
    public const string RdManager = "rdmanager";
    public const string Trainer = "trainer";
    public const string Auditor = "auditor";
    public const string TrainingManager = "trainingmgr";

    /// <summary>Branch manager of B01.</summary>
    public const string BranchManager = "branchmgr";

    /// <summary>Branch manager of B02.</summary>
    public const string BranchManagerB02 = "branchmgr2";

    /// <summary>Trainee of B01.</summary>
    public const string Trainee = "trainee";

    public static string For(RoleName role) => role switch
    {
        RoleName.Admin => Admin,
        RoleName.RdSpecialist => RdSpecialist,
        RoleName.RdManager => RdManager,
        RoleName.Trainer => Trainer,
        RoleName.Trainee => Trainee,
        RoleName.QualityAuditor => Auditor,
        RoleName.BranchManager => BranchManager,
        RoleName.TrainingManager => TrainingManager,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<BrewForgeApiFactory>
{
    public const string Name = "api";
}
