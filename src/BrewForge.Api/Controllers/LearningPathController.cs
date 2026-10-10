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

    /// <summary>
    /// Assigns, self-paced, every course of the path that is open to the user
    /// and that they are neither certified nor enrolled on. A new user gets
    /// this by itself; this is for staff who were there before a course was
    /// published.
    /// </summary>
    [HttpPost("learning-paths/{userId:long}/assign"), AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public Task<PathAssignmentDto> Assign(long userId, CancellationToken cancellationToken) =>
        paths.AssignAsync(userId, cancellationToken);
}
