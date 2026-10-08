using BrewForge.Api.Auth;
using BrewForge.Api.Errors;
using BrewForge.Api.Json;
using BrewForge.Application;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Infrastructure;
using BrewForge.Infrastructure.Persistence;
using BrewForge.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers()
    .AddJsonOptions(json =>
    {
        json.JsonSerializerOptions.Converters.Add(new EnumCodeJsonConverterFactory());
        json.JsonSerializerOptions.Converters.Add(new TimeOnlyJsonConverter());
    })
    .ConfigureApiBehaviorOptions(api => api.InvalidModelStateResponseFactory = ErrorEnvelope.InvalidModelState);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        // Keep the claim names of the contract (sub, username, role, branchId) as they are.
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = jwt.Value.CreateKey(),
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = BrewForgeClaims.Username,
            RoleClaimType = BrewForgeClaims.Role,
        };
    });
builder.Services.AddAuthorization(Policies.Configure);
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, EnvelopeAuthorizationResultHandler>();

const string frontendCors = "frontend";
builder.Services.AddCors(cors => cors.AddPolicy(frontendCors, policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Configuration.GetValue("Database:InitializeOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

app.UseMiddleware<ErrorEnvelopeMiddleware>();
app.UseStatusCodePages(statusCode => ErrorEnvelope.WriteAsync(statusCode.HttpContext,
    statusCode.HttpContext.Response.StatusCode,
    ErrorEnvelope.ForStatusCode(statusCode.HttpContext, statusCode.HttpContext.Response.StatusCode)));

app.UseCors(frontendCors);
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
app.MapControllers();

await app.RunAsync();

/// <summary>Exposed so the integration tests can host the API in-process.</summary>
public partial class Program;
