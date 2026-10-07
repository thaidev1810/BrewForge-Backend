namespace BrewForge.Application.Common;

/// <summary>The values written to <c>audit_log.action</c>.</summary>
public static class AuditActions
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string Deactivate = "DEACTIVATE";
    public const string Login = "LOGIN";
    public const string Logout = "LOGOUT";
    public const string Revoke = "REVOKE";
}

/// <summary>The values written to <c>audit_log.entity_type</c>.</summary>
public static class AuditEntities
{
    public const string User = "AppUser";
    public const string Branch = "Branch";
    public const string Ingredient = "Ingredient";
    public const string StandardEquipment = "StandardEquipment";

    /// <summary>
    /// A refresh token, identified by its numeric token id. The schema has no
    /// table for refresh tokens, so a revocation is recorded here and a token
    /// is revoked exactly when such an entry exists for its id.
    /// </summary>
    public const string RefreshToken = "RefreshToken";
}
