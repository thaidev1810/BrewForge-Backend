using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BrewForge.Api.Tests;

/// <summary>
/// Report 3 section 3.1.3 and SE-03: role-based access is enforced by the
/// server on every endpoint. For each of the eight roles this asserts the
/// exact set of endpoints it may reach.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class AuthorizationMatrixTests(BrewForgeApiFactory factory)
{
    public static TheoryData<RoleName> Roles => [.. Enum.GetValues<RoleName>()];

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Role_reaches_exactly_the_endpoints_the_matrix_grants(RoleName role)
    {
        using var client = await factory.ClientForAsync(role);
        var failures = new List<string>();

        foreach (var entry in AuthorizationMatrix.Entries.Where(e => !e.Anonymous))
        {
            using var response = await SendAsync(client, entry);
            var allowed = entry.Allowed.Contains(role);

            if (allowed && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                failures.Add($"{entry.Key}: should be reachable but answered {(int)response.StatusCode}");
            }
            else if (!allowed && response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{entry.Key}: should be 403 but answered {(int)response.StatusCode}");
            }
            else if (!allowed)
            {
                await response.ShouldBeErrorAsync(HttpStatusCode.Forbidden, "MSG-E01");
            }
        }

        Assert.True(failures.Count == 0, $"{role}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public async Task Anonymous_caller_reaches_only_login_and_refresh()
    {
        using var client = factory.CreateClient();
        var failures = new List<string>();

        foreach (var entry in AuthorizationMatrix.Entries)
        {
            using var response = await SendAsync(client, entry);
            var rejected = response.StatusCode == HttpStatusCode.Unauthorized;

            if (entry.Anonymous && rejected) failures.Add($"{entry.Key}: should be open to an anonymous caller");
            if (!entry.Anonymous && !rejected)
            {
                failures.Add($"{entry.Key}: should be 401 but answered {(int)response.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The guard that keeps the matrix honest: an endpoint that is added
    /// without deciding who may reach it fails here.
    /// </summary>
    [Fact]
    public void Every_endpoint_of_the_api_is_in_the_matrix_and_the_reverse()
    {
        var actual = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("api/v1/", StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {Normalize(endpoint.RoutePattern.RawText!)}"))
            .ToHashSet();
        var expected = AuthorizationMatrix.Entries.Select(e => e.Key).ToHashSet();

        Assert.Empty(actual.Except(expected).Order());   // endpoints nobody decided on
        Assert.Empty(expected.Except(actual).Order());   // matrix rows with no endpoint
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, AuthorizationMatrix.Entry entry)
    {
        var request = new HttpRequestMessage(new HttpMethod(entry.Method), entry.Path);
        if (entry.Method is "POST" or "PUT")
        {
            // An empty object: enough to pass authorization, never enough to change anything.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        return client.SendAsync(request);
    }

    // "api/v1/users/{id:long}" -> "/users/{id}"
    private static string Normalize(string rawRoute) =>
        RouteConstraint().Replace(rawRoute["api/v1".Length..], "{$1}");

    [GeneratedRegex(@"\{(\w+)(?::[^}]+)?\}")]
    private static partial Regex RouteConstraint();
}
