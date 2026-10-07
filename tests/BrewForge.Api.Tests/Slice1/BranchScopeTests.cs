using System.Net;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>
/// A BRANCH_MANAGER and a TRAINEE may only read rows of their own branch. The
/// restriction is a query filter of the persistence layer, so it holds for
/// every query, including ones no controller has been written for yet.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BranchScopeTests(BrewForgeApiFactory factory)
{
    [Theory]
    [InlineData(TestUsers.BranchManager, "B01")]
    [InlineData(TestUsers.Trainee, "B01")]
    [InlineData(TestUsers.BranchManagerB02, "B02")]
    public async Task Store_level_caller_lists_only_its_own_branch(string username, string ownBranch)
    {
        using var client = await factory.ClientForAsync(username);

        var page = await (await client.GetAsync("/api/v1/branches")).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(ownBranch, page.GetProperty("items")[0].GetProperty("branchCode").GetString());
    }

    [Fact]
    public async Task Store_level_caller_gets_404_for_another_branch()
    {
        var own = await factory.BranchIdAsync("B01");
        var other = await factory.BranchIdAsync("B02");
        using var client = await factory.ClientForAsync(TestUsers.BranchManager);

        await (await client.GetAsync($"/api/v1/branches/{own}")).ShouldBeAsync(HttpStatusCode.OK);
        // 404, not 403: the contract does not reveal that the row exists.
        await (await client.GetAsync($"/api/v1/branches/{other}"))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Head_office_caller_lists_every_branch()
    {
        using var client = await factory.ClientForAsync(TestUsers.RdManager);

        var page = await (await client.GetAsync("/api/v1/branches?q=B0")).ShouldBeAsync(HttpStatusCode.OK);

        var codes = page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("branchCode").GetString());
        Assert.Superset(new HashSet<string?> { "B01", "B02", "B03" }, codes.ToHashSet());
    }

    [Fact]
    public async Task Query_filter_confines_staff_queries_to_the_callers_branch()
    {
        var b01 = await factory.BranchIdAsync("B01");
        await using var db = factory.CreateDbContextAs(new FakeCurrentUser(1, RoleName.BranchManager, b01));

        var visible = await db.Users.ToListAsync();

        Assert.NotEmpty(visible);
        Assert.All(visible, user => Assert.Equal(b01, user.BranchId));
        Assert.Contains(visible, user => user.Username == TestUsers.Trainee);
        Assert.DoesNotContain(visible, user => user.Username == TestUsers.BranchManagerB02);
        Assert.DoesNotContain(visible, user => user.Username == TestUsers.Admin);
    }

    [Fact]
    public async Task Store_level_caller_without_a_branch_sees_nothing_rather_than_everything()
    {
        await using var db = factory.CreateDbContextAs(new FakeCurrentUser(1, RoleName.Trainee, branchId: null));

        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.Branches.ToListAsync());
    }

    [Fact]
    public async Task Head_office_caller_is_not_filtered()
    {
        await using var db = factory.CreateDbContextAs(new FakeCurrentUser(1, RoleName.QualityAuditor, null));

        var branches = await db.Users.Select(user => user.BranchId).Distinct().ToListAsync();

        Assert.True(branches.Count >= 3, "expected head-office staff and the staff of at least two branches");
    }
}
