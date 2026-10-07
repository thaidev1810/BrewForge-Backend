using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>UC-01 - API contract section 3. There is no DELETE: a user is deactivated (BR-16).</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = Policies.Admin)]
public sealed class UsersController(UserService users) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<UserDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? role, [FromQuery] long? branchId, [FromQuery] string? status,
        CancellationToken cancellationToken) =>
        users.ListAsync(paging, q, role, branchId, status, cancellationToken);

    [HttpGet("{id:long}")]
    public Task<UserDto> Get(long id, CancellationToken cancellationToken) => users.GetAsync(id, cancellationToken);

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var created = await users.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    public Task<UserDto> Update(long id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        users.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate")]
    public Task<UserDto> Deactivate(long id, CancellationToken cancellationToken) =>
        users.DeactivateAsync(id, cancellationToken);
}
