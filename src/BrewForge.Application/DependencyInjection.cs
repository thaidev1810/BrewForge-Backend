using BrewForge.Application.Audit;
using BrewForge.Application.Auth;
using BrewForge.Application.Courses;
using BrewForge.Application.Dashboards;
using BrewForge.Application.Impact;
using BrewForge.Application.Launch;
using BrewForge.Application.MasterData;
using BrewForge.Application.Notifications;
using BrewForge.Application.Recipes;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Application.Sales;
using BrewForge.Application.Training;
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

        services.AddScoped<RecipeService>();
        services.AddScoped<RecipeValidationService>();
        services.AddScoped<RecipeDraftingService>();
        services.AddScoped<RecipeReleaseService>();

        services.AddScoped<CourseRenderer>();
        services.AddScoped<CourseService>();

        services.AddScoped<EnrollmentEvaluator>();
        services.AddScoped<TrainingRegulationService>();
        services.AddScoped<TrainingClassService>();
        services.AddScoped<LearningService>();
        services.AddScoped<LearningPathService>();
        services.AddScoped<AssessmentService>();
        services.AddScoped<LaunchReadinessService>();
        services.AddScoped<LaunchHistory>();
        services.AddScoped<LaunchStatusService>();
        services.AddScoped<PilotService>();

        services.AddScoped<ImpactAnalysisService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<AuditService>();
        services.AddScoped<ComplianceReportService>();

        services.AddScoped<SalesService>();
        services.AddScoped<PosImportService>();
        services.AddScoped<SalesAnalyticsService>();
        services.AddScoped<DashboardService>();
        return services;
    }
}
