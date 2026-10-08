using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Launch;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Sales;

public sealed record SalesRecordDto(long Id, long BranchId, string? BranchCode, long RecipeId, string? RecipeCode,
    string? RecipeName, long? RecipeVersionId, int? VersionNo, DateOnly TradingDate, int CupsSold, SalesSource Source,
    long RecordedBy, DateTimeOffset RecordedAt);

/// <summary>
/// The sales record of the API contract. There is no <c>recipeVersionId</c>
/// here on purpose: the server resolves it (BR-24), and a client that sends
/// one is not listened to.
/// </summary>
public sealed record SalesRecordRequest(long? BranchId, long? RecipeId, DateOnly? TradingDate, int? CupsSold);

public sealed record SalesCorrectionRequest(int? CupsSold);

/// <summary>A drink that was on sale at the branch on the day asked about.</summary>
public sealed record SellableDrinkDto(long RecipeId, string RecipeCode, string Name, RecipeCategory Category,
    long RecipeVersionId, int? VersionNo, bool CoverageMet);

/// <summary>UC-22: daily sales entered by hand (SCR-26).</summary>
public sealed class SalesService(IBrewForgeDbContext db, LaunchHistory launchHistory, ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly SortMap<SalesRecord> Sorting = new SortMap<SalesRecord>("tradingDate,desc", s => s.Id)
        .Add("id", s => s.Id)
        .Add("tradingDate", s => s.TradingDate)
        .Add("cupsSold", s => s.CupsSold)
        .Add("recordedAt", s => s.RecordedAt);

    public async Task<PagedResult<SalesRecordDto>> ListAsync(PageQuery paging, long? branchId, long? recipeId,
        DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.SalesRecords.AsNoTracking().AsQueryable();
        if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
        if (recipeId is not null) query = query.Where(s => s.RecipeId == recipeId);
        if (from is not null) query = query.Where(s => s.TradingDate >= from);
        if (to is not null) query = query.Where(s => s.TradingDate <= to);

        var page = await query.ToPagedAsync(paging, Sorting, s => s, cancellationToken);
        return new PagedResult<SalesRecordDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size,
            page.Total);
    }

    /// <summary>
    /// The drinks a sale can be entered for: those on sale at the branch on
    /// that day, each with the version the cups will be attached to.
    /// </summary>
    public async Task<IReadOnlyList<SellableDrinkDto>> SellableDrinksAsync(long? branchId, DateOnly? tradingDate,
        CancellationToken cancellationToken)
    {
        var branch = branchId ?? currentUser.BranchId
                     ?? throw DomainException.Validation("A branch is required.", new ErrorDetail("branchId", "is required"));
        EnsureOwnBranch(branch);
        var today = TradingCalendar.DateOf(clock.GetUtcNow());
        var date = tradingDate ?? today;
        if (date > today) return [];

        var launches = await db.BranchLaunchStatuses.AsNoTracking()
            .Where(l => l.BranchId == branch && (l.Status == LaunchStatus.Live || l.Status == LaunchStatus.Withdrawn))
            .ToListAsync(cancellationToken);
        var history = await launchHistory.LoadAsync([.. launches.Select(l => l.Id)], cancellationToken);

        var onSale = new List<(BranchLaunchStatus Launch, long VersionId)>();
        foreach (var launch in launches)
        {
            try
            {
                onSale.Add((launch, launch.VersionSoldOn(date, LaunchHistory.Of(history, launch.Id))));
            }
            catch (DomainException refusal) when (refusal.Rule == "BR-24")
            {
                // Not on sale that day: not on the list.
            }
        }

        var recipeIds = onSale.Select(x => x.Launch.RecipeId).ToList();
        var versionIds = onSale.Select(x => x.VersionId).ToList();
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        var versionNos = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);

        return
        [
            .. onSale.Select(x =>
            {
                var recipe = recipes[x.Launch.RecipeId];
                return new SellableDrinkDto(recipe.Id, recipe.RecipeCode, recipe.Name, recipe.Category, x.VersionId,
                    versionNos.GetValueOrDefault(x.VersionId), x.Launch.CoverageMet);
            }).OrderBy(drink => drink.RecipeCode),
        ];
    }

    /// <summary>UC-22. 409 BR-24 if the branch was not live that day, 409 BR-25 if the day already has a record.</summary>
    public async Task<SalesRecordDto> CreateAsync(SalesRecordRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(request.BranchId is not null, "branchId", "is required")
            .Check(request.RecipeId is not null, "recipeId", "is required")
            .Check(request.TradingDate is not null, "tradingDate", "is required")
            .Check(request.CupsSold is not null, "cupsSold", "is required")
            .ThrowIfAny();
        var (branchId, recipeId, tradingDate) = (request.BranchId!.Value, request.RecipeId!.Value, request.TradingDate!.Value);
        EnsureOwnBranch(branchId);
        if (!await db.Recipes.AnyAsync(r => r.Id == recipeId, cancellationToken))
        {
            throw DomainException.NotFound("Recipe", recipeId);
        }

        // No launch status at all is the same answer as one that was not live that day.
        var launch = await db.BranchLaunchStatuses.AsNoTracking()
                         .SingleOrDefaultAsync(l => l.BranchId == branchId && l.RecipeId == recipeId, cancellationToken)
                     ?? throw BranchLaunchStatus.NotLiveOn(tradingDate);
        var history = await launchHistory.LoadAsync([launch.Id], cancellationToken);

        var record = SalesRecord.Record(launch, LaunchHistory.Of(history, launch.Id), tradingDate,
            request.CupsSold!.Value, SalesSource.Manual, currentUser.RequireUserId(), clock.GetUtcNow());
        var book = new SalesBook(await db.SalesRecords.AsNoTracking()
            .Where(s => s.BranchId == branchId && s.RecipeId == recipeId && s.TradingDate == tradingDate)
            .ToListAsync(cancellationToken));
        book.Add(record);

        db.SalesRecords.Add(record);
        db.Audit(AuditEntities.SalesRecord, () => record.Id, AuditActions.Create, new
        {
            record.BranchId, record.RecipeId, record.RecipeVersionId, record.TradingDate, record.CupsSold,
            source = record.Source.Code(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([record], cancellationToken))[0];
    }

    /// <summary>A correction of the count. Every correction is written to the audit log with the old figure.</summary>
    public async Task<SalesRecordDto> CorrectAsync(long id, SalesCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.CupsSold is not null, "cupsSold", "is required").ThrowIfAny();
        var record = await db.SalesRecords.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Sales record", id);
        EnsureOwnBranch(record.BranchId);

        var before = record.CupsSold;
        if (record.Correct(request.CupsSold!.Value, currentUser.RequireUserId(), clock.GetUtcNow()))
        {
            db.Audit(AuditEntities.SalesRecord, () => record.Id, AuditActions.CorrectSales,
                new { record.TradingDate, from = before, to = record.CupsSold });
            await db.SaveChangesAsync(cancellationToken);
        }
        return (await ToDtosAsync([record], cancellationToken))[0];
    }

    /// <summary>A store-level caller records sales for its own branch and no other.</summary>
    private void EnsureOwnBranch(long branchId)
    {
        if (currentUser.RestrictedToBranchId is { } own && own != branchId)
        {
            throw DomainException.Forbidden(message: "Sales are recorded for your own branch only.");
        }
    }

    private async Task<IReadOnlyList<SalesRecordDto>> ToDtosAsync(IReadOnlyList<SalesRecord> records,
        CancellationToken cancellationToken)
    {
        var branchIds = records.Select(r => r.BranchId).Distinct().ToList();
        var recipeIds = records.Select(r => r.RecipeId).Distinct().ToList();
        var versionIds = records.Select(r => r.RecipeVersionId).OfType<long>().Distinct().ToList();

        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .Select(r => new { r.Id, r.RecipeCode, r.Name }).ToDictionaryAsync(r => r.Id, cancellationToken);
        var versionNos = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);

        return
        [
            .. records.Select(r =>
            {
                var recipe = recipes.GetValueOrDefault(r.RecipeId);
                return new SalesRecordDto(r.Id, r.BranchId, branches.GetValueOrDefault(r.BranchId), r.RecipeId,
                    recipe?.RecipeCode, recipe?.Name, r.RecipeVersionId,
                    r.RecipeVersionId is { } versionId && versionNos.TryGetValue(versionId, out var no) ? no : null,
                    r.TradingDate, r.CupsSold, r.Source, r.RecordedBy, r.RecordedAt);
            }),
        ];
    }
}
