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
}
