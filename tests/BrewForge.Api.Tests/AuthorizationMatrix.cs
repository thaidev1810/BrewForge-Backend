using BrewForge.Domain.Identity;

namespace BrewForge.Api.Tests;

/// <summary>
/// The authorization matrix: for every endpoint of the API, the roles that
/// may reach it. It is written here independently of the controllers, from
/// the role columns of the API contract, and <see cref="AuthorizationMatrixTests"/>
/// holds the running API to it in both directions - every listed role gets
/// in, every other role gets 403, and an endpoint missing from this table
/// fails the build.
/// </summary>
public static class AuthorizationMatrix
{
    private static readonly RoleName[] Everyone = Enum.GetValues<RoleName>();

    private static readonly RoleName[] Nobody = [];

    private static readonly RoleName[] AdminOnly = [RoleName.Admin];

    private static readonly RoleName[] Specialist = [RoleName.RdSpecialist];

    /// <summary>"All head-office roles" in the contract: every role not confined to a branch.</summary>
    private static readonly RoleName[] HeadOffice =
    [
        RoleName.Admin, RoleName.RdSpecialist, RoleName.RdManager, RoleName.Trainer, RoleName.QualityAuditor,
        RoleName.TrainingManager,
    ];

    public sealed record Entry(string Method, string Route, RoleName[] Allowed, bool Anonymous = false)
    {
        public string Key => $"{Method} {Route}";

        /// <summary>The route with every parameter replaced by an id that does not exist.</summary>
        public string Path => "/api/v1" + System.Text.RegularExpressions.Regex.Replace(Route, @"\{[^}]+\}", "999999999");
    }

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // 2. Authentication
        new("POST", "/auth/login", Nobody, Anonymous: true),
        new("POST", "/auth/refresh", Nobody, Anonymous: true),
        new("POST", "/auth/logout", Everyone),
        new("GET", "/auth/me", Everyone),

        // 3. Master data
        new("GET", "/users", AdminOnly),
        new("POST", "/users", AdminOnly),
        new("GET", "/users/{id}", AdminOnly),
        new("PUT", "/users/{id}", AdminOnly),
        new("POST", "/users/{id}/deactivate", AdminOnly),

        // Reads are wider than the contract's "ADMIN": see BranchesController.
        new("GET", "/branches", Everyone),
        new("POST", "/branches", AdminOnly),
        new("GET", "/branches/{id}", Everyone),
        new("PUT", "/branches/{id}", AdminOnly),
        new("POST", "/branches/{id}/deactivate", AdminOnly),

        new("GET", "/ingredients", HeadOffice),
        new("POST", "/ingredients", AdminOnly),
        new("GET", "/ingredients/{id}", HeadOffice),
        new("PUT", "/ingredients/{id}", AdminOnly),
        new("POST", "/ingredients/{id}/deactivate", AdminOnly),

        new("GET", "/equipment-classes", HeadOffice),
        new("POST", "/equipment-classes", AdminOnly),
        new("GET", "/equipment-classes/{id}", HeadOffice),
        new("PUT", "/equipment-classes/{id}", AdminOnly),
        new("POST", "/equipment-classes/{id}/deactivate", AdminOnly),

        // 4. Recipe authoring and standardization
        new("GET", "/recipes", HeadOffice),
        new("POST", "/recipes", Specialist),
        new("GET", "/recipes/{id}", HeadOffice),
        new("GET", "/recipes/{id}/versions", HeadOffice),
        new("POST", "/recipes/{id}/versions", Specialist),
        new("GET", "/recipe-versions/{id}", HeadOffice),
        new("PUT", "/recipe-versions/{id}", Specialist),
        new("POST", "/recipe-versions/{id}/generate-draft", Specialist),
        new("POST", "/recipe-versions/{id}/validate", [RoleName.RdSpecialist, RoleName.RdManager]),
        new("POST", "/recipe-versions/{id}/repair", Specialist),
        new("POST", "/recipe-versions/{id}/submit", Specialist),
    ];
}
