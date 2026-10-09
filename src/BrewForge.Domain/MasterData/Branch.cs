using BrewForge.Domain.Common;

namespace BrewForge.Domain.MasterData;

public enum BranchStatus
{
    Active,
    Closed,
}

/// <summary>A physical outlet of the chain.</summary>
public sealed class Branch : INeverDeleted
{
    private Branch() { }

    public long Id { get; private set; }

    /// <summary>Unique; also the code used in the POS export file.</summary>
    public string BranchCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Address { get; private set; }
    public BranchStatus Status { get; private set; } = BranchStatus.Active;

    string INeverDeleted.RetentionRule => "BR-16";

    public static Branch Create(string branchCode, string name, string? address)
    {
        branchCode = branchCode?.Trim() ?? "";
        new FieldErrors().RequiredMax("branchCode", branchCode, 16).ThrowIfAny();

        var branch = new Branch { BranchCode = branchCode };
        branch.Update(name, address);
        return branch;
    }

    public void Update(string name, string? address)
    {
        name = name?.Trim() ?? "";
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        new FieldErrors()
            .RequiredMax("name", name, 120)
            .MaxLength("address", address, 255)
            .ThrowIfAny();

        Name = name;
        Address = address;
    }

    /// <summary>A branch is never deleted, only closed (BR-16).</summary>
    public void Deactivate() => Status = BranchStatus.Closed;

    public void Reactivate() => Status = BranchStatus.Active;
}
