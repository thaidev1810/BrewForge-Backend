using BrewForge.Api.Auth;
using BrewForge.Application.Training;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// The learning path: what the training regulation makes mandatory for a
/// member of staff, stage by stage, and where they stand on it. Not in the
/// API contract.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class LearningPathController(LearningPathService paths) : ControllerBase
{
    /// <summary>The caller's own path.</summary>
    [HttpGet("me/learning-path"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<LearningPathDto> Mine(CancellationToken cancellationToken) => paths.MyPathAsync(cancellationToken);

    [HttpGet("learning-paths/{userId:long}"), AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public Task<LearningPathDto> Get(long userId, CancellationToken cancellationToken) =>
        paths.GetPathAsync(userId, cancellationToken);
}
