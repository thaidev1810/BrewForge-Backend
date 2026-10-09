using System.Net;
using System.Text;
using System.Text.Json;
using BrewForge.Api.Errors;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Common;
using BrewForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrewForge.Api.Tests;

/// <summary>API contract section 1: every 4xx and 5xx uses the same envelope.</summary>
[Collection(ApiCollection.Name)]
public sealed class ErrorEnvelopeTests(BrewForgeApiFactory factory)
{
    [Fact]
    public async Task Unknown_route_is_404_in_the_envelope()
    {
        using var client = await factory.ClientForAsync(TestUsers.Admin);

        await (await client.GetAsync("/api/v1/no-such-resource")).ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Unknown_id_is_404_in_the_envelope()
    {
        using var client = await factory.ClientForAsync(TestUsers.Admin);

        await (await client.GetAsync("/api/v1/ingredients/999999999"))
            .ShouldBeErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Malformed_json_is_400_in_the_envelope()
    {
        using var client = await factory.ClientForAsync(TestUsers.Admin);
        using var content = new StringContent("{ \"ingredientCode\": ", Encoding.UTF8, "application/json");

        await (await client.PostAsync("/api/v1/ingredients", content))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Unhandled_exception_is_500_and_leaks_nothing()
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var middleware = new ErrorEnvelopeMiddleware(
            _ => throw new InvalidOperationException("secret connection string and a stack trace"),
            NullLogger<ErrorEnvelopeMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        var envelope = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("INTERNAL_ERROR", envelope.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at ", body);
    }

    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.Unauthenticated, 401)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.RuleViolation, 409)]
    [InlineData(ErrorKind.Refused, 422)]
    public void Each_kind_of_refusal_has_the_status_of_the_contract(ErrorKind kind, int status) =>
        Assert.Equal(status, ErrorEnvelope.StatusCodeOf(kind));

    /// <summary>
    /// A rule the database enforces must reach the client as a 409 naming the
    /// rule, never as a 500 - for example when two requests race past the
    /// domain guard.
    /// </summary>
    [Theory]
    [InlineData("23505", "ux_recipe_one_released", "BR-02")]
    [InlineData("23505", "recipe_version_recipe_id_version_no_key", "BR-03")]
    [InlineData("23514", "recipe_version_check", "BR-12")]
    [InlineData("23514", "step_dependency_check", "BR-09")]
    [InlineData("23505", "sales_record_branch_id_recipe_id_trading_date_key", "BR-25")]
    [InlineData("23505", "ux_pilot_active_version", "BR-28")]
    [InlineData("23505", "course_module_course_id_module_type_key", "BR-29")]
    [InlineData("23505", "ingredient_ingredient_code_key", "UNIQUE")]
    public void Database_constraint_violation_is_translated_to_its_rule(string sqlState, string constraint,
        string rule)
    {
        var refusal = PostgresErrorTranslator.Translate(
            new PostgresException("violation", "ERROR", "ERROR", sqlState, constraintName: constraint));

        Assert.NotNull(refusal);
        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
        Assert.Equal(rule, refusal.Rule);
    }

    [Fact]
    public void Immutability_trigger_is_translated_to_BR_01()
    {
        var refusal = PostgresErrorTranslator.Translate(new PostgresException(
            "recipe_version 1 is released and immutable (BR-01)", "ERROR", "ERROR", "P0001"));

        Assert.NotNull(refusal);
        Assert.Equal("BR-01", refusal.Rule);
        Assert.Equal("MSG-E06", refusal.Code);
    }

    [Fact]
    public void Unrelated_exception_is_not_translated() =>
        Assert.Null(PostgresErrorTranslator.Translate(new InvalidOperationException("nothing to do with the database")));
}
