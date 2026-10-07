using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>SF-02: API contract section 2.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthTests(BrewForgeApiFactory factory)
{
    private const string Login = "/api/v1/auth/login";
    private const string Refresh = "/api/v1/auth/refresh";
    private const string Logout = "/api/v1/auth/logout";
    private const string Me = "/api/v1/auth/me";

    [Fact]
    public async Task Login_returns_both_tokens_and_the_user()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Login,
            new { username = TestUsers.Trainee, password = BrewForgeApiFactory.SeedPassword });

        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        var user = body.GetProperty("user");
        Assert.Equal(TestUsers.Trainee, user.GetProperty("username").GetString());
        Assert.Equal("TRAINEE", user.GetProperty("role").GetString());
        Assert.False(user.TryGetProperty("passwordHash", out _));
        Assert.False(user.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Access_token_carries_the_claims_of_the_contract()
    {
        var b01 = await factory.BranchIdAsync("B01");
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);

        var trainee = DecodePayload((await factory.LoginAsync(TestUsers.Trainee)).AccessToken);
        var manager = DecodePayload((await factory.LoginAsync(TestUsers.RdManager)).AccessToken);

        Assert.Equal(traineeId.ToString(), trainee.GetProperty("sub").GetString());
        Assert.Equal(TestUsers.Trainee, trainee.GetProperty("username").GetString());
        Assert.Equal("TRAINEE", trainee.GetProperty("role").GetString());
        Assert.Equal(b01, trainee.GetProperty("branchId").GetInt64());
        Assert.True(trainee.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // A head-office role carries no branch.
        Assert.Equal("RD_MANAGER", manager.GetProperty("role").GetString());
        Assert.False(manager.TryGetProperty("branchId", out var branch) && branch.ValueKind != JsonValueKind.Null);
    }

    [Theory]
    [InlineData(TestUsers.Admin, "wrong-password")]
    [InlineData("nobody-by-this-name", BrewForgeApiFactory.SeedPassword)]
    public async Task Login_with_bad_credentials_is_401_in_the_error_envelope(string username, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Login, new { username, password });

        await response.ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Login_without_a_password_is_400_naming_the_field()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Login, new { username = TestUsers.Admin });

        var envelope = await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Contains("password", envelope.DetailFields());
    }

    [Fact]
    public async Task Me_returns_the_caller_with_role_and_permissions()
    {
        using var client = await factory.ClientForAsync(TestUsers.RdManager);

        var body = await (await client.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal(TestUsers.RdManager, body.GetProperty("username").GetString());
        Assert.Equal("RD_MANAGER", body.GetProperty("role").GetString());
        var permissions = body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("recipes.release", permissions);
        Assert.DoesNotContain("users.manage", permissions);
    }

    [Fact]
    public async Task Request_without_a_token_is_401_in_the_error_envelope()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Me);

        await response.ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Fact]
    public async Task Request_with_a_tampered_token_is_401()
    {
        var token = (await factory.LoginAsync(TestUsers.Trainee)).AccessToken;
        var parts = token.Split('.');
        // Promote the trainee to ADMIN and keep the original signature.
        var forgedPayload = Encoding.UTF8.GetString(Base64Url(parts[1])).Replace("TRAINEE", "ADMIN");
        var forged = $"{parts[0]}.{Convert.ToBase64String(Encoding.UTF8.GetBytes(forgedPayload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        await (await client.GetAsync("/api/v1/users")).ShouldBeErrorAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_issues_a_new_pair_and_the_old_refresh_token_stops_working()
    {
        using var client = factory.CreateClient();
        var first = await factory.LoginAsync(TestUsers.Trainer);

        var refreshed = await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeAsync(HttpStatusCode.OK);
        var newAccess = refreshed.GetProperty("accessToken").GetString()!;
        var newRefresh = refreshed.GetProperty("refreshToken").GetString()!;

        Assert.NotEqual(first.RefreshToken, newRefresh);

        // The new access token works.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newAccess);
        await (await client.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);

        // The rotated token is spent; the new one is good exactly once more.
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = newRefresh }))
            .ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var session = await factory.LoginAsync(TestUsers.Auditor);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        await (await client.PostAsJsonAsync(Logout, new { refreshToken = session.RefreshToken }))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Logout_with_another_users_refresh_token_is_403()
    {
        var mine = await factory.LoginAsync(TestUsers.Trainee);
        var theirs = await factory.LoginAsync(TestUsers.Trainer);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mine.AccessToken);

        await (await client.PostAsJsonAsync(Logout, new { refreshToken = theirs.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Forbidden, "MSG-E01");
    }

    [Fact]
    public async Task The_two_token_kinds_are_not_interchangeable()
    {
        var session = await factory.LoginAsync(TestUsers.Trainee);
        using var client = factory.CreateClient();

        // A refresh token is not a bearer token...
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.RefreshToken);
        await (await client.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized);

        // ...and an access token cannot be refreshed.
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = session.AccessToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Passwords_are_stored_as_argon2id_hashes()
    {
        var hashes = await factory.WithDbAsync(db => db.Users.Select(u => u.PasswordHash).ToListAsync());

        Assert.NotEmpty(hashes);
        Assert.All(hashes, hash =>
        {
            Assert.StartsWith("$argon2id$v=19$", hash);
            Assert.DoesNotContain(BrewForgeApiFactory.SeedPassword, hash);
        });
        // Each hash has its own salt, so equal passwords do not produce equal hashes.
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    private static JsonElement DecodePayload(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Base64Url(jwt.Split('.')[1]));

    private static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
