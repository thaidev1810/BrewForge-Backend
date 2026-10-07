using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrewForge.Api.Tests.Infrastructure;

public static class HttpAssertions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(BrewForgeApiFactory.Json);

    /// <summary>Asserts the status and returns the body, with the body in the failure message.</summary>
    public static async Task<JsonElement> ShouldBeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        return body.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(body, BrewForgeApiFactory.Json);
    }

    /// <summary>
    /// Asserts a refusal in the error envelope of the API contract: the
    /// status, the code, the rule where one applies, and a trace id.
    /// </summary>
    public static async Task<JsonElement> ShouldBeErrorAsync(this HttpResponseMessage response,
        HttpStatusCode expected, string? code = null, string? rule = null)
    {
        var envelope = await response.ShouldBeAsync(expected);

        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("details").ValueKind);
        Assert.True(envelope.TryGetProperty("rule", out var actualRule), "the envelope has no 'rule' property");

        if (code is not null) Assert.Equal(code, envelope.GetProperty("code").GetString());
        if (rule is not null) Assert.Equal(rule, actualRule.GetString());
        return envelope;
    }

    public static IReadOnlyList<string> DetailFields(this JsonElement envelope) =>
        [.. envelope.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("field").GetString()!)];
}
