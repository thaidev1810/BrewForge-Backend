namespace BrewForge.Domain.Common;

/// <summary>What kind of refusal this is. The API maps it to an HTTP status.</summary>
public enum ErrorKind
{
    /// <summary>400 - malformed body or failed field validation.</summary>
    Validation,
    /// <summary>401 - missing, expired or revoked credentials.</summary>
    Unauthenticated,
    /// <summary>403 - authenticated, but the role or branch is not permitted.</summary>
    Forbidden,
    /// <summary>404 - not found, or outside the caller's branch scope.</summary>
    NotFound,
    /// <summary>409 - a business rule rejects the request. Always names the rule.</summary>
    RuleViolation,
    /// <summary>422 - well-formed, but the domain refuses it.</summary>
    Refused,
}

public sealed record ErrorDetail(string Field, string Issue);

/// <summary>
/// The single exception type the domain and application layers throw for an
/// expected refusal. It carries everything the API error envelope needs.
/// </summary>
public sealed class DomainException : Exception
{
    private DomainException(ErrorKind kind, string code, string message, string? rule,
        IReadOnlyList<ErrorDetail>? details)
        : base(message)
    {
        Kind = kind;
        Code = code;
        Rule = rule;
        Details = details ?? [];
    }

    public ErrorKind Kind { get; }
    public string Code { get; }
    public string? Rule { get; }
    public IReadOnlyList<ErrorDetail> Details { get; }

    public static DomainException Validation(string message, params ErrorDetail[] details) =>
        new(ErrorKind.Validation, ErrorCodes.ValidationFailed, message, null, details);

    public static DomainException Validation(string code, string message, IReadOnlyList<ErrorDetail> details) =>
        new(ErrorKind.Validation, code, message, null, details);

    public static DomainException Unauthenticated(string message) =>
        new(ErrorKind.Unauthenticated, ErrorCodes.Unauthenticated, message, null, null);

    public static DomainException Forbidden(string? rule = null, string? message = null) =>
        new(ErrorKind.Forbidden, ErrorCodes.Forbidden,
            message ?? "You do not have permission to perform this action.", rule, null);

    public static DomainException NotFound(string entity, object id) =>
        new(ErrorKind.NotFound, ErrorCodes.NotFound, $"{entity} {id} was not found.", null, null);

    public static DomainException RuleViolation(string rule, string message, string? code = null,
        params ErrorDetail[] details) =>
        new(ErrorKind.RuleViolation, code ?? ErrorCodes.Conflict, message, rule, details);

    public static DomainException Refused(string code, string message, string? rule = null,
        params ErrorDetail[] details) =>
        new(ErrorKind.Refused, code, message, rule, details);
}

/// <summary>
/// Envelope codes. The MSG-* values are the message codes of Report 3 section
/// 5.3 and of the API contract; the others cover failures the list has no
/// message for.
/// </summary>
public static class ErrorCodes
{
    public const string Forbidden = "MSG-E01";
    public const string SourceVersionSuperseded = "MSG-W02";
    public const string CourseBeingUpdated = "MSG-W05";
    public const string ValidationFailedOnChecks = "MSG-E03";
    public const string LlmSchemaMismatch = "MSG-E04";
    public const string LlmTimeout = "MSG-E05";
    public const string ReleasedVersionImmutable = "MSG-E06";
    public const string CircularDependency = "MSG-E07";
    public const string MasterDataChanged = "MSG-E08";
    public const string ApproverIsAuthor = "MSG-E09";
    public const string DoseOutOfRange = "MSG-E10";
    public const string MaxLengthExceeded = "MSG-E11";
    public const string CourseHasEmptyLessons = "MSG-E12";
    public const string ImportUnknownDrink = "MSG-E21";
    public const string ImportBranchNotLive = "MSG-E22";
    public const string ImportDuplicateDay = "MSG-E23";

    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string InternalError = "INTERNAL_ERROR";
}
