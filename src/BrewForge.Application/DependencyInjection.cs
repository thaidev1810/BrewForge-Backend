using BrewForge.Application.Auth;
using BrewForge.Application.MasterData;
using BrewForge.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace BrewForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<BranchService>();
        services.AddScoped<IngredientService>();
        services.AddScoped<EquipmentClassService>();
        return services;
    }
}
