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
    ICurrentUser currentUser)
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
    /// in the same transaction, so each refresh token works once.
    /// </summary>
    public async Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors().Required("refreshToken", request.RefreshToken).ThrowIfAny();

        var claims = tokens.ReadRefreshToken(request.RefreshToken);
        if (claims is null || await IsRevokedAsync(claims.TokenId, cancellationToken))
        {
            throw DomainException.Unauthenticated("The refresh token is invalid, expired or revoked.");
        }

        var user = await db.Users.Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Id == claims.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw DomainException.Unauthenticated("The account is no longer active.");
        }

        var issued = tokens.Issue(user);
        db.Audit(AuditEntities.RefreshToken, () => claims.TokenId, AuditActions.Revoke,
            new { reason = "ROTATED", replacedBy = issued.RefreshTokenId }, actorUserId: user.Id);
        try
        {
            // Revoking the presented token is what makes the refresh single-use. A unique index
            // over this revocation lets only one of two concurrent refreshes of the same token
            // commit; the loser lands here and is refused exactly like a token already spent.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsRefreshRotationRace(exception))
        {
            throw DomainException.Unauthenticated("The refresh token is invalid, expired or revoked.");
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
        await db.SaveChangesAsync(cancellationToken);
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
}
