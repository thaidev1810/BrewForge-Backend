using System.Reflection;
using System.Text;

namespace BrewForge.Domain.Common;

/// <summary>
/// Overrides the stored code of an enum member. Without it the code is the
/// member name in UPPER_SNAKE_CASE, which is what the schema uses everywhere
/// except for units (<c>g</c>, <c>ml</c>, <c>degC</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class CodeAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}

/// <summary>
/// Maps an enum to the exact string the data dictionary lists for it, so the
/// database, the API and the code share one spelling of every enumerated value.
/// </summary>
public static class EnumCode<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Codes = [];
    private static readonly Dictionary<string, T> Values = new(StringComparer.Ordinal);

    static EnumCode()
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (T)field.GetValue(null)!;
            var code = field.GetCustomAttribute<CodeAttribute>()?.Code ?? ToUpperSnake(field.Name);
            Codes[value] = code;
            Values[code] = value;
        }
    }

    public static IReadOnlyCollection<string> AllCodes => Values.Keys;

    public static string ToCode(T value) =>
        Codes.TryGetValue(value, out var code)
            ? code
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a defined {typeof(T).Name}.");

    public static bool TryParse(string? code, out T value)
    {
        if (code is not null && Values.TryGetValue(code, out value)) return true;
        value = default;
        return false;
    }

    public static T Parse(string code) =>
        TryParse(code, out var value)
            ? value
            : throw new FormatException(
                $"'{code}' is not a valid {typeof(T).Name}. Allowed: {string.Join(", ", Values.Keys)}.");

    private static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }
}

public static class EnumCodeExtensions
{
    /// <summary>The value as the schema spells it, for example <c>RD_SPECIALIST</c>.</summary>
    public static string Code<T>(this T value) where T : struct, Enum => EnumCode<T>.ToCode(value);
}
