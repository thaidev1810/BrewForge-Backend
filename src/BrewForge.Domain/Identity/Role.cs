using BrewForge.Domain.Common;

namespace BrewForge.Domain.Identity;

/// <summary>A role group with its permission set.</summary>
public sealed class Role : INeverDeleted
{
    private Role() { }

    public Role(RoleName roleName, IEnumerable<string> permissions)
    {
        RoleName = roleName;
        Permissions = [.. permissions];
    }

    public long Id { get; private set; }
    public RoleName RoleName { get; private set; }
    public List<string> Permissions { get; private set; } = [];

    string INeverDeleted.RetentionRule => "BR-16";
}
