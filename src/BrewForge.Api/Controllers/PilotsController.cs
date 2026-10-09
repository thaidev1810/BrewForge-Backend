using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Launch;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>UC-21, UC-24 and UC-25 - API contract section 10.</summary>
[ApiController]
[Route("api/v1/pilots")]
public sealed class PilotsController(PilotService pilots) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.RdManager)]
    public Task<PagedResult<PilotDto>> List([FromQuery] PageQuery paging, [FromQuery] string? state,
        [FromQuery] long? recipeVersionId, CancellationToken cancellationToken) =>
        pilots.ListAsync(paging, state, recipeVersionId, cancellationToken);

    /// <summary>UC-21. 409 BR-28 if the version already has a pilot in DRAFT or RUNNING state.</summary>
    [HttpPost, Authorize(Policy = Policies.RdManager)]
    public async Task<ActionResult<PilotDto>> Create(PilotRequest request, CancellationToken cancellationToken)
    {
        var created = await pilots.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>A branch manager sees a pilot their branch takes part in, and of it their own branch.</summary>
    [HttpGet("{id:long}"), AuthorizeRoles(RoleName.RdManager, RoleName.BranchManager)]
    public Task<PilotDto> Get(long id, CancellationToken cancellationToken) => pilots.GetAsync(id, cancellationToken);

    /// <summary>409 BR-26 once the pilot is RUNNING.</summary>
    [HttpPut("{id:long}"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotDto> Update(long id, PilotRequest request, CancellationToken cancellationToken) =>
        pilots.UpdateAsync(id, request, cancellationToken);

    /// <summary>Certified staff per pilot branch against the threshold.</summary>
    [HttpGet("{id:long}/readiness"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotReadinessDto> Readiness(long id, CancellationToken cancellationToken) =>
        pilots.ReadinessAsync(id, cancellationToken);

    /// <summary>DRAFT to RUNNING. Freezes the criteria.</summary>
    [HttpPost("{id:long}/start"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotDto> Start(long id, CancellationToken cancellationToken) =>
        pilots.StartAsync(id, cancellationToken);

    /// <summary>DRAFT or RUNNING to CANCELLED. Not in the contract table; the state model gives it to the R&amp;D Manager.</summary>
    [HttpPost("{id:long}/cancel"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotDto> Cancel(long id, CancellationToken cancellationToken) =>
        pilots.CancelAsync(id, cancellationToken);

    /// <summary>The launch gate. 409 BR-23 if the branch is not READY.</summary>
    [HttpPost("{id:long}/branches/{branchId:long}/go-live"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotDto> GoLive(long id, long branchId, CancellationToken cancellationToken) =>
        pilots.GoLiveAsync(id, branchId, cancellationToken);

    /// <summary>UC-24. Verdict per criterion, per branch, per week.</summary>
    [HttpGet("{id:long}/evaluation"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotEvaluation> Evaluation(long id, CancellationToken cancellationToken) =>
        pilots.EvaluationAsync(id, cancellationToken);

    /// <summary>UC-25. <c>{ decision: ROLLOUT | REVISE | DISCONTINUE, note }</c>. 409 BR-27 unless the pilot is ENDED.</summary>
    [HttpPost("{id:long}/decision"), Authorize(Policy = Policies.RdManager)]
    public Task<PilotDto> Decide(long id, DecisionRequest request, CancellationToken cancellationToken) =>
        pilots.DecideAsync(id, request, cancellationToken);
}

/// <summary>Coverage per branch and drink - API contract section 10.</summary>
[ApiController]
[Route("api/v1/branch-launch-status")]
public sealed class BranchLaunchStatusController(LaunchStatusService launches) : ControllerBase
{
    /// <summary><c>?branchId=&amp;recipeId=&amp;status=</c>. A branch manager sees their own branch.</summary>
    [HttpGet, AuthorizeRoles(RoleName.RdManager, RoleName.BranchManager)]
    public Task<IReadOnlyList<BranchLaunchStatusDto>> List([FromQuery] long? branchId, [FromQuery] long? recipeId,
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        launches.ListAsync(branchId, recipeId, status, cancellationToken);

    /// <summary>LIVE to WITHDRAWN. Not in the contract table; the state model gives it to the R&amp;D Manager.</summary>
    [HttpPost("{id:long}/withdraw"), Authorize(Policy = Policies.RdManager)]
    public Task<BranchLaunchStatusDto> Withdraw(long id, CancellationToken cancellationToken) =>
        launches.WithdrawAsync(id, cancellationToken);
}
