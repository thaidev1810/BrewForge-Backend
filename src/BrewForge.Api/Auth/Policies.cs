using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;

namespace BrewForge.Api.Auth;

/// <summary>
/// One authorization policy per role, named after the role code, plus the
/// "head-office roles" group the API contract uses for catalogue and recipe
/// reads.
/// </summary>
public static class Policies
{
    public const string Admin = "ADMIN";
    public const string RdSpecialist = "RD_SPECIALIST";
    public const string RdManager = "RD_MANAGER";
    public const string Trainer = "TRAINER";
    public const string Trainee = "TRAINEE";
    public const string QualityAuditor = "QUALITY_AUDITOR";
    public const string BranchManager = "BRANCH_MANAGER";
    public const string TrainingManager = "TRAINING_MANAGER";

    /// <summary>Every role that is not confined to a branch: all but BRANCH_MANAGER and TRAINEE.</summary>
    public const string HeadOffice = "HEAD_OFFICE";

    public static readonly IReadOnlyList<RoleName> HeadOfficeRoles =
        [.. Enum.GetValues<RoleName>().Where(role => !role.IsBranchScoped())];

    public static void Configure(AuthorizationOptions options)
    {
        foreach (var role in Enum.GetValues<RoleName>())
        {
            options.AddPolicy(role.Code(), policy => policy.RequireRole(role.Code()));
        }
        options.AddPolicy(HeadOffice, policy => policy.RequireRole(HeadOfficeRoles.Select(role => role.Code())));

        // Nothing is public by omission: an endpoint without an attribute still needs a valid token.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

/// <summary>Restricts an endpoint to a set of roles, for sets that are not a single role or the head-office group.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class AuthorizeRolesAttribute : AuthorizeAttribute
{
    public AuthorizeRolesAttribute(params RoleName[] roles)
    {
        Roles = string.Join(',', roles.Select(role => role.Code()));
    }
}
