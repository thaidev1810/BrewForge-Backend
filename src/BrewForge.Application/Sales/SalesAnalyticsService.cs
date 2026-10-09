using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Sales;

/// <summary>
/// One group of an aggregate: a branch, a day or an ISO week.
/// <c>ChangePct</c> is the trend against the group before it (days and weeks
/// only); <c>ControlCups</c> and <c>PercentOfControl</c> compare the group
/// with the control drink over the same branches and days.
/// </summary>
public sealed record SalesGroupDto(string Key, long? BranchId, string? BranchCode, DateOnly From, DateOnly To,
    int Cups, int TradingDays, decimal CupsPerDay, decimal? ChangePct, int? ControlCups, decimal? PercentOfControl);

public sealed record SalesAggregateDto(long RecipeId, string RecipeCode, string RecipeName, long? RecipeVersionId,
    SalesGrouping GroupBy, DateOnly? From, DateOnly? To, int TotalCups, int TradingDays, decimal CupsPerDay,
    long? ControlRecipeId, string? ControlRecipeCode, int? ControlCups, decimal? PercentOfControl,
    IReadOnlyList<SalesGroupDto> Groups);

/// <summary>Sales of a drink per branch, per day and per ISO week, with trend and comparison.</summary>
public sealed class SalesAnalyticsService(IBrewForgeDbContext db)
{
    /// <summary>
    /// <c>?recipeVersionId=&amp;groupBy=branch|day|week</c>. A version narrows
    /// the figures to the cups sold on that version; <c>recipeId</c> takes
    /// every version of the drink. Optional: a branch, a period, and a
    /// control drink to compare with.
    /// </summary>
    public async Task<SalesAggregateDto> AggregateAsync(long? recipeVersionId, long? recipeId, string? groupBy,
        long? branchId, DateOnly? from, DateOnly? to, long? controlRecipeId, CancellationToken cancellationToken)
    {
        var grouping = PagingExtensions.ParseFilter<SalesGrouping>(groupBy?.ToUpperInvariant(), "groupBy")
                       ?? SalesGrouping.Week;
        new FieldErrors()
            .Check(recipeVersionId is not null || recipeId is not null, "recipeVersionId", "recipeVersionId or recipeId is required")
            .Check(from is null || to is null || from <= to, "from", "must not be after 'to'")
            .ThrowIfAny();

        if (recipeVersionId is not null)
        {
            var ofVersion = await db.RecipeVersions.AsNoTracking().Where(v => v.Id == recipeVersionId)
                .Select(v => (long?)v.RecipeId).SingleOrDefaultAsync(cancellationToken)
                ?? throw DomainException.NotFound("Recipe version", recipeVersionId);
            if (recipeId is not null && recipeId != ofVersion)
            {
                throw DomainException.Validation("The version does not belong to that recipe.",
                    new ErrorDetail("recipeVersionId", "is not a version of recipeId"));
            }
            recipeId = ofVersion;
        }
        var recipe = await FindRecipeAsync(recipeId!.Value, cancellationToken);
        var control = controlRecipeId is null ? null : await FindRecipeAsync(controlRecipeId.Value, cancellationToken);

        var sales = await LoadAsync(recipe.Id, recipeVersionId, branchId, from, to, cancellationToken);
        var buckets = SalesAggregator.Group(sales, grouping);

        // The control is measured where and when the drink was sold: the same branches, the same days.
        IReadOnlyList<DailyCups>? controlSales = null;
        Dictionary<string, int>? controlByKey = null;
        if (control is not null && sales.Count > 0)
        {
            var soldOn = sales.Select(day => (day.BranchId, day.TradingDate)).ToHashSet();
            controlSales =
            [
                .. (await LoadAsync(control.Id, null, branchId, sales.Min(d => d.TradingDate), sales.Max(d => d.TradingDate),
                    cancellationToken)).Where(day => soldOn.Contains((day.BranchId, day.TradingDate))),
            ];
            controlByKey = SalesAggregator.Group(controlSales, grouping).ToDictionary(b => b.Key, b => b.Cups);
        }
        else if (control is not null)
        {
            controlSales = [];
            controlByKey = [];
        }

        var branchCodes = grouping == SalesGrouping.Branch
            ? await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken)
            : [];
        var groups = new List<SalesGroupDto>();
        SalesBucket? previous = null;
        foreach (var bucket in buckets)
        {
            int? controlCups = controlByKey is null ? null : controlByKey.GetValueOrDefault(bucket.Key);
            groups.Add(new SalesGroupDto(bucket.Key, bucket.BranchId,
                bucket.BranchId is { } id ? branchCodes.GetValueOrDefault(id) : null, bucket.From, bucket.To,
                bucket.Cups, bucket.TradingDays, bucket.CupsPerDay,
                grouping != SalesGrouping.Branch && previous is not null
                    ? SalesAggregator.ChangePercent(previous.Cups, bucket.Cups)
                    : null,
                controlCups, controlCups is { } cups ? SalesAggregator.PercentOf(bucket.Cups, cups) : null));
            previous = bucket;
        }

        var total = sales.Sum(day => day.Cups);
        DateOnly? firstDay = sales.Count > 0 ? sales.Min(day => day.TradingDate) : null;
        DateOnly? lastDay = sales.Count > 0 ? sales.Max(day => day.TradingDate) : null;
        var controlTotal = controlSales?.Sum(day => day.Cups);
        return new SalesAggregateDto(recipe.Id, recipe.RecipeCode, recipe.Name, recipeVersionId, grouping,
            from ?? firstDay, to ?? lastDay, total, sales.Count, SalesAggregator.Average(total, sales.Count),
            control?.Id, control?.RecipeCode, controlTotal, controlTotal is { } all ? SalesAggregator.PercentOf(total, all) : null, groups);
    }

    private async Task<IReadOnlyList<DailyCups>> LoadAsync(long recipeId, long? recipeVersionId, long? branchId,
        DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.SalesRecords.AsNoTracking().Where(s => s.RecipeId == recipeId);
        if (recipeVersionId is not null) query = query.Where(s => s.RecipeVersionId == recipeVersionId);
        if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
        if (from is not null) query = query.Where(s => s.TradingDate >= from);
        if (to is not null) query = query.Where(s => s.TradingDate <= to);

        return await query.Select(s => new DailyCups(s.BranchId, s.TradingDate, s.CupsSold)).ToListAsync(cancellationToken);
    }

    private async Task<RecipeFacts> FindRecipeAsync(long id, CancellationToken cancellationToken) =>
        await db.Recipes.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new RecipeFacts(r.Id, r.RecipeCode, r.Name)).SingleOrDefaultAsync(cancellationToken)
        ?? throw DomainException.NotFound("Recipe", id);

    private sealed record RecipeFacts(long Id, string RecipeCode, string Name);
}
