using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

/// <summary>A named value an administrator maintains. The keys are fixed by <see cref="Known"/>; the values are validated here.</summary>
public sealed class SystemSetting : INeverDeleted
{
    public const string CompanyName = "company.name";
    public const string AuditPassMark = "audit.pass_mark";

    /// <summary>The settings that exist, with the check each value must pass.</summary>
    public static readonly IReadOnlyDictionary<string, Func<string, string?>> Known =
        new Dictionary<string, Func<string, string?>>
        {
            [CompanyName] = v => v.Length is >= 1 and <= 120 ? null : "must be 1 to 120 characters",
            [AuditPassMark] = v => int.TryParse(v, out var n) && n is >= 1 and <= 100 ? null : "must be a whole number from 1 to 100",
        };

    private SystemSetting() { }

    public long Id { get; private set; }
    public string SettingKey { get; private set; } = null!;
    public string Value { get; private set; } = null!;
    public DateTimeOffset UpdatedAt { get; private set; }
    public long? UpdatedBy { get; private set; }

    string INeverDeleted.RetentionRule => "BR-16";

    public void Set(string value, long? userId, DateTimeOffset now)
    {
        value = value?.Trim() ?? "";
        var problem = Known.TryGetValue(SettingKey, out var check) ? check(value) : "is not a setting";
        if (problem is not null)
        {
            throw DomainException.Validation("The setting value is invalid.", new ErrorDetail("value", problem));
        }

        Value = value;
        UpdatedBy = userId;
        UpdatedAt = now;
    }
}
