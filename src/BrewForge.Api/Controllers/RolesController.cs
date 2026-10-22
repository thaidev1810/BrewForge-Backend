using BrewForge.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// G-06: the role catalogue, so the front-end can build its menus and role screens from the
/// same permission codes <c>GET /auth/me</c> returns for the caller. Reference data; any
/// authenticated role may read it, and the server still authorizes every endpoint itself (SE-03).
/// </summary>
[ApiController]
[Route("api/v1/roles")]
public sealed class RolesController(AuthService auth) : ControllerBase
{
    /// <summary>Every role with its permission codes. Any authenticated role.</summary>
    [HttpGet]
    public Task<IReadOnlyList<RoleDto>> List(CancellationToken cancellationToken) => auth.RolesAsync(cancellationToken);
}
