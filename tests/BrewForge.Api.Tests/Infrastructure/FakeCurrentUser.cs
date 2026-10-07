using BrewForge.Application.Abstractions;
using BrewForge.Domain.Identity;

namespace BrewForge.Api.Tests.Infrastructure;

public sealed class FakeCurrentUser(long userId, RoleName role, long? branchId) : ICurrentUser
{
    public bool IsAuthenticated => true;
    public long? UserId => userId;
    public string? Username => $"user{userId}";
    public RoleName? Role => role;
    public long? BranchId => branchId;
    public long? RestrictedToBranchId => role.IsBranchScoped() ? branchId ?? CurrentUserExtensions.NoBranch : null;
}
