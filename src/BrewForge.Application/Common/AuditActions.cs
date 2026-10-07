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

    public const string Validate = "VALIDATE";
    public const string Submit = "SUBMIT";
    public const string AiDraft = "AI_DRAFT";

    /// <summary>
    /// One automatic AI repair of a draft. The schema has no counter column
    /// on <c>recipe_version</c>, so the number of repairs a draft has had is
    /// the number of these entries it has - which cannot be reset, because
    /// the log is append-only.
    /// </summary>
    public const string AiRepair = "AI_REPAIR";
    public const string ManualRepair = "MANUAL_REPAIR";

    public const string Review = "REVIEW";
    public const string Release = "RELEASE";
    public const string ReleaseRefused = "RELEASE_REFUSED";
    public const string Supersede = "SUPERSEDE";
    public const string Rollback = "ROLLBACK";
    public const string ImportExisting = "IMPORT_EXISTING";
}

/// <summary>The values written to <c>audit_log.entity_type</c>.</summary>
public static class AuditEntities
{
    public const string User = "AppUser";
    public const string Branch = "Branch";
    public const string Ingredient = "Ingredient";
    public const string StandardEquipment = "StandardEquipment";
    public const string Recipe = "Recipe";
    public const string RecipeVersion = "RecipeVersion";

    /// <summary>
    /// A refresh token, identified by its numeric token id. The schema has no
    /// table for refresh tokens, so a revocation is recorded here and a token
    /// is revoked exactly when such an entry exists for its id.
    /// </summary>
    public const string RefreshToken = "RefreshToken";
}
