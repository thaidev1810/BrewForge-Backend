using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Inventory;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Inventory;

/// <summary><c>{ ingredientId, quantity, unit, receivedOn }</c>.</summary>
public sealed record StockReceiptRequest(long? IngredientId, decimal? Quantity, string? Unit, DateOnly? ReceivedOn);

/// <summary><c>{ ingredientId, quantity, unit, countedOn }</c>.</summary>
public sealed record StockCountRequest(long? IngredientId, decimal? Quantity, string? Unit, DateOnly? CountedOn);

public sealed record StockMovementDto(long Id, long BranchId, long IngredientId, string IngredientCode,
    decimal Quantity, string Unit, DateOnly Date);

/// <summary>
/// One ingredient's stock loss between its two counts: what was used against what the recipes expected.
/// <c>OpeningCountedOn</c>/<c>ClosingCountedOn</c> are the days of those counts, the window actually measured.
/// </summary>
public sealed record StockLossLineDto(long IngredientId, string IngredientCode, string Unit, decimal OpeningStock,
    decimal Received, decimal ClosingStock, decimal ActualUsed, decimal TheoreticalUsed, decimal Loss,
    decimal? VariancePct, bool Flagged, DateOnly OpeningCountedOn, DateOnly ClosingCountedOn);

public sealed record StockLossReportDto(long BranchId, string? BranchCode, DateOnly From, DateOnly To,
    decimal VarianceThresholdPct, IReadOnlyList<StockLossLineDto> Lines);

/// <summary>
/// Periodic stock loss control. A branch records what it receives and counts what it has on hand; the
/// loss report compares the stock actually used between two counts (opening + received - closing) with
/// what the recipes say the sales should have used. A store-level caller works within its own branch.
/// </summary>
public sealed class StockService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public Task<StockMovementDto> RecordReceiptAsync(StockReceiptRequest request, CancellationToken cancellationToken) =>
        RecordAsync(request.IngredientId, request.Quantity, request.Unit, request.ReceivedOn, "receivedOn",
            quantityMustBePositive: true, cancellationToken);

    public Task<StockMovementDto> RecordCountAsync(StockCountRequest request, CancellationToken cancellationToken) =>
        RecordAsync(request.IngredientId, request.Quantity, request.Unit, request.CountedOn, "countedOn",
            quantityMustBePositive: false, cancellationToken);

    private async Task<StockMovementDto> RecordAsync(long? ingredientId, decimal? quantity, string? unit,
        DateOnly? date, string dateField, bool quantityMustBePositive, CancellationToken cancellationToken)
    {
        var branchId = OwnBranchOrThrow();
        new FieldErrors()
            .Check(ingredientId is not null, "ingredientId", "is required")
            .Check(quantity is not null, "quantity", "is required")
            .Check(quantity is null || (quantityMustBePositive ? quantity > 0 : quantity >= 0), "quantity",
                quantityMustBePositive ? "must be greater than zero" : "must not be negative")
            .Check(!string.IsNullOrWhiteSpace(unit), "unit", "is required")
            .Check(date is not null, dateField, "is required")
            .ThrowIfAny();

        var ingredient = await db.Ingredients.AsNoTracking().SingleOrDefaultAsync(i => i.Id == ingredientId!.Value, cancellationToken)
            ?? throw DomainException.NotFound("Ingredient", ingredientId!.Value);
        var ingredientUnit = ingredient.Unit.Code();
        if (!UnitConverter.TryConvert(quantity!.Value, unit!.Trim(), ingredientUnit, out var inIngredientUnit))
        {
            throw DomainException.Validation($"The unit '{unit}' cannot be expressed in '{ingredientUnit}'.",
                new ErrorDetail("unit", $"must be convertible to {ingredientUnit}, the unit of {ingredient.IngredientCode}"));
        }
        if (date!.Value > Today)
        {
            throw DomainException.Validation($"The {dateField} is in the future.",
                new ErrorDetail(dateField, "must not be in the future"));
        }

        var userId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        long id;
        if (quantityMustBePositive)
        {
            var receipt = new StockReceipt(branchId, ingredient.Id, inIngredientUnit, date.Value, userId, now);
            db.StockReceipts.Add(receipt);
            db.Audit(AuditEntities.StockReceipt, () => receipt.Id, AuditActions.Create,
                new { branchId, ingredient.IngredientCode, quantity = inIngredientUnit, unit = ingredientUnit, date.Value });
            await db.SaveChangesAsync(cancellationToken);
            id = receipt.Id;
        }
        else
        {
            var count = new StockCount(branchId, ingredient.Id, inIngredientUnit, date.Value, userId, now);
            db.StockCounts.Add(count);
            db.Audit(AuditEntities.StockCount, () => count.Id, AuditActions.Create,
                new { branchId, ingredient.IngredientCode, quantity = inIngredientUnit, unit = ingredientUnit, date.Value });
            await db.SaveChangesAsync(cancellationToken);
            id = count.Id;
        }

        return new StockMovementDto(id, branchId, ingredient.Id, ingredient.IngredientCode, inIngredientUnit,
            ingredientUnit, date.Value);
    }

    /// <summary>
    /// The loss of each ingredient over the period: its opening count is the latest on or before
    /// <paramref name="from"/>, its closing count the latest on or before <paramref name="to"/>, and the
    /// receipts and sales are those between the two counts themselves. Stock used (opening + received -
    /// closing) is set against what the recipes say those sales should have used. An ingredient whose two
    /// counts are the same count has nothing to measure and is left out.
    /// </summary>
    public async Task<StockLossReportDto> LossReportAsync(long? branchId, DateOnly? from, DateOnly? to,
        CancellationToken cancellationToken)
    {
        var scopedBranch = currentUser.RestrictedToBranchId ?? branchId
            ?? throw DomainException.Validation("A branch is required.", new ErrorDetail("branchId", "is required"));
        new FieldErrors()
            .Check(from is not null, "from", "is required")
            .Check(to is not null, "to", "is required")
            .Check(from is null || to is null || from <= to, "to", "must not be before 'from'")
            .ThrowIfAny();

        var opening = await CountsOnAsync(scopedBranch, from!.Value, cancellationToken);
        var closing = await CountsOnAsync(scopedBranch, to!.Value, cancellationToken);

        // Measurable: counted at both ends, by two different counts. Each ingredient is measured over its own
        // window (opening count day, closing count day]: a receipt or a sale between the opening count and
        // `from` belongs to the period, or the opening stock would be set against the wrong movements.
        var windows = opening.Keys.Intersect(closing.Keys)
            .Where(id => closing[id].CountedOn > opening[id].CountedOn)
            .ToDictionary(id => id, id => (Open: opening[id].CountedOn, Close: closing[id].CountedOn));
        var lines = new List<StockLossLineDto>();
        var branchCode = await db.Branches.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == scopedBranch).Select(b => b.BranchCode).FirstOrDefaultAsync(cancellationToken);

        if (windows.Count > 0)
        {
            var earliest = windows.Values.Min(w => w.Open);
            var latest = windows.Values.Max(w => w.Close);
            var ingredientIds = windows.Keys.ToList();
            var receipts = await db.StockReceipts.AsNoTracking()
                .Where(r => r.BranchId == scopedBranch && ingredientIds.Contains(r.IngredientId)
                            && r.ReceivedOn > earliest && r.ReceivedOn <= latest)
                .Select(r => new { r.IngredientId, r.ReceivedOn, r.Quantity })
                .ToListAsync(cancellationToken);
            var theoretical = await TheoreticalUseAsync(scopedBranch, earliest, latest, cancellationToken);
            var ingredients = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, cancellationToken);

            foreach (var (id, window) in windows)
            {
                if (!ingredients.TryGetValue(id, out var ingredient)) continue;
                bool InWindow(DateOnly day) => day > window.Open && day <= window.Close;
                var open = opening[id].Quantity;
                var close = closing[id].Quantity;
                var recv = receipts.Where(r => r.IngredientId == id && InWindow(r.ReceivedOn)).Sum(r => r.Quantity);
                var expected = theoretical.Where(t => t.IngredientId == id && InWindow(t.TradingDate)).Sum(t => t.Quantity);
                var actual = open + recv - close;
                var loss = actual - expected;
                decimal? variancePct = expected > 0 ? Math.Round(loss / expected * 100m, 2) : null;
                lines.Add(new StockLossLineDto(id, ingredient.IngredientCode, ingredient.Unit.Code(), open, recv, close,
                    actual, expected, loss, variancePct, StockLossPolicy.IsFlagged(variancePct), window.Open, window.Close));
            }
        }
        lines = [.. lines.OrderByDescending(line => line.VariancePct ?? decimal.MinValue)];

        return new StockLossReportDto(scopedBranch, branchCode, from.Value, to.Value,
            StockLossPolicy.VarianceThresholdPct, lines);
    }

    /// <summary>The latest count of each ingredient on or before a day: its on-hand quantity and the day it was counted.</summary>
    private async Task<Dictionary<long, (decimal Quantity, DateOnly CountedOn)>> CountsOnAsync(long branchId, DateOnly on,
        CancellationToken cancellationToken) =>
        (await db.StockCounts.AsNoTracking()
            .Where(c => c.BranchId == branchId && c.CountedOn <= on)
            .ToListAsync(cancellationToken))
        .GroupBy(c => c.IngredientId)
        .ToDictionary(g => g.Key, g =>
        {
            var latest = g.OrderByDescending(c => c.CountedOn).ThenByDescending(c => c.Id).First();
            return (latest.Quantity, latest.CountedOn);
        });

    /// <summary>
    /// What the recipes say the sales in (<paramref name="from"/>, <paramref name="to"/>] should have used, per
    /// ingredient and trading day, in the ingredient's own unit; the caller narrows it to each ingredient's window.
    /// </summary>
    private async Task<List<(long IngredientId, DateOnly TradingDate, decimal Quantity)>> TheoreticalUseAsync(long branchId,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var sales = await db.SalesRecords.AsNoTracking()
            .Where(s => s.BranchId == branchId && s.RecipeVersionId != null
                        && s.TradingDate > from && s.TradingDate <= to)
            .Select(s => new { VersionId = s.RecipeVersionId!.Value, s.TradingDate, s.CupsSold })
            .ToListAsync(cancellationToken);
        if (sales.Count == 0) return [];

        var versionIds = sales.Select(s => s.VersionId).Distinct().ToList();
        var versions = await db.RecipeVersions.AsNoTracking().Include(v => v.Steps).ThenInclude(s => s.Ingredients)
            .Where(v => versionIds.Contains(v.Id)).ToListAsync(cancellationToken);
        var useByVersion = versions.ToDictionary(v => v.Id, v => v.Steps.SelectMany(s => s.Ingredients)
            .GroupBy(i => i.IngredientId)
            .Select(g => (IngredientId: g.Key, Uses: g.Select(i => (i.Quantity, i.Unit)).ToList())).ToList());

        var ingredientIds = versions.SelectMany(v => v.Steps).SelectMany(s => s.Ingredients)
            .Select(i => i.IngredientId).Distinct().ToList();
        var unitOf = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Unit.Code(), cancellationToken);

        var uses = new List<(long IngredientId, DateOnly TradingDate, decimal Quantity)>();
        foreach (var sale in sales)
        {
            if (!useByVersion.TryGetValue(sale.VersionId, out var perIngredient)) continue;
            foreach (var (ingredientId, perCup) in perIngredient)
            {
                if (!unitOf.TryGetValue(ingredientId, out var target)) continue;
                var perCupInUnit = perCup.Sum(u => UnitConverter.TryConvert(u.Quantity, u.Unit, target, out var c) ? c : 0m);
                uses.Add((ingredientId, sale.TradingDate, perCupInUnit * sale.CupsSold));
            }
        }
        return uses;
    }

    private long OwnBranchOrThrow() => currentUser.RestrictedToBranchId
        ?? throw DomainException.Forbidden(message: "Only a branch manager records the stock of their branch.");

    // The chain trades in Vietnam: "today" is the trading day there, not in UTC, as for sales.
    private DateOnly Today => TradingCalendar.DateOf(clock.GetUtcNow());
}
