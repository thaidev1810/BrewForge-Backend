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
            // BE-03: why the token was refused, when one was sent; no rule when no token came at all.
            var envelope = ErrorEnvelope.ForStatusCode(context, StatusCodes.Status401Unauthorized);
            if (context.Items[AccessTokenValidator.RuleItem] is string rule)
            {
                envelope = envelope with { Rule = rule };
            }
            return ErrorEnvelope.WriteAsync(context, StatusCodes.Status401Unauthorized, envelope);
        }

        if (authorizeResult.Forbidden)
        {
            return ErrorEnvelope.WriteAsync(context, StatusCodes.Status403Forbidden,
                ErrorEnvelope.ForStatusCode(context, StatusCodes.Status403Forbidden));
        }

        return next(context);
    }
}
