namespace BrewForge.Domain.Inventory;

/// <summary>
/// A quantity of an ingredient received into a branch's store on a day. Quantities are kept in the
/// ingredient's own unit (the service converts what was entered), so a loss report can add them up
/// without converting again.
/// </summary>
public sealed class StockReceipt
{
    private StockReceipt() { }

    public StockReceipt(long branchId, long ingredientId, decimal quantity, DateOnly receivedOn, long recordedBy,
        DateTimeOffset createdAt)
    {
        BranchId = branchId;
        IngredientId = ingredientId;
        Quantity = quantity;
        ReceivedOn = receivedOn;
        RecordedBy = recordedBy;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }
    public long BranchId { get; private set; }
    public long IngredientId { get; private set; }

    /// <summary>In the ingredient's own unit.</summary>
    public decimal Quantity { get; private set; }
    public DateOnly ReceivedOn { get; private set; }
    public long RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// A physical count of how much of an ingredient a branch had on hand on a day. Two counts bracket a
/// period: the stock used between them is the opening count plus what was received minus the closing
/// count. Quantities are in the ingredient's own unit.
/// </summary>
public sealed class StockCount
{
    private StockCount() { }

    public StockCount(long branchId, long ingredientId, decimal quantity, DateOnly countedOn, long recordedBy,
        DateTimeOffset createdAt)
    {
        BranchId = branchId;
        IngredientId = ingredientId;
        Quantity = quantity;
        CountedOn = countedOn;
        RecordedBy = recordedBy;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }
    public long BranchId { get; private set; }
    public long IngredientId { get; private set; }

    /// <summary>On-hand quantity, in the ingredient's own unit.</summary>
    public decimal Quantity { get; private set; }
    public DateOnly CountedOn { get; private set; }
    public long RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// The rule for reading a stock loss: how much the measured use may stray from what the recipes say
/// the sales should have used before it is worth looking into (over-pouring, remakes, spoilage, theft).
/// </summary>
public static class StockLossPolicy
{
    /// <summary>A variance beyond this share of the expected use, either way, is flagged for review.</summary>
    public const decimal VarianceThresholdPct = 5m;

    public static bool IsFlagged(decimal? variancePct) =>
        variancePct is { } value && Math.Abs(value) > VarianceThresholdPct;
}
