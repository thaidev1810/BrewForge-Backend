using BrewForge.Api.Auth;
using BrewForge.Application.Dashboards;
using BrewForge.Application.Training;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-17 - API contract section 8. Certificates can be read and nothing
/// else. There is no POST: a certificate is a consequence of passing, issued
/// by the domain (BR-21). There is no DELETE: it is superseded, never
/// deleted (BR-15).
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class CertificatesController(AssessmentService assessment) : ControllerBase
{
    /// <summary>The caller's skill passport (SCR-20).</summary>
    [HttpGet("me/certificates"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<CertificateDto>> Mine(CancellationToken cancellationToken) =>
        assessment.MyCertificatesAsync(cancellationToken);

    [HttpGet("certificates/{id:long}"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer, RoleName.QualityAuditor)]
    public Task<CertificateDto> Get(long id, CancellationToken cancellationToken) =>
        assessment.GetCertificateAsync(id, cancellationToken);
}

/// <summary>UC-18 - API contract section 12.</summary>
[ApiController]
[Route("api/v1/dashboards")]
public sealed class DashboardsController(DashboardService dashboards) : ControllerBase
{
    /// <summary>Per branch and course: learners by state, overdue, and certified staff (SCR-22).</summary>
    [HttpGet("training-progress")]
    [AuthorizeRoles(RoleName.Trainer, RoleName.RdManager, RoleName.BranchManager, RoleName.QualityAuditor)]
    public Task<IReadOnlyList<TrainingProgressRowDto>> TrainingProgress([FromQuery] long? branchId,
        [FromQuery] long? courseId, CancellationToken cancellationToken) =>
        dashboards.TrainingProgressAsync(branchId, courseId, cancellationToken);

    /// <summary>Per branch and drink: sales in the period beside certificate coverage. Defaults to the last four weeks.</summary>
    [HttpGet("branch-performance")]
    [AuthorizeRoles(RoleName.RdManager, RoleName.BranchManager, RoleName.QualityAuditor)]
    public Task<BranchPerformanceDto> BranchPerformance([FromQuery] long? branchId, [FromQuery] long? recipeId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        dashboards.BranchPerformanceAsync(branchId, recipeId, from, to, cancellationToken);

    /// <summary>UC-31. Per course: pass rate per module, retakes, attendance, time to certification, beside the cups sold (SCR-33).</summary>
    [HttpGet("course-effectiveness"), AuthorizeRoles(RoleName.TrainingManager, RoleName.RdManager)]
    public Task<IReadOnlyList<CourseEffectivenessDto>> CourseEffectiveness([FromQuery] long? courseId,
        CancellationToken cancellationToken) =>
        dashboards.CourseEffectivenessAsync(courseId, cancellationToken);
}
