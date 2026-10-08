using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;

namespace BrewForge.Domain.Sales;

/// <summary>The <c>sales_source</c> enumeration.</summary>
public enum SalesSource
{
    Manual,
    PosImport,
}

/// <summary>
/// Cups of one drink sold at one branch on one trading day. The version is
/// never supplied by whoever enters the figure: it is the version that was
/// live at the branch on that day (BR-24), and a record cannot be made for a
/// day on which the drink was not on sale there.
/// </summary>
public sealed class SalesRecord
{
    private SalesRecord() { }

    public long Id { get; private set; }
    public long BranchId { get; private set; }
    public long RecipeId { get; private set; }
    public long? RecipeVersionId { get; private set; }
    public DateOnly TradingDate { get; private set; }
    public int CupsSold { get; private set; }
    public SalesSource Source { get; private set; }
    public long RecordedBy { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// The only way a sales record comes into being. The launch status of the
    /// branch for the drink decides whether the day can be sold on at all and
    /// which version the cups belong to.
    /// </summary>
    public static SalesRecord Record(BranchLaunchStatus launch, IReadOnlyCollection<LaunchChange> history,
        DateOnly tradingDate, int cupsSold, SalesSource source, long recordedBy, DateTimeOffset now)
    {
        EnsureValid(tradingDate, cupsSold, now);
        return new SalesRecord
        {
            BranchId = launch.BranchId,
            RecipeId = launch.RecipeId,
            RecipeVersionId = launch.VersionSoldOn(tradingDate, history),
            TradingDate = tradingDate,
            CupsSold = cupsSold,
            Source = source,
            RecordedBy = recordedBy,
            RecordedAt = now,
        };
    }

    /// <summary>A correction of the count by hand. Returns false when the figure is the one already stored.</summary>
    public bool Correct(int cupsSold, long recordedBy, DateTimeOffset now)
    {
        EnsureValid(TradingDate, cupsSold, now);
        if (cupsSold == CupsSold) return false;

        CupsSold = cupsSold;
        RecordedBy = recordedBy;
        RecordedAt = now;
        return true;
    }

    /// <summary>
    /// BR-25: a day that is imported again replaces what was recorded for it;
    /// it does not become a second record. Returns false when the import
    /// carries the figure already stored from an import.
    /// </summary>
    public bool ReplaceFromImport(int cupsSold, long recordedBy, DateTimeOffset now)
    {
        EnsureValid(TradingDate, cupsSold, now);
        if (cupsSold == CupsSold && Source == SalesSource.PosImport) return false;

        CupsSold = cupsSold;
        Source = SalesSource.PosImport;
        RecordedBy = recordedBy;
        RecordedAt = now;
        return true;
    }

    private static void EnsureValid(DateOnly tradingDate, int cupsSold, DateTimeOffset now) =>
        new FieldErrors()
            .Check(cupsSold >= 0, "cupsSold", "must not be negative")
            .Check(tradingDate <= TradingCalendar.DateOf(now), "tradingDate", "is in the future")
            .ThrowIfAny();
}

/// <summary>
/// The sales records that exist for the days being written. It holds at most
/// one record per drink, branch and day, and refuses a second (BR-25).
/// </summary>
public sealed class SalesBook(IEnumerable<SalesRecord> existing)
{
    private readonly Dictionary<(long BranchId, long RecipeId, DateOnly TradingDate), SalesRecord> _records =
        existing.ToDictionary(record => (record.BranchId, record.RecipeId, record.TradingDate));

    public SalesRecord? Find(long branchId, long recipeId, DateOnly tradingDate) =>
        _records.GetValueOrDefault((branchId, recipeId, tradingDate));

    /// <summary>False, and nothing added, when the drink already has a record for that branch and day.</summary>
    public bool TryAdd(SalesRecord record) =>
        _records.TryAdd((record.BranchId, record.RecipeId, record.TradingDate), record);

    public void Add(SalesRecord record)
    {
        if (!TryAdd(record))
        {
            throw DomainException.RuleViolation("BR-25",
                $"A sales record already exists for this drink at this branch on {record.TradingDate:yyyy-MM-dd}. Correct that record instead.",
                ErrorCodes.ImportDuplicateDay, new ErrorDetail("tradingDate", "already has a sales record"));
        }
    }
}
