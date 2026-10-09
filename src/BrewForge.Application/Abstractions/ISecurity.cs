using BrewForge.Domain.Identity;

namespace BrewForge.Application.Abstractions;

/// <summary>Salted adaptive password hashing (SE-02). The implementation is Argon2id.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

public sealed record IssuedTokens(string AccessToken, string RefreshToken, long RefreshTokenId);

public sealed record RefreshTokenClaims(long UserId, long TokenId);

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
