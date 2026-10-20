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

    [Fact]
    public async Task Lookup_is_minimal_filtered_and_scoped_to_the_callers_branch()
    {
        var b01 = await factory.BranchIdAsync("B01");
        var b02 = await factory.BranchIdAsync("B02");

        // A trainer (head office) looks up trainees of B01; the result carries only the minimum.
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var page = await (await trainer.GetAsync($"/api/v1/users/lookup?role=TRAINEE&branchId={b01}&size=100"))
            .ShouldBeAsync(HttpStatusCode.OK);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, u =>
        {
            Assert.Equal("TRAINEE", u.GetProperty("role").GetString());
            Assert.Equal(b01, u.GetProperty("branchId").GetInt64());
            Assert.True(u.GetProperty("active").GetBoolean());
            Assert.True(u.TryGetProperty("branchName", out _));
            Assert.False(u.TryGetProperty("email", out _));          // minimal: no e-mail
            Assert.False(u.TryGetProperty("passwordHash", out _));   // and never the hash
        });

        // A branch manager of B01 cannot reach B02's people even by asking for branchId=B02.
        using var manager = await factory.ClientForAsync(TestUsers.BranchManager);
        var crossBranch = await (await manager.GetAsync($"/api/v1/users/lookup?branchId={b02}&size=100"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(crossBranch.GetProperty("items").EnumerateArray());

        var ownBranch = await (await manager.GetAsync("/api/v1/users/lookup?size=100")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.NotEmpty(ownBranch.GetProperty("items").EnumerateArray());
        Assert.All(ownBranch.GetProperty("items").EnumerateArray(), u => Assert.Equal(b01, u.GetProperty("branchId").GetInt64()));
    }

    [Fact]
    public async Task Lookup_resolves_stored_ids_including_inactive_but_stays_in_scope()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var b01 = await factory.BranchIdAsync("B01");

        async Task<long> NewB01TraineeAsync()
        {
            var created = await (await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "TRAINEE", b01)))
                .ShouldBeAsync(HttpStatusCode.Created);
            return created.GetProperty("id").GetInt64();
        }

        var active = await NewB01TraineeAsync();
        var gone = await NewB01TraineeAsync();
        await (await admin.PostAsync($"{Users}/{gone}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);

        // A trainer resolves both stored IDs to labels: the inactive one is included (active=false) so a
        // historical reference keeps its name.
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var resolved = await (await trainer.GetAsync($"/api/v1/users/lookup?ids={active},{gone}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        var byId = resolved.GetProperty("items").EnumerateArray()
            .ToDictionary(u => u.GetProperty("id").GetInt64(), u => u.GetProperty("active").GetBoolean());
        Assert.True(byId[active]);
        Assert.False(byId[gone]);

        // The B02 manager cannot resolve B01 IDs: branch scope holds even when IDs are given.
        using var b02Manager = await factory.ClientForAsync(TestUsers.BranchManagerB02);
        var outOfScope = await (await b02Manager.GetAsync($"/api/v1/users/lookup?ids={active},{gone}"))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(outOfScope.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Coverage_is_recounted_when_a_certified_user_is_deactivated_or_moved()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var b01 = await factory.BranchIdAsync("B01");
        var b02 = await factory.BranchIdAsync("B02");

        // A released existing drink the seed already sells at every branch, with coverage not met.
        var version = await factory.WithDbAsync(db => (from v in db.RecipeVersions
            join r in db.Recipes on v.RecipeId equals r.Id
            where r.RecipeCode == "R05" && v.State == BrewForge.Domain.Recipes.VersionState.Released
            select v.Id).SingleAsync());
        // Reuse the published course bound to this version if the seed already made one; only the
        // certificate's course_id matters here, and a version keeps at most one PRODUCT course (BR-18).
        var existing = await factory.WithDbAsync(db => db.Courses
            .Where(c => c.RecipeVersionId == version).Select(c => (long?)c.Id).FirstOrDefaultAsync());
        var course = existing ?? (await factory.NewPublishedCourseAsync(version)).GetProperty("id").GetInt64();

        async Task<long> NewB01TraineeAsync()
        {
            var created = await (await admin.PostAsJsonAsync(Users, NewUser(UniqueName(), "TRAINEE", b01)))
                .ShouldBeAsync(HttpStatusCode.Created);
            return created.GetProperty("id").GetInt64();
        }
        Task<int> CoverageAsync(long branchId) => factory.WithDbAsync(db => db.BranchLaunchStatuses
            .IgnoreQueryFilters().Where(l => l.BranchId == branchId && l.RecipeVersionId == version)
            .Select(l => l.CertifiedCount).SingleAsync());

        var t1 = await NewB01TraineeAsync();
        var t2 = await NewB01TraineeAsync();
        await factory.CertifyAsync(t1, course, version);
        await factory.CertifyAsync(t2, course, version);

        // Deactivating one certified trainee recounts B01 to the one who is left: not two (the other
        // certificate is still valid) and not the stale zero (nothing had recounted before).
        await (await admin.PostAsync($"{Users}/{t1}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, await CoverageAsync(b01));

        // Reactivating and moving t1 to B02 carries their certified head there: B01 stays 1, B02 becomes 1.
        await (await admin.PutAsJsonAsync($"{Users}/{t1}", new
        {
            email = $"u{t1}@brewforge.local", fullName = "Moved Trainee", role = "TRAINEE", branchId = b02, status = "ACTIVE",
        })).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, await CoverageAsync(b01));
        Assert.Equal(1, await CoverageAsync(b02));
    }
}
