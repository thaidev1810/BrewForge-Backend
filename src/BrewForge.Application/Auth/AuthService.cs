using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Users;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record LoginResponse(string AccessToken, string RefreshToken, UserDto User);

public sealed record MeResponse(long Id, string Username, string Email, string FullName, RoleName Role,
    long? BranchId, UserStatus Status, IReadOnlyList<string> Permissions);

/// <summary>A role and the permission codes it carries, for the client to build menus and role screens (G-06).</summary>
public sealed record RoleDto(RoleName Role, IReadOnlyList<string> Permissions);

/// <summary>SF-02: login, token refresh with rotation, logout and the caller's own profile.</summary>
public sealed class AuthService(IBrewForgeDbContext db, IPasswordHasher hasher, ITokenService tokens,
    ICurrentUser currentUser, TimeProvider clock)
{
    // Verified against when the username is unknown, so that a wrong username
    // and a wrong password take about the same time to refuse.
    private readonly Lazy<string> _decoyHash = new(() => hasher.Hash("decoy-password-never-matches"));

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Required("username", request.Username)
            .Required("password", request.Password)
            .ThrowIfAny();

        var username = request.Username!.Trim();
        var user = await db.Users.Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Username == username, cancellationToken);

        var passwordMatches = hasher.Verify(request.Password!, user?.PasswordHash ?? _decoyHash.Value);
        if (user is null || !passwordMatches || !user.IsActive)
        {
            throw DomainException.Unauthenticated("The username or password is incorrect.");
        }

        var issued = tokens.Issue(user);
        db.Audit(AuditEntities.User, () => user.Id, AuditActions.Login,
            new { refreshTokenId = issued.RefreshTokenId }, actorUserId: user.Id);
        await db.SaveChangesAsync(cancellationToken);

        return new LoginResponse(issued.AccessToken, issued.RefreshToken, UserDto.From(user));
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair. The presented token is revoked
    /// in the same transaction, so each refresh token works once. Every refusal
    /// is a 401 whose <c>rule</c> says why (BE-03, <see cref="AuthRules"/>); a
    /// spent token presented again ends every session of its user.
    /// </summary>
    public async Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors().Required("refreshToken", request.RefreshToken).ThrowIfAny();

        var claims = tokens.ReadRefreshToken(request.RefreshToken)
            ?? throw DomainException.Unauthenticated("The refresh token is invalid or expired.", AuthRules.TokenInvalid);

        var user = await db.Users.Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Id == claims.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw DomainException.Unauthenticated("The account is no longer active.", AuthRules.AccountInactive);
        }

        var revocation = await RevocationOfAsync(claims.TokenId, cancellationToken);
        if (revocation is not null)
        {
            var rotated = revocation.Payload.Contains("ROTATED", StringComparison.Ordinal);
            if (rotated && clock.GetUtcNow() - revocation.At <= ReuseGrace)
            {
                // Rotated a moment ago: the same client retrying (a double-submit, a timed-out request), not a thief.
                throw DomainException.Unauthenticated(
                    "The refresh token was just used by another request; use the pair that request received.",
                    AuthRules.TokenAlreadyUsed);
            }
            if (rotated && user.SecurityVersion == claims.SecurityVersion)
            {
                // Spent by a refresh, and now back: a copy is in other hands. Whoever holds the newer token
                // (the user or the thief) loses it too - every session of the user ends, sign in again.
                user.EndAllSessions();
                db.Audit(AuditEntities.User, () => user.Id, AuditActions.RefreshReuseDetected,
                    new { refreshTokenId = claims.TokenId }, actorUserId: user.Id);
                await db.SaveChangesAsync(cancellationToken);
                throw DomainException.Unauthenticated(
                    "This refresh token was already used. Every session of the account has been ended; sign in again.",
                    AuthRules.TokenReused);
            }
            throw DomainException.Unauthenticated("The refresh token has been revoked.", AuthRules.TokenRevoked);
        }
        // G-11: a password reset or a role/branch change ends the sessions that were open before it,
        // refresh tokens included; otherwise a stolen refresh token would outlive the reset.
        if (user.SecurityVersion != claims.SecurityVersion)
        {
            throw DomainException.Unauthenticated(
                "The session was ended by a change to the account (password, role, branch or status); sign in again.",
                AuthRules.SessionRevoked);
        }

        var issued = tokens.Issue(user);
        db.Audit(AuditEntities.RefreshToken, () => claims.TokenId, AuditActions.Revoke,
            new { reason = "ROTATED", replacedBy = issued.RefreshTokenId }, actorUserId: user.Id);
        try
        {
            // Revoking the presented token is what makes the refresh single-use. A unique index
            // over this revocation lets only one of two concurrent refreshes of the same token
            // commit; the loser lands here. It is a double-submit, not a theft: no session is ended.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsRefreshRotationRace(exception))
        {
            throw DomainException.Unauthenticated(
                "The refresh token was used by another request at the same moment; use the pair that request received.",
                AuthRules.TokenAlreadyUsed);
        }

        return new LoginResponse(issued.AccessToken, issued.RefreshToken, UserDto.From(user));
    }

    // The partial unique index of migration 015; its name reaches here in the exception message,
    // so the race is recognised without this layer taking a dependency on the database provider.
    private const string SingleRevokeIndex = "ux_refresh_token_single_revoke";

    private static bool IsRefreshRotationRace(DbUpdateException exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error.Message.Contains(SingleRevokeIndex, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Revokes the caller's refresh token. Safe to repeat.</summary>
    public async Task LogoutAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        var claims = tokens.ReadRefreshToken(request.RefreshToken);
        if (claims is null) return; // nothing usable to revoke
        if (claims.UserId != userId) throw DomainException.Forbidden();
        if (await IsRevokedAsync(claims.TokenId, cancellationToken)) return;

        db.Audit(AuditEntities.RefreshToken, () => claims.TokenId, AuditActions.Revoke, new { reason = "LOGOUT" });
        db.Audit(AuditEntities.User, () => userId, AuditActions.Logout);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsRefreshRotationRace(exception))
        {
            // A concurrent logout or refresh revoked this token first: it is revoked either way, which is
            // all a logout asks for, so this is the same quiet success as logging out twice.
        }
    }

    public async Task<MeResponse> MeAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.Users.AsNoTracking().Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw DomainException.Unauthenticated("The account is no longer active.");
        }

        return new MeResponse(user.Id, user.Username, user.Email, user.FullName, user.Role.RoleName,
            user.BranchId, user.Status, user.Role.Permissions);
    }

    /// <summary>
    /// Every role with its permission set, so the client can build menus and role screens (G-06).
    /// The server still authorizes each endpoint on the role itself and never trusts the client (SE-03).
    /// </summary>
    public async Task<IReadOnlyList<RoleDto>> RolesAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.RoleName).ToListAsync(cancellationToken);
        return [.. roles.Select(r => new RoleDto(r.RoleName, r.Permissions))];
    }

    private Task<bool> IsRevokedAsync(long tokenId, CancellationToken cancellationToken) =>
        db.AuditLogs.AnyAsync(a => a.EntityType == AuditEntities.RefreshToken && a.EntityId == tokenId
                                   && a.Action == AuditActions.Revoke, cancellationToken);

    /// <summary>
    /// How long after a rotation the old refresh token, presented again, is taken for the same client retrying
    /// rather than for a stolen copy (BE-03): refused with TOKEN_ALREADY_USED, without ending any session.
    /// </summary>
    public static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    private sealed record Revocation(string Payload, DateTimeOffset At);

    /// <summary>The token's revocation (its reason in the payload, and when), or null while it is live.</summary>
    private async Task<Revocation?> RevocationOfAsync(long tokenId, CancellationToken cancellationToken)
    {
        var row = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.RefreshToken && a.EntityId == tokenId && a.Action == AuditActions.Revoke)
            .Select(a => new { a.PayloadJson, a.CreatedAt }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new Revocation(row.PayloadJson ?? "", row.CreatedAt);
    }
}
