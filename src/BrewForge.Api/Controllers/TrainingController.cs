using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Training;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>UC-27 - API contract section 6.</summary>
[ApiController]
[Route("api/v1/training-regulations")]
public sealed class TrainingRegulationsController(TrainingRegulationService regulations) : ControllerBase
{
    [HttpGet, AuthorizeRoles(RoleName.TrainingManager, RoleName.Admin)]
    public Task<IReadOnlyList<TrainingRegulationDto>> List(CancellationToken cancellationToken) =>
        regulations.ListAsync(cancellationToken);

    [HttpPost, Authorize(Policy = Policies.TrainingManager)]
    public async Task<ActionResult<TrainingRegulationDto>> Create(TrainingRegulationRequest request,
        CancellationToken cancellationToken)
    {
        var created = await regulations.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(List), created);
    }

    /// <summary>Refused once an enrolment was created under the regulation: add a later one instead.</summary>
    [HttpPut("{id:long}"), Authorize(Policy = Policies.TrainingManager)]
    public Task<TrainingRegulationDto> Update(long id, TrainingRegulationRequest request,
        CancellationToken cancellationToken) =>
        regulations.UpdateAsync(id, request, cancellationToken);
}

/// <summary>UC-29 - API contract section 7.</summary>
[ApiController]
[Route("api/v1/training-needs")]
public sealed class TrainingNeedsController(TrainingRegulationService regulations) : ControllerBase
{
    /// <summary>Who must be trained, according to the regulation in force.</summary>
    [HttpGet, AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public Task<IReadOnlyList<TrainingNeedDto>> List([FromQuery] long? branchId, [FromQuery] long? courseId,
        CancellationToken cancellationToken) =>
        regulations.TrainingNeedsAsync(branchId, courseId, cancellationToken);
}

/// <summary>UC-29 - API contract section 7.</summary>
[ApiController]
[Route("api/v1/training-classes")]
[Authorize(Policy = Policies.Trainer)]
public sealed class TrainingClassesController(TrainingClassService classes) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<TrainingClassDto>> List([FromQuery] PageQuery paging, [FromQuery] string? state,
        [FromQuery] long? branchId, [FromQuery] long? courseId, CancellationToken cancellationToken) =>
        classes.ListAsync(paging, state, branchId, courseId, cancellationToken);

    [HttpPost]
    public async Task<ActionResult<TrainingClassDto>> Create(TrainingClassRequest request,
        CancellationToken cancellationToken)
    {
        var created = await classes.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}")]
    public Task<TrainingClassDto> Get(long id, CancellationToken cancellationToken) =>
        classes.GetAsync(id, cancellationToken);

    [HttpPut("{id:long}")]
    public Task<TrainingClassDto> Update(long id, TrainingClassRequest request, CancellationToken cancellationToken) =>
        classes.UpdateAsync(id, request, cancellationToken);

    /// <summary>409 BR-34 if the trainer is not certified on the bound version.</summary>
    [HttpPost("{id:long}/sessions")]
    public async Task<ActionResult<TrainingSessionDto>> AddSession(long id, SessionRequest request,
        CancellationToken cancellationToken)
    {
        var created = await classes.AddSessionAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id }, created);
    }

    /// <summary>PLANNED to RUNNING. Assigns the course, sets the due date, notifies. Optional body <c>{ traineeIds }</c>.</summary>
    [HttpPost("{id:long}/open")]
    public Task<OpenClassResultDto> Open(long id, [FromBody] OpenClassRequest? request,
        CancellationToken cancellationToken) =>
        classes.OpenAsync(id, request, cancellationToken);

    /// <summary>RUNNING to CLOSED. Freezes attendance.</summary>
    [HttpPost("{id:long}/close")]
    public Task<TrainingClassDto> Close(long id, CancellationToken cancellationToken) =>
        classes.CloseAsync(id, cancellationToken);
}

/// <summary>UC-29 and UC-30 - API contract section 7.</summary>
[ApiController]
[Route("api/v1/training-sessions")]
[Authorize(Policy = Policies.Trainer)]
public sealed class TrainingSessionsController(TrainingClassService classes) : ControllerBase
{
    [HttpPut("{id:long}")]
    public Task<TrainingSessionDto> Update(long id, SessionRequest request, CancellationToken cancellationToken) =>
        classes.UpdateSessionAsync(id, request, cancellationToken);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await classes.DeleteSessionAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:long}/attendance")]
    public Task<AttendanceSheetDto> GetAttendance(long id, CancellationToken cancellationToken) =>
        classes.GetAttendanceAsync(id, cancellationToken);

    /// <summary>UC-30. <c>{ entries: [ { enrollmentId, status, note } ] }</c>. Only while the class is RUNNING.</summary>
    [HttpPut("{id:long}/attendance")]
    public Task<AttendanceSheetDto> RecordAttendance(long id, AttendanceRequest request,
        CancellationToken cancellationToken) =>
        classes.RecordAttendanceAsync(id, request, cancellationToken);
}

/// <summary>
/// UC-14 - API contract section 8. "Own" endpoints are open to TRAINEE and to
/// TRAINER: a trainer must be able to take a course to become certified on a
/// version before teaching it (BR-34). Ownership is enforced by the service.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class EnrollmentsController(LearningService learning) : ControllerBase
{
    /// <summary>My courses, progress and deadlines.</summary>
    [HttpGet("me/enrollments"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<EnrollmentDto>> Mine(CancellationToken cancellationToken) =>
        learning.MyEnrollmentsAsync(cancellationToken);

    [HttpGet("enrollments/{id:long}"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<EnrollmentDto> Get(long id, CancellationToken cancellationToken) =>
        learning.GetAsync(id, cancellationToken);

    /// <summary>Module progress, with each lesson rendered for the lesson viewer.</summary>
    [HttpGet("enrollments/{id:long}/modules"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<EnrollmentModuleDto>> Modules(long id, CancellationToken cancellationToken) =>
        learning.GetModulesAsync(id, cancellationToken);

    /// <summary>UC-14. The learner's own action only.</summary>
    [HttpPost("enrollments/{id:long}/modules/{moduleId:long}/complete"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<EnrollmentDto> CompleteModule(long id, long moduleId, CancellationToken cancellationToken) =>
        learning.CompleteModuleAsync(id, moduleId, cancellationToken);

    /// <summary><c>{ eligible, missingModules[], attendancePct, retakesLeft }</c> (BR-32).</summary>
    [HttpGet("enrollments/{id:long}/eligibility"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<EligibilityDto> Eligibility(long id, CancellationToken cancellationToken) =>
        learning.EligibilityAsync(id, cancellationToken);

    /// <summary>
    /// The quiz as the learner takes it: questions and options, never the
    /// answers. Not in the contract table; the quiz screen needs it. Behind
    /// the same eligibility gate as the attempt (BR-32).
    /// </summary>
    [HttpGet("enrollments/{id:long}/quiz"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<LearnerQuizDto> Quiz(long id, [FromServices] AssessmentService assessment,
        CancellationToken cancellationToken) =>
        assessment.GetQuizAsync(id, cancellationToken);

    /// <summary>UC-15. <c>{ answers: [ { questionId, selectedOption } ] }</c>. 409 BR-32 if not eligible, 409 BR-33 if no retakes are left.</summary>
    [HttpPost("enrollments/{id:long}/quiz-attempts"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<QuizAttemptResultDto> AttemptQuiz(long id, QuizAttemptRequest request,
        [FromServices] AssessmentService assessment, CancellationToken cancellationToken) =>
        assessment.AttemptQuizAsync(id, request, cancellationToken);

    /// <summary>Every attempt, each with its per-module breakdown.</summary>
    [HttpGet("enrollments/{id:long}/quiz-attempts"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<QuizAttemptResultDto>> QuizAttempts(long id, [FromServices] AssessmentService assessment,
        CancellationToken cancellationToken) =>
        assessment.ListAttemptsAsync(id, cancellationToken);

    /// <summary>
    /// The recording of the practical: <c>multipart/form-data</c> with one
    /// .mp4, .mov or .webm of at most 200 MB in the field <c>file</c>. Not in
    /// the contract table. Uploaded by the trainer who runs the practical;
    /// 403 BR-14 for the trainee.
    /// </summary>
    [HttpPost("enrollments/{id:long}/practical-videos"), Authorize(Policy = Policies.Trainer)]
    [RequestSizeLimit(PracticalVideo.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = PracticalVideo.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<PracticalVideoDto>> UploadPracticalVideo(long id,
        [FromServices] AssessmentService assessment, CancellationToken cancellationToken)
    {
        // Read here rather than bound, for the reason given at the POS import.
        var file = Request.HasFormContentType
            ? (await Request.ReadFormAsync(cancellationToken)).Files.GetFile("file")
            : null;
        if (file is null)
        {
            throw DomainException.Validation("A video is required, sent as multipart/form-data in the field 'file'.",
                new ErrorDetail("file", "is required"));
        }
        await using var content = file.OpenReadStream();
        var video = await assessment.UploadPracticalVideoAsync(id, file.FileName, file.Length, content, cancellationToken);
        return Created($"/api/v1/practical-videos/{video.Id}", video);
    }

    /// <summary>The recordings of an enrolment, each with the evaluation it was used for. Not in the contract table.</summary>
    [HttpGet("enrollments/{id:long}/practical-videos"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<PracticalVideoDto>> PracticalVideos(long id, [FromServices] AssessmentService assessment,
        CancellationToken cancellationToken) =>
        assessment.ListPracticalVideosAsync(id, cancellationToken);

    /// <summary>The recording itself, with range requests so that a player can seek. Not in the contract table.</summary>
    [HttpGet("practical-videos/{id:long}")]
    [AuthorizeRoles(RoleName.Trainee, RoleName.Trainer, RoleName.TrainingManager, RoleName.QualityAuditor)]
    public async Task<IActionResult> PracticalVideoContent(long id, [FromServices] AssessmentService assessment,
        CancellationToken cancellationToken)
    {
        var video = await assessment.OpenPracticalVideoAsync(id, cancellationToken);
        return File(video.Content, video.ContentType, video.FileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// UC-16. <c>{ practicalVideoId, items: [ { recipeStepId, passed, note } ] }</c>. 403 BR-14 if the evaluator
    /// is the trainee; 409 without the recording of the practical.
    /// </summary>
    [HttpPost("enrollments/{id:long}/practical-evaluation"), Authorize(Policy = Policies.Trainer)]
    public Task<PracticalEvaluationDto> EvaluatePractical(long id, PracticalEvaluationRequest request,
        [FromServices] AssessmentService assessment, CancellationToken cancellationToken) =>
        assessment.EvaluatePracticalAsync(id, request, cancellationToken);

    /// <summary>Withdraws an enrolment. Not in the contract table; the state model gives it to the Training Manager.</summary>
    [HttpPost("enrollments/{id:long}/close"), Authorize(Policy = Policies.TrainingManager)]
    public Task<EnrollmentDto> Close(long id, CancellationToken cancellationToken) =>
        learning.CloseAsync(id, cancellationToken);

    /// <summary>LOCKED to ASSIGNED. Not in the contract table; the state model gives it to the Training Manager.</summary>
    [HttpPost("enrollments/{id:long}/reset"), Authorize(Policy = Policies.TrainingManager)]
    public Task<EnrollmentDto> Reset(long id, CancellationToken cancellationToken) =>
        learning.ResetAsync(id, cancellationToken);
}
