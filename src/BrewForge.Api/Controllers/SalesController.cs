using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Sales;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Infrastructure.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-22 and UC-23 - API contract section 9. There is no endpoint that pulls
/// from a POS system: the integration is one-way and file-based.
/// </summary>
[ApiController]
[Route("api/v1/sales")]
public sealed class SalesController(SalesService sales, PosImportService imports, SalesAnalyticsService analytics)
    : ControllerBase
{
    /// <summary><c>?branchId=&amp;recipeId=&amp;from=&amp;to=</c>. A branch manager sees the sales of their own branch.</summary>
    [HttpGet, AuthorizeRoles(RoleName.BranchManager, RoleName.RdManager, RoleName.QualityAuditor)]
    public Task<PagedResult<SalesRecordDto>> List([FromQuery] PageQuery paging, [FromQuery] long? branchId,
        [FromQuery] long? recipeId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        sales.ListAsync(paging, branchId, recipeId, from, to, cancellationToken);

    /// <summary>
    /// The drinks on sale at the branch on a day, for the entry screen. Not
    /// in the contract table; the drink list of SCR-26 needs it.
    /// </summary>
    [HttpGet("drinks"), Authorize(Policy = Policies.BranchManager)]
    public Task<IReadOnlyList<SellableDrinkDto>> Drinks([FromQuery] long? branchId, [FromQuery] DateOnly? tradingDate,
        CancellationToken cancellationToken) =>
        sales.SellableDrinksAsync(branchId, tradingDate, cancellationToken);

    /// <summary>
    /// UC-22. <c>{ branchId, recipeId, tradingDate, cupsSold }</c>. The
    /// version is resolved by the server. 409 BR-24 if the branch was not
    /// live that day, 409 BR-25 on a duplicate.
    /// </summary>
    [HttpPost, Authorize(Policy = Policies.BranchManager)]
    public async Task<ActionResult<SalesRecordDto>> Create(SalesRecordRequest request,
        CancellationToken cancellationToken)
    {
        var created = await sales.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(List), null, created);
    }

    /// <summary>A correction of the count: <c>{ cupsSold }</c>. Written to the audit log.</summary>
    [HttpPut("{id:long}"), Authorize(Policy = Policies.BranchManager)]
    public Task<SalesRecordDto> Correct(long id, SalesCorrectionRequest request, CancellationToken cancellationToken) =>
        sales.CorrectAsync(id, request, cancellationToken);

    /// <summary>
    /// UC-23. <c>multipart/form-data</c> with one CSV or XLSX in the field
    /// <c>file</c>, in the layout <c>branch_code, drink_code, trading_date,
    /// quantity</c>. Accepts an <c>Idempotency-Key</c> header.
    /// </summary>
    [HttpPost("import"), Authorize(Policy = Policies.BranchManager)]
    [RequestSizeLimit(PosFileReader.MaxBytes + 64 * 1024)]
    public async Task<PosImportResultDto> Import([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // The form is read here rather than bound as a parameter: a bound file makes the
        // framework answer 415 to the wrong media type before it has asked who is calling.
        var file = Request.HasFormContentType
            ? (await Request.ReadFormAsync(cancellationToken)).Files.GetFile("file")
            : null;
        if (file is null)
        {
            throw DomainException.Validation("A file is required, sent as multipart/form-data in the field 'file'.",
                new ErrorDetail("file", "is required"));
        }
        await using var content = file.OpenReadStream();
        return await imports.ImportAsync(content, file.FileName, idempotencyKey, cancellationToken);
    }

    /// <summary>The result of an import: accepted, rejected, and every rejected row with its reason.</summary>
    [HttpGet("import/{jobId}"), Authorize(Policy = Policies.BranchManager)]
    public Task<PosImportResultDto> ImportResult(string jobId, CancellationToken cancellationToken) =>
        imports.GetResultAsync(jobId, cancellationToken);

    /// <summary><c>?recipeVersionId=&amp;groupBy=branch|day|week</c>, with an optional branch, period and control drink.</summary>
    [HttpGet("aggregate"), AuthorizeRoles(RoleName.RdManager, RoleName.TrainingManager)]
    public Task<SalesAggregateDto> Aggregate([FromQuery] long? recipeVersionId, [FromQuery] long? recipeId,
        [FromQuery] string? groupBy, [FromQuery] long? branchId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] long? controlRecipeId, CancellationToken cancellationToken) =>
        analytics.AggregateAsync(recipeVersionId, recipeId, groupBy, branchId, from, to, controlRecipeId,
            cancellationToken);
}
