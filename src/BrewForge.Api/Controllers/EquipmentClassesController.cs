using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.MasterData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-03 - API contract section 3. Reads: all head-office roles. Writes: ADMIN.
/// There is no DELETE: an equipment class is deactivated (BR-16).
/// </summary>
[ApiController]
[Route("api/v1/equipment-classes")]
public sealed class EquipmentClassesController(EquipmentClassService equipment) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.HeadOffice)]
    public Task<PagedResult<EquipmentClassDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        equipment.ListAsync(paging, q, status, cancellationToken);

    [HttpGet("{id:long}"), Authorize(Policy = Policies.HeadOffice)]
    public Task<EquipmentClassDto> Get(long id, CancellationToken cancellationToken) =>
        equipment.GetAsync(id, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<EquipmentClassDto>> Create(EquipmentClassRequest request,
        CancellationToken cancellationToken)
    {
        var created = await equipment.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}"), Authorize(Policy = Policies.Admin)]
    public Task<EquipmentClassDto> Update(long id, EquipmentClassRequest request,
        CancellationToken cancellationToken) =>
        equipment.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate"), Authorize(Policy = Policies.Admin)]
    public Task<EquipmentClassDto> Deactivate(long id, CancellationToken cancellationToken) =>
        equipment.DeactivateAsync(id, cancellationToken);
}
