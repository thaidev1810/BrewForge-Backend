using BrewForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// BE-04: which build is running and whether its database has every schema script, so a tester can tell the
/// server under test is the commit they mean before retesting. Anonymous, as a deployment check; it reveals no
/// secret (no connection string, configuration value or host).
/// </summary>
[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(SystemInfoReader reader, IHostEnvironment environment) : ControllerBase
{
    /// <summary>Version, commit, environment, start time and schema state. <c>ready</c> is the one-word answer.</summary>
    [HttpGet("info"), AllowAnonymous]
    public Task<SystemInfoDto> Info(CancellationToken cancellationToken) => reader.ReadAsync(environment.EnvironmentName, cancellationToken);
}
