using System.Globalization;
using System.Security.Claims;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Security;

namespace BrewForge.Api.Auth;

/// <summary>
/// The caller as described by the validated access token. Outside a request
/// (startup, seeding) there is no caller and nothing is restricted.
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public long? UserId => ReadLong(BrewForgeClaims.Subject);

    public string? Username => Principal?.FindFirstValue(BrewForgeClaims.Username);

    public RoleName? Role =>
        EnumCode<RoleName>.TryParse(Principal?.FindFirstValue(BrewForgeClaims.Role), out var role) ? role : null;

    public long? BranchId => ReadLong(BrewForgeClaims.BranchId);

    public long? RestrictedToBranchId
    {
        get
        {
            if (!IsAuthenticated) return null;
            // An authenticated caller whose role cannot be read is treated as
            // store-level: the safe reading of a token we do not understand.
            var storeLevel = Role?.IsBranchScoped() ?? true;
            return storeLevel ? BranchId ?? CurrentUserExtensions.NoBranch : null;
        }
    }

    private long? ReadLong(string claimType) =>
        long.TryParse(Principal?.FindFirstValue(claimType), NumberStyles.None, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
}
