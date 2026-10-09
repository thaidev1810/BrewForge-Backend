namespace BrewForge.Domain.Common;

/// <summary>
/// Collects field-level problems so a caller sees every invalid field at once
/// rather than one per round trip. The limits come from the column sizes of
/// the schema.
/// </summary>
public sealed class FieldErrors
{
    private readonly List<ErrorDetail> _details = [];
    private bool _onlyLengthIssues = true;

    public bool Any => _details.Count > 0;

    public FieldErrors Required(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, "is required");
        return this;
    }

    public FieldErrors MaxLength(string field, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            _details.Add(new ErrorDetail(field, $"Exceed max length of {max}."));
        }
        return this;
    }

    public FieldErrors RequiredMax(string field, string? value, int max) =>
        Required(field, value).MaxLength(field, value, max);

    public FieldErrors Check(bool ok, string field, string issue)
    {
        if (!ok) Add(field, issue);
        return this;
    }

    public void ThrowIfAny()
    {
        if (_details.Count == 0) return;
        var code = _onlyLengthIssues ? ErrorCodes.MaxLengthExceeded : ErrorCodes.ValidationFailed;
        throw DomainException.Validation(code, "One or more fields are invalid.", _details);
    }

    private void Add(string field, string issue)
    {
        _onlyLengthIssues = false;
        _details.Add(new ErrorDetail(field, issue));
    }
}
