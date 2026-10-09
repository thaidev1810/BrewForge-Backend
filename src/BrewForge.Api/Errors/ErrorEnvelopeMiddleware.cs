using BrewForge.Domain.Common;
using BrewForge.Infrastructure.Persistence;

namespace BrewForge.Api.Errors;

/// <summary>
/// Converts every failure into the error envelope. An expected refusal keeps
/// its code and rule; a database rejection is translated to the rule it
/// enforces; anything else becomes a 500 that leaks no detail.
/// </summary>
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var refusal = exception as DomainException ?? PostgresErrorTranslator.Translate(exception);
            if (refusal is not null)
            {
                logger.LogInformation("Request refused: {Code} {Rule} {Message}", refusal.Code, refusal.Rule,
                    refusal.Message);
                await ErrorEnvelope.WriteAsync(context, ErrorEnvelope.StatusCodeOf(refusal.Kind),
                    ErrorEnvelope.From(context, refusal));
                return;
            }

            if (exception is BadHttpRequestException badRequest)
            {
                await ErrorEnvelope.WriteAsync(context, badRequest.StatusCode,
                    ErrorEnvelope.ForStatusCode(context, badRequest.StatusCode));
                return;
            }

            logger.LogError(exception, "Unhandled exception");
            await ErrorEnvelope.WriteAsync(context, StatusCodes.Status500InternalServerError,
                ErrorEnvelope.ForStatusCode(context, StatusCodes.Status500InternalServerError));
        }
    }
}
