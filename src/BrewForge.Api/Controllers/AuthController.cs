using BrewForge.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>SF-02 - API contract section 2.</summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous]
    public Task<LoginResponse> Login(LoginRequest request, CancellationToken cancellationToken) =>
        auth.LoginAsync(request, cancellationToken);

    [HttpPost("refresh"), AllowAnonymous]
    public Task<LoginResponse> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        auth.RefreshAsync(request, cancellationToken);

    /// <summary>Revokes the refresh token sent in the body. Any authenticated role.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        await auth.LogoutAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's user, role and permissions. Any authenticated role.</summary>
    [HttpGet("me")]
    public Task<MeResponse> Me(CancellationToken cancellationToken) => auth.MeAsync(cancellationToken);
}
