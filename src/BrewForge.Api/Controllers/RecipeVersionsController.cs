using BrewForge.Api.Auth;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Recipes;
using BrewForge.Application.Recipes.Drafting;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Files;
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
    RecipeDraftingService drafting, RecipeReleaseService release) : ControllerBase
{
    /// <summary>The full tree: steps, dependencies and ingredients.</summary>
    [HttpGet("{id:long}"), Authorize(Policy = Policies.HeadOffice)]
    public Task<RecipeVersionDto> Get(long id, CancellationToken cancellationToken) =>
        recipes.GetVersionAsync(id, cancellationToken);

    /// <summary>
    /// What differs from an earlier version of the same recipe, step by step.
    /// <c>?against=</c> names that version; without it, the one that was in
    /// production before this one. Not in the contract table.
    /// </summary>
    [HttpGet("{id:long}/diff"), Authorize(Policy = Policies.HeadOffice)]
    public Task<RecipeVersionDiffDto> Diff(long id, [FromQuery] long? against, CancellationToken cancellationToken) =>
        recipes.DiffAsync(id, against, cancellationToken);

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

    /// <summary>
    /// Transcribes the chain's own document for an existing drink into the
    /// draft. Either <c>{ documentText, fileName? }</c> as JSON, or
    /// <c>multipart/form-data</c> with one .txt, .md or .docx in the field
    /// <c>file</c>. Not in the contract table. 409 unless the recipe is of
    /// origin EXISTING.
    /// </summary>
    [HttpPost("{id:long}/extract-document"), Authorize(Policy = Policies.RdSpecialist)]
    [RequestSizeLimit(DocumentTextReader.MaxBytes + 64 * 1024)]
    public async Task<ExtractionResultDto> ExtractDocument(long id, [FromServices] IDocumentTextReader reader,
        CancellationToken cancellationToken)
    {
        // Read here rather than bound: the body is a form or JSON, and a bound parameter admits only one of them.
        ExtractDocumentRequest request;
        if (Request.HasFormContentType)
        {
            var file = (await Request.ReadFormAsync(cancellationToken)).Files.GetFile("file")
                       ?? throw DomainException.Validation("A document is required in the field 'file'.",
                           new ErrorDetail("file", "is required"));
            await using var content = file.OpenReadStream();
            request = new ExtractDocumentRequest(await reader.ReadAsync(content, file.FileName, cancellationToken),
                file.FileName);
        }
        else if (Request.HasJsonContentType())
        {
            request = await Request.ReadFromJsonAsync<ExtractDocumentRequest>(cancellationToken)
                      ?? new ExtractDocumentRequest(null, null);
        }
        else
        {
            request = new ExtractDocumentRequest(null, null);
        }
        return await drafting.ExtractAsync(id, request, cancellationToken);
    }

    /// <summary>
    /// The report of the last extraction into this version: the document,
    /// the passage each step was taken from and whether it is in the
    /// document, and what could not be expressed. Not in the contract table.
    /// </summary>
    [HttpGet("{id:long}/extraction"), Authorize(Policy = Policies.HeadOffice)]
    public Task<ExtractionReportDto> Extraction(long id, CancellationToken cancellationToken) =>
        drafting.GetExtractionAsync(id, cancellationToken);

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

    /// <summary>UC-09. <c>{ "decisions": [ { "stepId": 901, "action": "ACCEPT" | "EDIT" | "REJECT", "editedText": "..." } ] }</c></summary>
    [HttpPost("{id:long}/review"), Authorize(Policy = Policies.RdManager)]
    public Task<ReviewResultDto> Review(long id, ReviewRequest request, CancellationToken cancellationToken) =>
        release.ReviewAsync(id, request, cancellationToken);

    /// <summary>
    /// UC-10. Re-validates, then seals. 409 MSG-E08 if master data changed,
    /// 409 BR-12 if the caller is the author, 409 BR-02 if the recipe would
    /// have two released versions. Accepts an <c>Idempotency-Key</c> header.
    /// </summary>
    [HttpPost("{id:long}/release"), Authorize(Policy = Policies.RdManager)]
    public Task<ReleaseResultDto> Release(long id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        release.ReleaseAsync(id, idempotencyKey, cancellationToken);

    /// <summary>BR-04. Creates a new version whose content is copied from this one. Never restores in place.</summary>
    [HttpPost("{id:long}/rollback"), Authorize(Policy = Policies.RdManager)]
    public async Task<ActionResult<RecipeVersionDto>> Rollback(long id, CancellationToken cancellationToken)
    {
        var created = await release.RollbackAsync(id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }
}
