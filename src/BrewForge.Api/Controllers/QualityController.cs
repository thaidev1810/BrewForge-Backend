using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Quality;
using BrewForge.Application.Training;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// The Quality Auditor's branch audits. Only an auditor records one; head office
/// reads them all and a branch manager reads those of their own branch. There is
/// no PUT and no DELETE: an audit is a record of what was seen (BR-37).
/// </summary>
[ApiController]
[Route("api/v1/branch-audits")]
[AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager, RoleName.Admin, RoleName.BranchManager)]
public sealed class BranchAuditsController(QualityAuditService audits) : ControllerBase
{
    /// <summary><c>?branchId=&amp;recipeVersionId=&amp;result=PASSED|FAILED&amp;from=&amp;to=</c>, latest first.</summary>
    [HttpGet]
    public Task<PagedResult<BranchAuditDto>> List([FromQuery] PageQuery paging, [FromQuery] long? branchId,
        [FromQuery] long? recipeVersionId, [FromQuery] string? result, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        audits.ListAsync(paging, branchId, recipeVersionId, result, from, to, cancellationToken);

    /// <summary>The auditor dashboard. The current month unless <c>from</c> and <c>to</c> say otherwise.</summary>
    [HttpGet("summary"), AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager, RoleName.Admin)]
    public Task<AuditSummaryDto> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        audits.SummaryAsync(from, to, cancellationToken);

    /// <summary>The photo of a checklist item. Returns the URL to put in the item's <c>evidenceUrl</c>.</summary>
    [HttpPost("evidence"), Authorize(Policy = Policies.QualityAuditor)]
    [RequestSizeLimit(EvidenceService.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = EvidenceService.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<EvidenceDto>> UploadEvidence([FromServices] EvidenceService evidence,
        CancellationToken cancellationToken)
    {
        var file = Request.HasFormContentType
            ? (await Request.ReadFormAsync(cancellationToken)).Files.GetFile("file")
            : null;
        if (file is null)
        {
            throw DomainException.Validation("An image is required, sent as multipart/form-data in the field 'file'.",
                new ErrorDetail("file", "is required"));
        }
        await using var content = file.OpenReadStream();
        return StatusCode(StatusCodes.Status201Created, await evidence.SaveAsync(file.FileName, content, cancellationToken));
    }

    [HttpGet("evidence/{**key}")]
    public async Task<IActionResult> Evidence(string key, [FromServices] EvidenceService evidence,
        CancellationToken cancellationToken)
    {
        var photo = await evidence.OpenAsync(key, cancellationToken);
        return File(photo.Content, photo.ContentType);
    }

    [HttpGet("{id:long}")]
    public Task<BranchAuditDto> Get(long id, CancellationToken cancellationToken) =>
        audits.GetAsync(id, cancellationToken);

    /// <summary>
    /// Records an audit. The score and the verdict are computed from the items;
    /// a failed audit raises its corrective action in the same call.
    /// </summary>
    [HttpPost, Authorize(Policy = Policies.QualityAuditor)]
    public async Task<ActionResult<BranchAuditDto>> Create(BranchAuditRequest request, CancellationToken cancellationToken)
    {
        var created = await audits.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }
}

/// <summary>What a failed audit obliges a branch to do.</summary>
[ApiController]
[Route("api/v1/corrective-actions")]
[AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager, RoleName.Admin, RoleName.BranchManager)]
public sealed class CorrectiveActionsController(CorrectiveActionService actions) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<CorrectiveActionDto>> List([FromQuery] PageQuery paging, [FromQuery] long? branchId,
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        actions.ListAsync(paging, branchId, status, cancellationToken);

    [HttpGet("{id:long}")]
    public Task<CorrectiveActionDto> Get(long id, CancellationToken cancellationToken) =>
        actions.GetAsync(id, cancellationToken);

    /// <summary>OPEN to IN_PROGRESS: the branch has started the work.</summary>
    [HttpPost("{id:long}/start"), AuthorizeRoles(RoleName.QualityAuditor, RoleName.BranchManager)]
    public Task<CorrectiveActionDto> Start(long id, CancellationToken cancellationToken) =>
        actions.StartAsync(id, cancellationToken);

    /// <summary>The staff the action names and the published courses tied to the audited SOP, for the auditor to pick from.</summary>
    [HttpGet("{id:long}/retraining-options"), Authorize(Policy = Policies.QualityAuditor)]
    public Task<RetrainingOptionsDto> RetrainingOptions(long id, CancellationToken cancellationToken) =>
        actions.RetrainingOptionsAsync(id, cancellationToken);

    /// <summary>Enrols the staff named on the action on a course (<c>{ courseId, deadline }</c>), reporting the outcome per employee.</summary>
    [HttpPost("{id:long}/assign-retraining"), Authorize(Policy = Policies.QualityAuditor)]
    public Task<RetrainingResultDto> AssignRetraining(long id, RetrainingRequest request,
        CancellationToken cancellationToken) =>
        actions.AssignRetrainingAsync(id, request, cancellationToken);

    /// <summary>The auditor closes the action once the deviation is corrected.</summary>
    [HttpPost("{id:long}/close"), Authorize(Policy = Policies.QualityAuditor)]
    public Task<CorrectiveActionDto> Close(long id, CloseCorrectiveActionRequest request,
        CancellationToken cancellationToken) =>
        actions.CloseAsync(id, request, cancellationToken);
}
