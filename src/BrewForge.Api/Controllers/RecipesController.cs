using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-05 - API contract section 4. Reads: head-office roles. Authoring:
/// RD_SPECIALIST. A recipe and its versions are never deleted.
/// </summary>
[ApiController]
[Route("api/v1/recipes")]
public sealed class RecipesController(RecipeService recipes) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.HeadOffice)]
    public Task<PagedResult<RecipeDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? category, [FromQuery] string? origin, [FromQuery] string? status,
        CancellationToken cancellationToken) =>
        recipes.ListAsync(paging, q, category, origin, status, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.RdSpecialist)]
    public async Task<ActionResult<RecipeDto>> Create(CreateRecipeRequest request,
        CancellationToken cancellationToken)
    {
        var created = await recipes.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>
    /// UC-26. The body is the structured recipe as entered from the current
    /// document: <c>{ recipeCode, name, category, steps }</c>. The origin is
    /// forced to EXISTING.
    /// </summary>
    [HttpPost("import-existing"), Authorize(Policy = Policies.RdSpecialist)]
    public async Task<ActionResult<ImportedRecipeDto>> ImportExisting(ImportExistingRecipeRequest request,
        [FromServices] RecipeReleaseService release, CancellationToken cancellationToken)
    {
        var imported = await release.ImportExistingAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = imported.Recipe.Id }, imported);
    }

    /// <summary>Includes the id of the released version, if there is one.</summary>
    [HttpGet("{id:long}"), Authorize(Policy = Policies.HeadOffice)]
    public Task<RecipeDto> Get(long id, CancellationToken cancellationToken) =>
        recipes.GetAsync(id, cancellationToken);

    /// <summary>Version history (SCR-12): state, validation outcome, approver and release time.</summary>
    [HttpGet("{id:long}/versions"), Authorize(Policy = Policies.HeadOffice)]
    public Task<IReadOnlyList<RecipeVersionSummaryDto>> ListVersions(long id, CancellationToken cancellationToken) =>
        recipes.ListVersionsAsync(id, cancellationToken);

    /// <summary>Creates a DRAFT version. The body is optional: <c>{ "copyFromVersionId": 310 }</c>.</summary>
    [HttpPost("{id:long}/versions"), Authorize(Policy = Policies.RdSpecialist)]
    public async Task<ActionResult<RecipeVersionDto>> CreateVersion(long id,
        [FromBody] CreateVersionRequest? request, CancellationToken cancellationToken)
    {
        var created = await recipes.CreateVersionAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(RecipeVersionsController.Get), "RecipeVersions", new { id = created.Id },
            created);
    }
}
