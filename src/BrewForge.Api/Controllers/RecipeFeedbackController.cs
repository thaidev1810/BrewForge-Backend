using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Recipes;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// Feedback on a recipe from the counter. Not in the API contract: the pack
/// has no way for what staff and customers learn about a drink to reach
/// R&amp;D. Staff who were certified on a drink remark on how it is made,
/// branch staff write down what a customer said, and R&amp;D reviews both.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class RecipeFeedbackController(RecipeFeedbackService feedback) : ControllerBase
{
    /// <summary>
    /// <c>{ source, category, content, rating?, recipeVersionId?, recipeStepId? }</c>.
    /// STAFF: 409 FEEDBACK_REQUIRES_TRAINING unless the caller was certified
    /// on the drink. CUSTOMER: needs a <c>rating</c>, and 409
    /// FEEDBACK_NOT_ON_SALE unless the caller's branch sells the drink.
    /// </summary>
    [HttpPost("recipes/{id:long}/feedback"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer, RoleName.BranchManager)]
    public async Task<ActionResult<RecipeFeedbackDto>> Submit(long id, RecipeFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var created = await feedback.SubmitAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("recipe-feedback/{id:long}")]
    public Task<RecipeFeedbackDto> Get(long id, CancellationToken cancellationToken) =>
        feedback.GetAsync(id, cancellationToken);

}
