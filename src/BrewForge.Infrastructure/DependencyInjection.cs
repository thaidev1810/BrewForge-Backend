using BrewForge.Application.Abstractions;
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

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<BrewForgeDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                                       .GetConnectionString(ConnectionStringName)
                                   ?? throw new InvalidOperationException(
                                       $"Connection string '{ConnectionStringName}' is not configured.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<IBrewForgeDbContext>(provider => provider.GetRequiredService<BrewForgeDbContext>());

        services.AddScoped<DataSeeder>();
        services.AddScoped<DatabaseInitializer>();

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        return services;
    }
}
