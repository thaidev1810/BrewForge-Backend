namespace BrewForge.Domain.Recipes;

/// <summary>
/// Converts between units of the same physical dimension. Units of different
/// dimensions are never convertible: there is deliberately no density table,
/// so grams never become millilitres.
///
/// Every factor is an exact decimal and the arithmetic is decimal throughout,
/// so a dose that is 0.1 over a threshold is over it and a dose on the
/// threshold is on it - no binary floating point is involved.
/// </summary>
public static class UnitConverter
{
    private enum Dimension { Mass, Volume, Count, Time, Temperature, Pressure }

    /// <summary>Each unit with its dimension and its size in the base unit of that dimension.</summary>
    private static readonly Dictionary<string, (Dimension Dimension, decimal InBaseUnits)> Units =
        new(StringComparer.Ordinal)
        {
            // mass, base unit: gram
            ["mg"] = (Dimension.Mass, 0.001m),
            ["g"] = (Dimension.Mass, 1m),
            ["kg"] = (Dimension.Mass, 1000m),
            // volume, base unit: millilitre
            ["ml"] = (Dimension.Volume, 1m),
            ["l"] = (Dimension.Volume, 1000m),
            // count
            ["pcs"] = (Dimension.Count, 1m),
            // time, base unit: second
            ["sec"] = (Dimension.Time, 1m),
            ["min"] = (Dimension.Time, 60m),
            // single-unit dimensions of the dosing_unit enumeration
            ["degC"] = (Dimension.Temperature, 1m),
            ["bar"] = (Dimension.Pressure, 1m),
        };

    public static IReadOnlyCollection<string> KnownUnits => Units.Keys;

    public static bool IsKnown(string? unit) => unit is not null && Units.ContainsKey(unit);

    /// <summary>Whether a quantity in <paramref name="from"/> can be expressed in <paramref name="to"/>.</summary>
    public static bool CanConvert(string? from, string? to) =>
        from is not null && to is not null
        && Units.TryGetValue(from, out var source) && Units.TryGetValue(to, out var target)
        && source.Dimension == target.Dimension;

    public static bool TryConvert(decimal quantity, string? from, string? to, out decimal converted)
    {
        converted = 0;
        if (from is null || to is null
            || !Units.TryGetValue(from, out var source) || !Units.TryGetValue(to, out var target)
            || source.Dimension != target.Dimension)
        {
            return false;
        }

        // Through the base unit: 1.5 kg -> 1500 g -> 1 500 000 mg.
        converted = quantity * source.InBaseUnits / target.InBaseUnits;
        return true;
    }
}
