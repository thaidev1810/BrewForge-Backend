namespace BrewForge.Application.Auth;

/// <summary>
/// BE-03: the <c>rule</c> of a 401, so that the client knows what to do - refresh, use the pair another request
/// received, or send the user to the sign-in screen. A 403 (signed in, not allowed) never carries one of these.
/// </summary>
public static class AuthRules
{
    /// <summary>Access token expired: refresh it.</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    /// <summary>Token missing, malformed, wrongly signed or (refresh token) expired: sign in.</summary>
    public const string TokenInvalid = "TOKEN_INVALID";

    /// <summary>Refresh token revoked by a logout: sign in.</summary>
    public const string TokenRevoked = "TOKEN_REVOKED";

    /// <summary>A refresh token already spent by a refresh came back: every session of the user is ended. Sign in.</summary>
    public const string TokenReused = "TOKEN_REUSED";

    /// <summary>Two refreshes of one token at the same moment: this one lost. Use the pair the other received.</summary>
    public const string TokenAlreadyUsed = "TOKEN_ALREADY_USED";

    /// <summary>The password, role, branch or status changed after the token was issued: sign in.</summary>
    public const string SessionRevoked = "SESSION_REVOKED";

    /// <summary>The account is inactive (or gone): sign in is refused too.</summary>
    public const string AccountInactive = "ACCOUNT_INACTIVE";
}
