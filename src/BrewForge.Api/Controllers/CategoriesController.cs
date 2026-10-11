using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.MasterData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// The drink, ingredient, training and equipment categories. Writes are for
/// ADMIN; every authenticated role may read them. There is no DELETE: a
/// category is deactivated (BR-16).
/// </summary>
[ApiController]
[Route("api/v1/categories")]
public sealed class CategoriesController(CategoryService categories) : ControllerBase
{
    /// <summary><c>?q=&amp;kind=DRINK|INGREDIENT|TRAINING|EQUIPMENT&amp;status=ACTIVE|INACTIVE</c></summary>
    [HttpGet, Authorize]
    public Task<PagedResult<CategoryDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? kind, [FromQuery] string? status, CancellationToken cancellationToken) =>
        categories.ListAsync(paging, q, kind, status, cancellationToken);

    [HttpGet("{id:long}"), Authorize]
    public Task<CategoryDto> Get(long id, CancellationToken cancellationToken) =>
        categories.GetAsync(id, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<CategoryDto>> Create(CategoryRequest request, CancellationToken cancellationToken)
    {
        var created = await categories.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}"), Authorize(Policy = Policies.Admin)]
    public Task<CategoryDto> Update(long id, CategoryRequest request, CancellationToken cancellationToken) =>
        categories.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate"), Authorize(Policy = Policies.Admin)]
    public Task<CategoryDto> Deactivate(long id, CancellationToken cancellationToken) =>
        categories.DeactivateAsync(id, cancellationToken);
}
