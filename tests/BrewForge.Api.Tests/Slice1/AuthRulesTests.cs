using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>
/// BE-03: every 401 says why in its <c>rule</c>, so the client knows whether to refresh, to use the pair another
/// request received, or to send the user to the sign-in screen; and a spent refresh token that comes back ends
/// every session of its user. Each test signs in a user of its own: ending sessions must not touch the tokens
/// the other tests hold for the seeded users.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthRulesTests(BrewForgeApiFactory factory)
{
    private const string Refresh = "/api/v1/auth/refresh";
    private const string Logout = "/api/v1/auth/logout";
    private const string Me = "/api/v1/auth/me";
    private const string SigningKey = "integration-test-signing-key-0123456789-abcdefghij";

    [Fact]
    public async Task A_rotated_token_presented_again_later_ends_every_session_of_the_user()
    {
        var user = await NewUserAsync();
        var first = await factory.LoginAsync(user.Username, user.Password);
        using var client = factory.CreateClient();
        var second = await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeAsync(HttpStatusCode.OK);
        // A refresh token rotated long ago, past the grace a retrying client gets. The audit log is append-only,
        // so the rotation of an older login is written as it would stand, five minutes back.
        var old = await factory.LoginAsync(user.Username, user.Password);
        var oldTokenId = JsonDocument.Parse(FromBase64Url(old.RefreshToken.Split('.')[1])).RootElement.GetProperty("jti").GetString()!;
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO audit_log (user_id, entity_type, entity_id, action, payload_json, created_at) VALUES ({user.Id}, 'RefreshToken', {long.Parse(oldTokenId)}, 'REVOKE', '{{\"reason\": \"ROTATED\"}}'::jsonb, now() - interval '5 minutes')"));
        first = old;

        // The spent token comes back: someone else has a copy.
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_REUSED");

        // Whoever held the newer pair loses it too: refresh and access token alike.
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = second.GetProperty("refreshToken").GetString() }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "SESSION_REVOKED");
        using var holder = Bearer(second.GetProperty("accessToken").GetString()!);
        await (await holder.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "SESSION_REVOKED");
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "AppUser" && a.EntityId == user.Id && a.Action == "REFRESH_REUSE")));

        // Signing in again works.
        await factory.LoginAsync(user.Username, user.Password);
    }

    [Fact]
    public async Task A_rotated_token_presented_again_at_once_is_a_retry_and_ends_nothing()
    {
        var user = await NewUserAsync();
        var first = await factory.LoginAsync(user.Username, user.Password);
        using var client = factory.CreateClient();
        var second = await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeAsync(HttpStatusCode.OK);

        // The same client retries a request that timed out, though the server had rotated the token.
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = first.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_ALREADY_USED");

        // The pair the first request received still works.
        using var holder = Bearer(second.GetProperty("accessToken").GetString()!);
        await (await holder.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = second.GetProperty("refreshToken").GetString() }))
            .ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_refreshes_at_once_the_loser_is_told_to_use_the_winners_pair()
    {
        var user = await NewUserAsync();
        var session = await factory.LoginAsync(user.Username, user.Password);
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }),
            client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], responses.Select(r => r.StatusCode).Order());
        await responses.Single(r => r.StatusCode == HttpStatusCode.Unauthorized)
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_ALREADY_USED");
        var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).JsonAsync();
        using var holder = Bearer(winner.GetProperty("accessToken").GetString()!);
        await (await holder.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_logged_out_or_unreadable_refresh_token_says_so()
    {
        var user = await NewUserAsync();
        var session = await factory.LoginAsync(user.Username, user.Password);
        using var client = Bearer(session.AccessToken);
        await (await client.PostAsJsonAsync(Logout, new { refreshToken = session.RefreshToken })).ShouldBeAsync(HttpStatusCode.NoContent);

        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_REVOKED");
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = "not-a-token" }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_INVALID");
        // Logout ends the session of that refresh token only: the access token lives out its 15 minutes.
        await (await client.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused_with_ACCOUNT_INACTIVE()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var user = await NewUserAsync();
        var session = await factory.LoginAsync(user.Username, user.Password);

        await (await admin.PostAsync($"/api/v1/users/{user.Id}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);

        using var client = Bearer(session.AccessToken);
        await (await client.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "ACCOUNT_INACTIVE");
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "ACCOUNT_INACTIVE");
    }

    [Fact]
    public async Task A_role_change_ends_the_session_with_SESSION_REVOKED_and_a_new_sign_in_carries_the_new_role()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var user = await NewUserAsync();
        var session = await factory.LoginAsync(user.Username, user.Password);

        await (await admin.PutAsJsonAsync($"/api/v1/users/{user.Id}", new
        {
            email = $"{user.Username}@brewforge.local", fullName = "Rules Test", role = "TRAINER",
            branchId = await factory.BranchIdAsync("B01"),
        })).ShouldBeAsync(HttpStatusCode.OK);

        using var client = Bearer(session.AccessToken);
        await (await client.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "SESSION_REVOKED");
        await (await client.PostAsJsonAsync(Refresh, new { refreshToken = session.RefreshToken }))
            .ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "SESSION_REVOKED");

        var again = await factory.LoginAsync(user.Username, user.Password);
        using var fresh = Bearer(again.AccessToken);
        Assert.Equal("TRAINER", (await (await fresh.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task An_expired_access_token_says_TOKEN_EXPIRED_a_forged_one_TOKEN_INVALID_and_none_no_rule()
    {
        var user = await NewUserAsync();
        var session = await factory.LoginAsync(user.Username, user.Password);

        using var resigned = Bearer(Resign(session.AccessToken, _ => { }));
        await (await resigned.GetAsync(Me)).ShouldBeAsync(HttpStatusCode.OK);   // the test signs as the server does
        using var expired = Bearer(Resign(session.AccessToken, payload =>
        {
            // Issued 20 minutes ago for its 15: a token that simply ran out.
            var issued = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();
            payload["iat"] = issued;
            payload["nbf"] = issued;
            payload["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        }));
        await (await expired.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_EXPIRED");

        using var forged = Bearer(session.AccessToken[..^4] + "AAAA");
        await (await forged.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", rule: "TOKEN_INVALID");

        using var anonymous = factory.CreateClient();
        var none = await (await anonymous.GetAsync(Me)).ShouldBeErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("rule").ValueKind);

        // Signed in but not allowed is a 403, never one of these rules.
        using var trainee = Bearer(session.AccessToken);
        var forbidden = await (await trainee.GetAsync("/api/v1/users")).ShouldBeErrorAsync(HttpStatusCode.Forbidden);
        Assert.NotEqual("TOKEN_INVALID", forbidden.GetProperty("rule").ToString());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(long Id, string Username, string Password)> NewUserAsync()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var name = $"be3{Guid.NewGuid():N}"[..14];
        const string password = "Rules-Test-Passw0rd";
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            // A branch of no class or test cohort: a trainee here is never swept into a whole-branch assignment.
            username = name, email = $"{name}@brewforge.local", password, fullName = "Rules Test",
            role = "TRAINEE", branchId = await factory.BranchIdAsync("B03"),
        })).ShouldBeAsync(HttpStatusCode.Created);
        return (created.GetProperty("id").GetInt64(), name, password);
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The token with its payload changed and signed again with the test key, as the server would have.</summary>
    private static string Resign(string jwt, Action<JsonObject> change)
    {
        var parts = jwt.Split('.');
        var payload = JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject();
        change(payload);
        var unsigned = $"{parts[0]}.{ToBase64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()))}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.ASCII.GetBytes(unsigned));
        return $"{unsigned}.{ToBase64Url(signature)}";
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
