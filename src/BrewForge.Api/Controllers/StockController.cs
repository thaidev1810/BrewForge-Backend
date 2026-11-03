using BrewForge.Api.Auth;
using BrewForge.Application.Inventory;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// Periodic stock and ingredient loss control. A branch manager records what their branch receives and
/// counts on hand; the loss report compares the stock used between two counts with what the recipes say
/// the sales should have used. Not in the API contract.
/// </summary>
[ApiController]
[Route("api/v1/stock")]
public sealed class StockController(StockService stock) : ControllerBase
{
    /// <summary>Records a stock receipt for the caller's branch: <c>{ ingredientId, quantity, unit, receivedOn }</c>.</summary>
    [HttpPost("receipts"), Authorize(Policy = Policies.BranchManager)]
    public async Task<ActionResult<StockMovementDto>> Receive(StockReceiptRequest request,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await stock.RecordReceiptAsync(request, cancellationToken));

    /// <summary>Records a physical count for the caller's branch: <c>{ ingredientId, quantity, unit, countedOn }</c>.</summary>
    [HttpPost("counts"), Authorize(Policy = Policies.BranchManager)]
    public async Task<ActionResult<StockMovementDto>> Count(StockCountRequest request,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await stock.RecordCountAsync(request, cancellationToken));

    /// <summary>
    /// The ingredient loss of a branch between a count on <c>from</c> and one on <c>to</c>. A branch
    /// manager sees their own branch; head office passes <c>branchId</c>.
    /// </summary>
    [HttpGet("loss-report"), AuthorizeRoles(RoleName.BranchManager, RoleName.RdManager, RoleName.Admin)]
    public Task<StockLossReportDto> LossReport([FromQuery] long? branchId, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        stock.LossReportAsync(branchId, from, to, cancellationToken);
}
