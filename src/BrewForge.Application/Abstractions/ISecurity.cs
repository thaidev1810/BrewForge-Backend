using BrewForge.Domain.Identity;

namespace BrewForge.Application.Abstractions;

/// <summary>Salted adaptive password hashing (SE-02). The implementation is Argon2id.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

public sealed record IssuedTokens(string AccessToken, string RefreshToken, long RefreshTokenId);

/// <summary>
/// What a refresh token carries. <c>SecurityVersion</c> is the account's version when the token was issued:
/// a password reset, deactivation or role/branch change bumps it, and a token of an older version is refused (G-11).
/// </summary>
public sealed record RefreshTokenClaims(long UserId, long TokenId, int SecurityVersion);

public interface ITokenService
{
    /// <summary>Issues an access token and a refresh token for the user.</summary>
    IssuedTokens Issue(AppUser user);

    /// <summary>
    /// Reads a refresh token. Returns null when it is malformed, expired, not
    /// signed by this service, or not a refresh token at all.
    /// </summary>
    RefreshTokenClaims? ReadRefreshToken(string? refreshToken);
}
