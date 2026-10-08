using System.Globalization;
using BrewForge.Domain.Common;

namespace BrewForge.Domain.Sales;

/// <summary>Why one line of a POS file was rejected. The row number is the one the user sees in the file.</summary>
public sealed record PosRowError(int Row, string Code, string Message);

/// <summary>
/// One line of a POS export, read and understood. The layout is fixed:
/// <c>branch_code, drink_code, trading_date, quantity</c>, in that order,
/// with a header line. There is no column mapping.
/// </summary>
public sealed record PosImportLine(int Row, string BranchCode, string DrinkCode, DateOnly TradingDate, int Quantity)
{
    public static readonly IReadOnlyList<string> Columns = ["branch_code", "drink_code", "trading_date", "quantity"];

    /// <summary>The first line of the file must name the four columns, in order.</summary>
    public static void EnsureLayout(IReadOnlyList<string>? header)
    {
        var found = (header ?? []).Select(cell => cell.Trim().ToLowerInvariant()).ToList();
        while (found.Count > 0 && found[^1].Length == 0) found.RemoveAt(found.Count - 1);

        if (!found.SequenceEqual(Columns))
        {
            throw DomainException.Validation(ErrorCodes.ImportLayout,
                $"The file does not have the supported layout. Its first line must be: {string.Join(", ", Columns)}.",
                [new ErrorDetail("file", found.Count == 0 ? "is empty" : $"first line is: {string.Join(", ", found)}")]);
        }
    }

    /// <summary>
    /// Reads one data line. A line is either understood completely or
    /// rejected with one reason; nothing is guessed.
    /// </summary>
    public static PosImportLine? Parse(int row, IReadOnlyList<string> cells, out PosRowError? error)
    {
        string Cell(int index) => index < cells.Count ? cells[index].Trim() : "";
        var (branchCode, drinkCode, dateText, quantityText) = (Cell(0), Cell(1), Cell(2), Cell(3));

        error = null;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Cell(i).Length == 0)
            {
                error = new PosRowError(row, ErrorCodes.ImportMissingValue, $"Missing {Columns[i]}");
                return null;
            }
        }
        if (!TryParseDate(dateText, out var tradingDate))
        {
            error = new PosRowError(row, ErrorCodes.ImportInvalidDate,
                $"Trading date '{dateText}' is not a date in the form YYYY-MM-DD");
            return null;
        }
        if (!TryParseQuantity(quantityText, out var quantity))
        {
            error = new PosRowError(row, ErrorCodes.ImportInvalidQuantity,
                $"Quantity '{quantityText}' is not a whole number");
            return null;
        }
        if (quantity < 0)
        {
            error = new PosRowError(row, ErrorCodes.ImportInvalidQuantity, $"Quantity {quantity} is negative");
            return null;
        }
        return new PosImportLine(row, branchCode, drinkCode, tradingDate, quantity);
    }

    /// <summary>
    /// ISO dates, and the day serial a spreadsheet stores for a date cell
    /// (the same day counted from 1899-12-30).
    /// </summary>
    private static bool TryParseDate(string text, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }
        // An ISO date-time with nothing but midnight after the date, as some exports write a date.
        if (text.Length > 10 && text[10] == 'T'
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            && instant.TimeOfDay == TimeSpan.Zero)
        {
            date = DateOnly.FromDateTime(instant);
            return true;
        }
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var serial)
            && serial is >= 36526 and <= 73050) // 2000-01-01 to 2099-12-31
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    /// <summary>A whole number. "143.0" is accepted, because that is how a spreadsheet may write 143.</summary>
    private static bool TryParseQuantity(string text, out int quantity)
    {
        quantity = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }
        if (number != decimal.Truncate(number) || number is < int.MinValue or > int.MaxValue) return false;
        quantity = (int)number;
        return true;
    }
}
