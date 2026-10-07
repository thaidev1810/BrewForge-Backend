using System.Net;
using System.Net.Http.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>UC-01: user and role management.</summary>
[Collection(ApiCollection.Name)]
public sealed class UserManagementTests(BrewForgeApiFactory factory)
{
    private const string Users = "/api/v1/users";
    private const string NewPassword = "Another-Test-Passw0rd";

    private static string UniqueName() => $"u{Guid.NewGuid():N}"[..12];

    private static object NewUser(string username, string role, long? branchId = null, string password = NewPassword) =>
        new { username, email = $"{username}@brewforge.local", password, fullName = "Test User", role, branchId };

    [Fact]
    public async Task Created_user_can_log_in_and_the_password_is_never_returned_or_stored()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = UniqueName();

        var response = await admin.PostAsJsonAsync(Users, NewUser(username, "RD_SPECIALIST"));

        var created = await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal("RD_SPECIALIST", created.GetProperty("role").GetString());
        Assert.Equal("ACTIVE", created.GetProperty("status").GetString());
        Assert.DoesNotContain(NewPassword, await response.Content.ReadAsStringAsync());

        var stored = await factory.WithDbAsync(db =>
            db.Users.Where(u => u.Username == username).Select(u => u.PasswordHash).SingleAsync());
        Assert.StartsWith("$argon2id$", stored);
        Assert.DoesNotContain(NewPassword, stored);

        var session = await factory.LoginAsync(username, NewPassword);
        Assert.Equal(username, session.User.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Store_level_role_requires_a_branch()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "TRAINEE", branchId: null));

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("branchId", envelope.DetailFields());
    }

    [Fact]
    public async Task Head_office_role_may_not_carry_a_branch()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var b01 = await factory.BranchIdAsync("B01");

        var response = await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "RD_MANAGER", b01));

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("branchId", envelope.DetailFields());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task Weak_password_is_400(string password)
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "TRAINER", password: password));

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("password", envelope.DetailFields());
    }

    [Fact]
    public async Task Role_outside_the_enumeration_is_400()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "SUPERUSER"));

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        Assert.Contains("role", envelope.DetailFields());
    }

    [Fact]
    public async Task Duplicate_username_is_409()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);

        var response = await admin.PostAsJsonAsync(Users, NewUser(TestUsers.Trainer, "TRAINER"));

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "UNIQUE");
        Assert.Contains("username", envelope.DetailFields());
    }

    [Fact]
    public async Task User_is_moved_to_another_role_and_branch()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = UniqueName();
        var b02 = await factory.BranchIdAsync("B02");
        var created = await (await admin.PostAsJsonAsync(Users, NewUser(username, "TRAINER")))
            .ShouldBeAsync(HttpStatusCode.Created);

        var updated = await (await admin.PutAsJsonAsync($"{Users}/{created.GetProperty("id").GetInt64()}", new
            {
                email = $"{username}@brewforge.local", fullName = "Moved User", role = "BRANCH_MANAGER", branchId = b02,
            }))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("BRANCH_MANAGER", updated.GetProperty("role").GetString());
        Assert.Equal(b02, updated.GetProperty("branchId").GetInt64());
        Assert.Equal("Moved User", updated.GetProperty("fullName").GetString());

        // The new role is in the next token, and with it the branch restriction.
        using var moved = factory.CreateClient();
        moved.DefaultRequestHeaders.Authorization =
            new("Bearer", (await factory.LoginAsync(username, NewPassword)).AccessToken);
        var branches = await (await moved.GetAsync("/api/v1/branches")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, branches.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Deactivated_user_keeps_its_record_and_can_no_longer_log_in()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = UniqueName();
        var created = await (await admin.PostAsJsonAsync(Users, NewUser(username, "TRAINER")))
            .ShouldBeAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        var session = await factory.LoginAsync(username, NewPassword);

        var deactivated = await (await admin.PostAsync($"{Users}/{id}/deactivate", null))
            .ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("INACTIVE", deactivated.GetProperty("status").GetString());
        await (await admin.GetAsync($"{Users}/{id}")).ShouldBeAsync(HttpStatusCode.OK);

        using var anonymous = factory.CreateClient();
        await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { username, password = NewPassword }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized);
        await (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Administrator_cannot_deactivate_their_own_account()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var adminId = await factory.UserIdAsync(TestUsers.Admin);

        var response = await admin.PostAsync($"{Users}/{adminId}/deactivate", null);

        await response.ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "SELF_DEACTIVATION");
    }

    [Fact]
    public async Task User_list_filters_by_role_and_branch()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var b01 = await factory.BranchIdAsync("B01");

        var page = await (await admin.GetAsync($"{Users}?role=TRAINEE&branchId={b01}&sort=username,asc"))
            .ShouldBeAsync(HttpStatusCode.OK);

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 2);
        Assert.All(items, user =>
        {
            Assert.Equal("TRAINEE", user.GetProperty("role").GetString());
            Assert.Equal(b01, user.GetProperty("branchId").GetInt64());
            Assert.False(user.TryGetProperty("passwordHash", out _));
        });
    }
}
