using System.Reflection;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests;

/// <summary>
/// G-06: the permission catalogue stays in step with the roles and the endpoints. Every permission a
/// role advertises is a real code, the seeded database matches what the code defines, and a role that
/// can reach an endpoint carries the permission the front-end gates that control on. The server still
/// authorizes on the role itself; these hold the advertised capabilities honest.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PermissionCatalogTests(BrewForgeApiFactory factory)
{
    private static readonly IReadOnlySet<string> DeclaredCodes =
        typeof(Permissions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

    [Fact]
    public void Every_permission_a_role_carries_is_a_declared_code()
    {
        foreach (var role in Enum.GetValues<RoleName>())
        {
            Assert.All(Permissions.DefaultsFor(role), code => Assert.Contains(code, DeclaredCodes));
        }
    }

    [Fact]
    public async Task The_seeded_roles_carry_exactly_the_permissions_the_code_defines()
    {
        var stored = await factory.WithDbAsync(db => db.Roles.AsNoTracking()
            .ToDictionaryAsync(r => r.RoleName, r => r.Permissions));

        foreach (var role in Enum.GetValues<RoleName>())
        {
            Assert.Equal(Permissions.DefaultsFor(role).Order(), stored[role].Order());
        }
    }

    [Theory]
    [MemberData(nameof(CapabilityEndpoints))]
    public void A_role_that_can_reach_an_endpoint_carries_the_permission_it_is_gated_on(string permission,
        string method, string route)
    {
        var entry = AuthorizationMatrix.Entries.Single(e => e.Method == method && e.Route == route);
        Assert.All(entry.Allowed, role => Assert.Contains(permission, Permissions.DefaultsFor(role)));
    }

    /// <summary>A representative endpoint for each permission that gates a control in the UI.</summary>
    public static TheoryData<string, string, string> CapabilityEndpoints() => new()
    {
        { Permissions.UsersManage, "POST", "/users" },
        { Permissions.SettingsManage, "PUT", "/settings/{key}" },
        { Permissions.ProductsManage, "POST", "/products" },
        { Permissions.AssignmentsRead, "GET", "/assignments" },
        { Permissions.AssignmentsManage, "POST", "/assignments" },
        { Permissions.SchedulesManageOwn, "POST", "/enrollments/{id}/practical-schedules" },
        { Permissions.QualityAct, "POST", "/branch-audits" },
        { Permissions.QuestionBankManage, "POST", "/question-bank" },
        { Permissions.SkillsRead, "GET", "/me/skills" },
        { Permissions.LearningOwn, "GET", "/me/enrollments" },
        { Permissions.RecipesRelease, "POST", "/recipe-versions/{id}/release" },
        { Permissions.RecipesAuthor, "POST", "/recipes" },
        { Permissions.ClassesManage, "POST", "/training-classes" },
        { Permissions.RegulationsManage, "POST", "/training-regulations" },
        { Permissions.SalesRecord, "POST", "/sales" },
    };
}
