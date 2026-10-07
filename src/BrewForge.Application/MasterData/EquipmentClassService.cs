using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

public sealed record EquipmentClassDto(long Id, string EquipmentCode, string EquipmentClass,
    decimal MinThreshold, decimal MaxThreshold, DosingUnit DosingUnit, CatalogStatus Status)
{
    public static EquipmentClassDto From(StandardEquipment equipment) =>
        new(equipment.Id, equipment.EquipmentCode, equipment.EquipmentClass, equipment.MinThreshold,
            equipment.MaxThreshold, equipment.DosingUnit, equipment.Status);
}

/// <summary><c>Status</c> is optional on update and reactivates or deactivates the class.</summary>
public sealed record EquipmentClassRequest(string? EquipmentCode, string? EquipmentClass,
    decimal? MinThreshold, decimal? MaxThreshold, DosingUnit? DosingUnit, CatalogStatus? Status);

/// <summary>UC-03: the chain-wide standard equipment catalogue (SCR-05).</summary>
public sealed class EquipmentClassService(IBrewForgeDbContext db)
{
    private static readonly SortMap<StandardEquipment> Sorting =
        new SortMap<StandardEquipment>("equipmentCode", e => e.Id)
            .Add("id", e => e.Id)
            .Add("equipmentCode", e => e.EquipmentCode)
            .Add("equipmentClass", e => e.EquipmentClass)
            .Add("minThreshold", e => e.MinThreshold)
            .Add("maxThreshold", e => e.MaxThreshold)
            .Add("status", e => e.Status);

    public async Task<PagedResult<EquipmentClassDto>> ListAsync(PageQuery paging, string? keyword, string? status,
        CancellationToken cancellationToken)
    {
        var statusFilter = PagingExtensions.ParseFilter<CatalogStatus>(status, "status");

        var query = db.StandardEquipment.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(e => e.EquipmentCode.ToLower().Contains(term)
                                     || e.EquipmentClass.ToLower().Contains(term));
        }
        if (statusFilter is not null) query = query.Where(e => e.Status == statusFilter);

        return await query.ToPagedAsync(paging, Sorting, EquipmentClassDto.From, cancellationToken);
    }

    public async Task<EquipmentClassDto> GetAsync(long id, CancellationToken cancellationToken) =>
        EquipmentClassDto.From(await FindAsync(id, cancellationToken));

    public async Task<EquipmentClassDto> CreateAsync(EquipmentClassRequest request,
        CancellationToken cancellationToken)
    {
        RequireThresholds(request);
        var equipment = Domain.MasterData.StandardEquipment.Create(request.EquipmentCode!, request.EquipmentClass!,
            request.MinThreshold!.Value, request.MaxThreshold!.Value, request.DosingUnit!.Value);

        var clashes = new List<ErrorDetail>();
        if (await db.StandardEquipment.AnyAsync(e => e.EquipmentCode == equipment.EquipmentCode, cancellationToken))
        {
            clashes.Add(new ErrorDetail("equipmentCode", "already exists"));
        }
        if (await db.StandardEquipment.AnyAsync(e => e.EquipmentClass == equipment.EquipmentClass, cancellationToken))
        {
            clashes.Add(new ErrorDetail("equipmentClass", "already exists"));
        }
        if (clashes.Count > 0)
        {
            throw DomainException.RuleViolation("UNIQUE",
                "An equipment class with the same code or class name already exists.", details: [.. clashes]);
        }

        db.StandardEquipment.Add(equipment);
        db.Audit(AuditEntities.StandardEquipment, () => equipment.Id, AuditActions.Create,
            EquipmentClassDto.From(equipment));
        await db.SaveChangesAsync(cancellationToken);
        return EquipmentClassDto.From(equipment);
    }

    public async Task<EquipmentClassDto> UpdateAsync(long id, EquipmentClassRequest request,
        CancellationToken cancellationToken)
    {
        var equipment = await FindAsync(id, cancellationToken);
        MasterDataGuards.EnsureCodeUnchanged("equipmentCode", request.EquipmentCode, equipment.EquipmentCode);
        MasterDataGuards.EnsureCodeUnchanged("equipmentClass", request.EquipmentClass, equipment.EquipmentClass);
        RequireThresholds(request);

        equipment.Update(request.MinThreshold!.Value, request.MaxThreshold!.Value, request.DosingUnit!.Value);
        if (request.Status == CatalogStatus.Inactive) equipment.Deactivate();
        if (request.Status == CatalogStatus.Active) equipment.Reactivate();

        db.Audit(AuditEntities.StandardEquipment, () => equipment.Id, AuditActions.Update,
            EquipmentClassDto.From(equipment));
        await db.SaveChangesAsync(cancellationToken);
        return EquipmentClassDto.From(equipment);
    }

    /// <summary>An equipment class is deactivated, never deleted (BR-16).</summary>
    public async Task<EquipmentClassDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        var equipment = await FindAsync(id, cancellationToken);

        equipment.Deactivate();
        db.Audit(AuditEntities.StandardEquipment, () => equipment.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return EquipmentClassDto.From(equipment);
    }

    private static void RequireThresholds(EquipmentClassRequest request) =>
        new FieldErrors()
            .Check(request.MinThreshold is not null, "minThreshold", "is required")
            .Check(request.MaxThreshold is not null, "maxThreshold", "is required")
            .Check(request.DosingUnit is not null, "dosingUnit", "is required")
            .ThrowIfAny();

    private async Task<StandardEquipment> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.StandardEquipment.SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Equipment class", id);
}
