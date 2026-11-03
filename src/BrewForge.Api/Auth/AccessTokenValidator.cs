using System.Security.Claims;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Auth;

/// <summary>
/// G-11: an access token is a short-lived snapshot, but a lock, a role or branch change, or a password
/// reset must take effect at once, not linger until the token expires. Every validated access token is
/// therefore checked against the live account on each request: if the user is gone, no longer active, or
/// carries a security version other than the token was minted with (any of those changes bumps it), the
/// token is rejected and the caller must sign in again.
/// </summary>
public static class AccessTokenValidator
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null || !long.TryParse(principal.FindFirstValue(BrewForgeClaims.Subject), out var userId)
            || !int.TryParse(principal.FindFirstValue(BrewForgeClaims.SecurityVersion), out var tokenVersion))
        {
            context.Fail("The access token is missing a subject or security version.");
            return;
        }

        // Read past the branch query filter: the acting user is not established yet, and the account is
        // looked up by its own id, not within anyone's branch scope.
        var db = context.HttpContext.RequestServices.GetRequiredService<IBrewForgeDbContext>();
        var account = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.SecurityVersion })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (account is null || account.Status != UserStatus.Active || account.SecurityVersion != tokenVersion)
        {
            context.Fail("The account is locked, gone, or its role, branch or password has changed; sign in again.");
        }
    }
}
