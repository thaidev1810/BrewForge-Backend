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

    /// <summary>Brings the role's permission set up to the current catalogue (G-06); the resource scope on
    /// the server is unchanged, this is only what the client is told the role can do.</summary>
    public void SyncPermissions(IEnumerable<string> permissions) => Permissions = [.. permissions];

    string INeverDeleted.RetentionRule => "BR-16";
}
