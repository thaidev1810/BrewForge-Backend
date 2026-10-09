using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;

namespace BrewForge.Application.Abstractions;

/// <summary>The authenticated caller, as carried by the access token.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    long? UserId { get; }
    string? Username { get; }
    RoleName? Role { get; }
    long? BranchId { get; }

    /// <summary>
    /// The branch a store-level caller is confined to, or null when the caller
    /// may see every branch. A store-level caller whose token carries no
    /// branch is confined to a branch that does not exist, so it sees nothing
    /// rather than everything.
    /// </summary>
    long? RestrictedToBranchId { get; }
}

public static class CurrentUserExtensions
{
    public const long NoBranch = -1;

    public static long RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw DomainException.Unauthenticated("Authentication is required.");
}
