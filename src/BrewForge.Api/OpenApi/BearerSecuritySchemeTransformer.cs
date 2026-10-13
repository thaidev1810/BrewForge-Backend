using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BrewForge.Api.OpenApi;

/// <summary>
/// Declares the JWT bearer security scheme on the generated OpenAPI document so the
/// Swagger UI shows an "Authorize" button where the front-end can paste an access token.
/// </summary>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the JWT access token here (the 'Bearer ' prefix is added automatically).",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = scheme;

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeId, document)] = [],
        };

        document.Security ??= [];
        document.Security.Add(requirement);

        return Task.CompletedTask;
    }
}
