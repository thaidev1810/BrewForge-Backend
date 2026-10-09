using BrewForge.Domain.Recipes;

namespace BrewForge.Domain.Tests.Recipes;

public sealed class UnitConverterTests
{
    [Theory]
    [InlineData("18", "g", "g", "18")]
    [InlineData("1.5", "kg", "g", "1500")]
    [InlineData("500", "mg", "g", "0.5")]
    [InlineData("1500", "g", "kg", "1.5")]
    [InlineData("1.5", "kg", "mg", "1500000")]
    [InlineData("0.25", "l", "ml", "250")]
    [InlineData("330", "ml", "l", "0.33")]
    [InlineData("2", "min", "sec", "120")]
    [InlineData("90", "sec", "min", "1.5")]
    [InlineData("3", "pcs", "pcs", "3")]
    [InlineData("92", "degC", "degC", "92")]
    [InlineData("9", "bar", "bar", "9")]
    public void Converts_within_a_dimension(string quantity, string from, string to, string expected)
    {
        Assert.True(UnitConverter.TryConvert(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), from, to, out var converted));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), converted);
    }

    [Theory]
    [InlineData("g", "ml")]    // no density: mass is never volume
    [InlineData("ml", "g")]
    [InlineData("pcs", "g")]
    [InlineData("g", "sec")]
    [InlineData("degC", "bar")]
    [InlineData("cup", "ml")]  // not a unit of the system
    [InlineData("g", "oz")]
    [InlineData("G", "g")]     // unit codes are case-sensitive, as the enumerations are
    [InlineData("", "g")]
    [InlineData(null, "g")]
    [InlineData("g", null)]
    public void Refuses_to_convert_across_dimensions_or_unknown_units(string? from, string? to)
    {
        Assert.False(UnitConverter.CanConvert(from, to));
        Assert.False(UnitConverter.TryConvert(1m, from, to, out _));
    }

    /// <summary>
    /// The property the threshold check rests on: conversion is exact, so a
    /// dose a hair over the limit is still over it after conversion.
    /// </summary>
    [Fact]
    public void Conversion_is_exact_decimal_arithmetic()
    {
        Assert.True(UnitConverter.TryConvert(0.0251m, "kg", "g", out var justOver));
        Assert.Equal(25.1m, justOver);
        Assert.True(justOver > 25m);

        Assert.True(UnitConverter.TryConvert(0.025m, "kg", "g", out var onTheLimit));
        Assert.Equal(25m, onTheLimit);

        Assert.True(UnitConverter.TryConvert(25_001m, "mg", "g", out var oneMilligramOver));
        Assert.Equal(25.001m, oneMilligramOver);
    }

    [Fact]
    public void Round_trip_returns_the_original_quantity()
    {
        Assert.True(UnitConverter.TryConvert(17.375m, "g", "kg", out var kilograms));
        Assert.True(UnitConverter.TryConvert(kilograms, "kg", "g", out var grams));
        Assert.Equal(17.375m, grams);
    }

    [Fact]
    public void Knows_every_unit_of_the_two_unit_enumerations()
    {
        foreach (var unit in new[] { "g", "ml", "pcs", "sec", "degC", "bar" })
        {
            Assert.True(UnitConverter.IsKnown(unit), unit);
        }
    }
}
