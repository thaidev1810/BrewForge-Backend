using System.Security.Claims;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Auth;

/// <summary>
/// G-11: an access token is a short-lived snapshot, but a lock or a role change must take effect at
/// once, not linger until the token expires. Every validated access token is therefore checked against
/// the live account on each request: if the user is gone, no longer active, or now holds a different
/// role than the token was minted with, the token is rejected and the caller must sign in again.
/// </summary>
public static class AccessTokenValidator
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null || !long.TryParse(principal.FindFirstValue(BrewForgeClaims.Subject), out var userId))
        {
            context.Fail("The access token has no subject.");
            return;
        }
        var tokenRole = principal.FindFirstValue(BrewForgeClaims.Role);

        // Read past the branch query filter: the acting user is not established yet, and the account is
        // looked up by its own id, not within anyone's branch scope.
        var db = context.HttpContext.RequestServices.GetRequiredService<IBrewForgeDbContext>();
        var account = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, Role = u.Role.RoleName })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (account is null || account.Status != UserStatus.Active || account.Role.Code() != tokenRole)
        {
            context.Fail("The account is locked, gone, or its role has changed; sign in again.");
        }
    }
}
