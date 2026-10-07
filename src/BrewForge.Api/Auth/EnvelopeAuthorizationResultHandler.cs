using BrewForge.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Net.Http.Headers;

namespace BrewForge.Api.Auth;

/// <summary>Answers a failed authorization with the error envelope instead of an empty 401 or 403.</summary>
public sealed class EnvelopeAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return ErrorEnvelope.WriteAsync(context, StatusCodes.Status401Unauthorized,
                ErrorEnvelope.ForStatusCode(context, StatusCodes.Status401Unauthorized));
        }

        if (authorizeResult.Forbidden)
        {
            return ErrorEnvelope.WriteAsync(context, StatusCodes.Status403Forbidden,
                ErrorEnvelope.ForStatusCode(context, StatusCodes.Status403Forbidden));
        }

        return next(context);
    }
}
