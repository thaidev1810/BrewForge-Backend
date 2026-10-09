using BrewForge.Application.Abstractions;
using BrewForge.Application.Audit;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Application.Sales;
using BrewForge.Infrastructure.Files;
using BrewForge.Infrastructure.Llm;
using BrewForge.Infrastructure.Persistence;
using BrewForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrewForge.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "BrewForge";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<Argon2Options>(configuration.GetSection(Argon2Options.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));

        services.TryAddSingleton<TimeProvider>(new DatabasePrecisionTimeProvider(TimeProvider.System));

        services.AddDbContext<BrewForgeDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                                       .GetConnectionString(ConnectionStringName)
                                   ?? throw new InvalidOperationException(
                                       $"Connection string '{ConnectionStringName}' is not configured.");
            // An aggregate is loaded with several collections; one query per
            // collection avoids multiplying its rows by each other.
            options.UseNpgsql(connectionString,
                npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        });
        services.AddScoped<IBrewForgeDbContext>(provider => provider.GetRequiredService<BrewForgeDbContext>());

        services.AddScoped<DataSeeder>();
        services.AddScoped<DatabaseInitializer>();

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddSingleton<IPosFileReader, PosFileReader>();
        services.AddSingleton<IReportExporter, ReportExporter>();

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddSingleton<IPracticalVideoStorage, LocalPracticalVideoStorage>();

        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.Section));
        services.AddHttpClient<IRecipeDraftModel, OpenAiRecipeDraftModel>(http =>
        {
            // The adapter enforces the 30-second limit of MSG-E05 itself.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        return services;
    }
}
