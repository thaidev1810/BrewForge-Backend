using BrewForge.Api.Auth;
using BrewForge.Application.Recipes;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-05 to UC-08 - API contract section 4. There is no DELETE: a version is
/// retained forever (BR-01).
/// </summary>
[ApiController]
[Route("api/v1/recipe-versions")]
public sealed class RecipeVersionsController(RecipeService recipes, RecipeValidationService validation,
    RecipeDraftingService drafting) : ControllerBase
{
    /// <summary>The full tree: steps, dependencies and ingredients.</summary>
    [HttpGet("{id:long}"), Authorize(Policy = Policies.HeadOffice)]
    public Task<RecipeVersionDto> Get(long id, CancellationToken cancellationToken) =>
        recipes.GetVersionAsync(id, cancellationToken);

    /// <summary>Replaces the content of a draft. 409 BR-01 on a released version.</summary>
    [HttpPut("{id:long}"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<RecipeVersionDto> Update(long id, RecipeContentRequest request,
        CancellationToken cancellationToken) =>
        recipes.UpdateVersionAsync(id, request, cancellationToken);

    /// <summary>UC-06. Calls the language model under the fixed schema, then validates the draft.</summary>
    [HttpPost("{id:long}/generate-draft"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<DraftResultDto> GenerateDraft(long id, GenerateDraftRequest request,
        CancellationToken cancellationToken) =>
        drafting.GenerateDraftAsync(id, request, cancellationToken);

    /// <summary>UC-07. Runs all three checks. 200 even when a check fails.</summary>
    [HttpPost("{id:long}/validate"), AuthorizeRoles(RoleName.RdSpecialist, RoleName.RdManager)]
    public Task<ValidationResultDto> Validate(long id, CancellationToken cancellationToken) =>
        validation.ValidateAsync(id, cancellationToken);

    /// <summary>UC-08. <c>{ "mode": "AI" }</c> or <c>{ "mode": "MANUAL", "patch": { "steps": [...] } }</c>. 409 after three AI repairs.</summary>
    [HttpPost("{id:long}/repair"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<DraftResultDto> Repair(long id, RepairRequest request, CancellationToken cancellationToken) =>
        drafting.RepairAsync(id, request, cancellationToken);

    /// <summary>DRAFT to VALIDATED once the checks pass. 422 MSG-E03 when they do not.</summary>
    [HttpPost("{id:long}/submit"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<ValidationResultDto> Submit(long id, CancellationToken cancellationToken) =>
        validation.SubmitAsync(id, cancellationToken);
}
