using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.MasterData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-04 - API contract section 3. Writes are for ADMIN. Reads are open to
/// every authenticated role, because scheduling a class, setting up a pilot
/// and entering sales all need to name a branch; a store-level caller is
/// limited to its own branch by the persistence query filter.
/// There is no DELETE: a branch is closed (BR-16).
/// </summary>
[ApiController]
[Route("api/v1/branches")]
public sealed class BranchesController(BranchService branches) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<PagedResult<BranchDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        branches.ListAsync(paging, q, status, cancellationToken);

    [HttpGet("{id:long}"), Authorize]
    public Task<BranchDto> Get(long id, CancellationToken cancellationToken) =>
        branches.GetAsync(id, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<BranchDto>> Create(BranchRequest request, CancellationToken cancellationToken)
    {
        var created = await branches.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}"), Authorize(Policy = Policies.Admin)]
    public Task<BranchDto> Update(long id, BranchRequest request, CancellationToken cancellationToken) =>
        branches.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate"), Authorize(Policy = Policies.Admin)]
    public Task<BranchDto> Deactivate(long id, CancellationToken cancellationToken) =>
        branches.DeactivateAsync(id, cancellationToken);
}
