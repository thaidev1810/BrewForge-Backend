using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BrewForge.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const int MinSigningKeyBytes = 32;

    public string Issuer { get; set; } = "brewforge";

    /// <summary>Audience of access tokens. The API accepts only this audience.</summary>
    public string Audience { get; set; } = "brewforge-api";

    /// <summary>
    /// Audience of refresh tokens. Being different from <see cref="Audience"/>,
    /// it makes a refresh token useless as a bearer token and the reverse.
    /// </summary>
    public string RefreshAudience { get; set; } = "brewforge-refresh";

    /// <summary>HMAC-SHA256 secret, at least 32 bytes. Supplied by configuration, never committed for production.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Also the inactivity window of CR-07: every refresh issues a new refresh
    /// token, so a session ends 60 minutes after its last refresh.
    /// </summary>
    public int RefreshTokenMinutes { get; set; } = 60;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < MinSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"{Section}:SigningKey must be configured with at least {MinSigningKeyBytes} bytes.");
        }
        return new SymmetricSecurityKey(bytes);
    }
}

/// <summary>Claim names of the access token, as fixed by the API contract.</summary>
public static class BrewForgeClaims
{
    public const string Subject = "sub";
    public const string Username = "username";
    public const string Role = "role";
    public const string BranchId = "branchId";
    public const string TokenId = "jti";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly SigningCredentials _credentials =
        new(options.Value.CreateKey(), SecurityAlgorithms.HmacSha256);
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public IssuedTokens Issue(AppUser user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var subject = user.Id.ToString(CultureInfo.InvariantCulture);

        var accessClaims = new Dictionary<string, object>
        {
            [BrewForgeClaims.Subject] = subject,
            [BrewForgeClaims.Username] = user.Username,
            [BrewForgeClaims.Role] = user.Role.RoleName.Code(),
        };
        // Absent for head-office roles, which the contract describes as null.
        if (user.BranchId is { } branchId) accessClaims[BrewForgeClaims.BranchId] = branchId;

        var refreshTokenId = NewTokenId();
        var refreshClaims = new Dictionary<string, object>
        {
            [BrewForgeClaims.Subject] = subject,
            [BrewForgeClaims.TokenId] = refreshTokenId.ToString(CultureInfo.InvariantCulture),
        };

        return new IssuedTokens(
            Create(accessClaims, _options.Audience, now, _options.AccessTokenMinutes),
            Create(refreshClaims, _options.RefreshAudience, now, _options.RefreshTokenMinutes),
            refreshTokenId);
    }

    public RefreshTokenClaims? ReadRefreshToken(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var result = _handler.ValidateTokenAsync(refreshToken, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.RefreshAudience,
            IssuerSigningKey = _credentials.Key,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            LifetimeValidator = (_, expires, _, _) => expires is not null && expires > clock.GetUtcNow().UtcDateTime,
        }).GetAwaiter().GetResult();

        if (!result.IsValid) return null;

        return result.Claims.TryGetValue(BrewForgeClaims.Subject, out var sub)
               && long.TryParse(sub?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
               && result.Claims.TryGetValue(BrewForgeClaims.TokenId, out var jti)
               && long.TryParse(jti?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var tokenId)
            ? new RefreshTokenClaims(userId, tokenId)
            : null;
    }

    private string Create(Dictionary<string, object> claims, string audience, DateTime now, int lifetimeMinutes) =>
        _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(lifetimeMinutes),
            SigningCredentials = _credentials,
        });

    // A positive 63-bit random number: unguessable, and it fits audit_log.entity_id.
    private static long NewTokenId() =>
        BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue;
}
