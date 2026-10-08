using BrewForge.Api.Auth;
using BrewForge.Application.Audit;
using BrewForge.Application.Common;
using BrewForge.Application.Impact;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>UC-19 - API contract section 11.</summary>
[ApiController]
[Route("api/v1/impact-analysis")]
[Authorize(Policy = Policies.RdManager)]
public sealed class ImpactAnalysisController(ImpactAnalysisService impact) : ControllerBase
{
    /// <summary>
    /// What-if. <c>{ entityType, entityId }</c> for an Ingredient, a
    /// StandardEquipment or a RecipeVersion. Writes nothing but the
    /// uncommitted rows of the analysis.
    /// </summary>
    [HttpPost]
    public Task<ImpactAnalysisDto> Analyze(ImpactRequest request, CancellationToken cancellationToken) =>
        impact.AnalyzeAsync(request, cancellationToken);

    /// <summary>Affected versions, courses, staff and live branches.</summary>
    [HttpGet("{runId}")]
    public Task<ImpactAnalysisDto> Get(string runId, CancellationToken cancellationToken) =>
        impact.GetAsync(runId, cancellationToken);

    /// <summary>Applies the flags (BR-15): OUT_OF_DATE, NEEDS_RECERT, and the notices. Nothing is deleted.</summary>
    [HttpPost("{runId}/commit")]
    public Task<ImpactAnalysisDto> Commit(string runId, CancellationToken cancellationToken) =>
        impact.CommitAsync(runId, cancellationToken);
}

/// <summary>
/// UC-20 - API contract section 12. The log can be read and nothing else:
/// there is no PUT and no DELETE, here or anywhere.
/// </summary>
[ApiController]
[Route("api/v1/audit-log")]
public sealed class AuditLogController(AuditService audit) : ControllerBase
{
    /// <summary><c>?entityType=&amp;entityId=&amp;action=&amp;userId=&amp;branchId=&amp;from=&amp;to=</c>, latest first.</summary>
    [HttpGet, AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager, RoleName.Admin)]
    public Task<PagedResult<AuditEntryDto>> List([FromQuery] PageQuery paging, [FromQuery] string? entityType,
        [FromQuery] long? entityId, [FromQuery] string? action, [FromQuery] long? userId, [FromQuery] long? branchId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        audit.ListAsync(paging, entityType, entityId, action, userId, branchId, from, to, cancellationToken);

    /// <summary>Follows a version from author to approver to validation result to courses to certified staff.</summary>
    [HttpGet("trace/{entityType}/{entityId:long}"), AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager)]
    public Task<VersionTraceDto> Trace(string entityType, long entityId, CancellationToken cancellationToken) =>
        audit.TraceAsync(entityType, entityId, cancellationToken);
}

/// <summary>UC-20 - API contract section 12.</summary>
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController(ComplianceReportService compliance) : ControllerBase
{
    /// <summary>
    /// <c>?format=xlsx|csv</c>, narrowed by <c>from</c>, <c>to</c>,
    /// <c>branchId</c>, <c>courseId</c> or <c>recipeVersionId</c>. Streams
    /// the file. PDF is out of scope for release 1.0.
    /// </summary>
    [HttpGet("compliance"), AuthorizeRoles(RoleName.QualityAuditor, RoleName.RdManager)]
    public async Task<FileContentResult> Compliance([FromQuery] string? format, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] long? branchId, [FromQuery] long? courseId,
        [FromQuery] long? recipeVersionId, CancellationToken cancellationToken)
    {
        var file = await compliance.ExportAsync(format, from, to, branchId, courseId, recipeVersionId, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
