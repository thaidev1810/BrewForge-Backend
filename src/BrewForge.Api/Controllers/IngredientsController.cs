using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.MasterData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-02 - API contract section 3. Reads: all head-office roles. Writes: ADMIN.
/// There is no DELETE: an ingredient is deactivated (BR-16).
/// </summary>
[ApiController]
[Route("api/v1/ingredients")]
public sealed class IngredientsController(IngredientService ingredients) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.HeadOffice)]
    public Task<PagedResult<IngredientDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? unit, [FromQuery] string? status, CancellationToken cancellationToken) =>
        ingredients.ListAsync(paging, q, unit, status, cancellationToken);

    [HttpGet("{id:long}"), Authorize(Policy = Policies.HeadOffice)]
    public Task<IngredientDto> Get(long id, CancellationToken cancellationToken) =>
        ingredients.GetAsync(id, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<IngredientDto>> Create(IngredientRequest request,
        CancellationToken cancellationToken)
    {
        var created = await ingredients.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}"), Authorize(Policy = Policies.Admin)]
    public Task<IngredientDto> Update(long id, IngredientRequest request, CancellationToken cancellationToken) =>
        ingredients.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate"), Authorize(Policy = Policies.Admin)]
    public Task<IngredientDto> Deactivate(long id, CancellationToken cancellationToken) =>
        ingredients.DeactivateAsync(id, cancellationToken);
}
