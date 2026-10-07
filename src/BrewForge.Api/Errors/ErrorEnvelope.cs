using System.Diagnostics;
using BrewForge.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Errors;

/// <summary>The one shape of every 4xx and 5xx response (API contract, section 1).</summary>
public sealed record ErrorEnvelope(string Code, string Message, string? Rule, IReadOnlyList<ErrorDetail> Details,
    string TraceId)
{
    public static ErrorEnvelope Create(HttpContext context, string code, string message, string? rule = null,
        IReadOnlyList<ErrorDetail>? details = null) =>
        new(code, message, rule, details ?? [], Activity.Current?.Id ?? context.TraceIdentifier);

    public static ErrorEnvelope From(HttpContext context, DomainException exception) =>
        Create(context, exception.Code, exception.Message, exception.Rule, exception.Details);

    public static int StatusCodeOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.RuleViolation => StatusCodes.Status409Conflict,
        ErrorKind.Refused => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static Task WriteAsync(HttpContext context, int statusCode, ErrorEnvelope envelope)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(envelope, context.RequestAborted);
    }

    /// <summary>For a status produced without a body, such as an unknown route or a method the route does not offer.</summary>
    public static ErrorEnvelope ForStatusCode(HttpContext context, int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => Create(context, ErrorCodes.Unauthenticated,
            "Authentication is required. The access token is missing, invalid or expired."),
        StatusCodes.Status403Forbidden => Create(context, ErrorCodes.Forbidden,
            "You do not have permission to perform this action."),
        StatusCodes.Status404NotFound => Create(context, ErrorCodes.NotFound, "The resource does not exist."),
        StatusCodes.Status405MethodNotAllowed => Create(context, ErrorCodes.MethodNotAllowed,
            "This resource does not offer that method. Records are deactivated, never deleted."),
        StatusCodes.Status415UnsupportedMediaType => Create(context, ErrorCodes.ValidationFailed,
            "The request body must be application/json."),
        >= 500 => Create(context, ErrorCodes.InternalError, "An unexpected error occurred."),
        _ => Create(context, ErrorCodes.ValidationFailed, "The request could not be processed."),
    };

    /// <summary>Malformed JSON and values of the wrong type, reported per field.</summary>
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var details = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error =>
                new ErrorDetail(FieldName(entry.Key), Issue(error.ErrorMessage))))
            .ToList();

        return new BadRequestObjectResult(Create(context.HttpContext, ErrorCodes.ValidationFailed,
            "The request body or parameters are invalid.", details: details));
    }

    private static string FieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        return name is "" or "$" ? "body" : name;
    }

    // System.Text.Json appends the position in the document, which is noise next to the field name.
    private static string Issue(string message)
    {
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        message = cut > 0 ? message[..cut] : message;
        return string.IsNullOrWhiteSpace(message) ? "is invalid" : message;
    }
}
