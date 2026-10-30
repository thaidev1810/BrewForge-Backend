using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BrewForge.Api.OpenApi;

/// <summary>
/// Clears the global bearer requirement from anonymous operations (login and refresh), so the OpenAPI
/// document does not say they need a token. An empty operation-level <c>security</c> overrides the
/// document-level one for that operation.
/// </summary>
public sealed class AnonymousOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [];
        }
        return Task.CompletedTask;
    }
}
