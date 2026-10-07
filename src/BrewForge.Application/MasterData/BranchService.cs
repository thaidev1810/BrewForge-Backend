using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

public sealed record BranchDto(long Id, string BranchCode, string Name, string? Address, BranchStatus Status)
{
    public static BranchDto From(Branch branch) =>
        new(branch.Id, branch.BranchCode, branch.Name, branch.Address, branch.Status);
}

/// <summary><c>Status</c> is optional on update and reopens or closes the branch.</summary>
public sealed record BranchRequest(string? BranchCode, string? Name, string? Address, BranchStatus? Status);

/// <summary>UC-04: the branch registry (SCR-06).</summary>
public sealed class BranchService(IBrewForgeDbContext db)
{
    private static readonly SortMap<Branch> Sorting = new SortMap<Branch>("branchCode", b => b.Id)
        .Add("id", b => b.Id)
        .Add("branchCode", b => b.BranchCode)
        .Add("name", b => b.Name)
        .Add("status", b => b.Status);

    /// <summary>
    /// A store-level caller receives only its own branch: the restriction is a
    /// query filter of the persistence layer, not a condition written here.
    /// </summary>
    public async Task<PagedResult<BranchDto>> ListAsync(PageQuery paging, string? keyword, string? status,
        CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<BranchStatus>(status, "status");

        var query = db.Branches.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(b => b.BranchCode.ToLower().Contains(term) || b.Name.ToLower().Contains(term));
        }
        if (statusFilter is not null) query = query.Where(b => b.Status == statusFilter);

        return await query.ToPagedAsync(paging, Sorting, BranchDto.From, cancellationToken);
    }

    public async Task<BranchDto> GetAsync(long id, CancellationToken cancellationToken) =>
        BranchDto.From(await FindAsync(id, cancellationToken));

    public async Task<BranchDto> CreateAsync(BranchRequest request, CancellationToken cancellationToken)
    {
        var branch = Branch.Create(request.BranchCode!, request.Name!, request.Address);

        if (await db.Branches.IgnoreQueryFilters().AnyAsync(b => b.BranchCode == branch.BranchCode, cancellationToken))
        {
            throw DomainException.RuleViolation("UNIQUE", $"Branch code '{branch.BranchCode}' already exists.",
                details: new ErrorDetail("branchCode", "already exists"));
        }

        db.Branches.Add(branch);
        db.Audit(AuditEntities.Branch, () => branch.Id, AuditActions.Create, BranchDto.From(branch));
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    public async Task<BranchDto> UpdateAsync(long id, BranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        MasterDataGuards.EnsureCodeUnchanged("branchCode", request.BranchCode, branch.BranchCode);

        branch.Update(request.Name!, request.Address);
        if (request.Status == BranchStatus.Closed) branch.Deactivate();
        if (request.Status == BranchStatus.Active) branch.Reactivate();

        db.Audit(AuditEntities.Branch, () => branch.Id, AuditActions.Update, BranchDto.From(branch));
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    /// <summary>A branch is closed, never deleted (BR-16).</summary>
    public async Task<BranchDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);

        branch.Deactivate();
        db.Audit(AuditEntities.Branch, () => branch.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    private async Task<Branch> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.Branches.SingleOrDefaultAsync(b => b.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Branch", id);
}

internal static class MasterDataGuards
{
    /// <summary>
    /// Natural keys are what recipes, the POS export and the LLM prompt refer
    /// to, so they are fixed once the record exists.
    /// </summary>
    public static void EnsureCodeUnchanged(string field, string? requested, string current)
    {
        if (requested is not null && !string.Equals(requested.Trim(), current, StringComparison.Ordinal))
        {
            throw DomainException.Validation("The code of an existing record cannot be changed.",
                new ErrorDetail(field, "cannot be changed after creation"));
        }
    }
}
