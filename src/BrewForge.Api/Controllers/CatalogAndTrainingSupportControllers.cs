using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Application.MasterData;
using BrewForge.Application.Training;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// The drinks as they are sold. Written by the R&amp;D specialist, read by every role. There is no DELETE:
/// a product is deactivated (BR-16).
/// </summary>
[ApiController]
[Route("api/v1/products")]
public sealed class ProductsController(ProductService products) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<PagedResult<ProductDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] long? categoryId, [FromQuery] string? status, CancellationToken cancellationToken) =>
        products.ListAsync(paging, q, categoryId, status, cancellationToken);

    [HttpGet("{id:long}"), Authorize]
    public Task<ProductDto> Get(long id, CancellationToken cancellationToken) => products.GetAsync(id, cancellationToken);

    [HttpPost, Authorize(Policy = Policies.RdSpecialist)]
    public async Task<ActionResult<ProductDto>> Create(ProductRequest request, CancellationToken cancellationToken)
    {
        var created = await products.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<ProductDto> Update(long id, ProductRequest request, CancellationToken cancellationToken) =>
        products.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:long}/deactivate"), Authorize(Policy = Policies.RdSpecialist)]
    public Task<ProductDto> Deactivate(long id, CancellationToken cancellationToken) =>
        products.DeactivateAsync(id, cancellationToken);
}

/// <summary>The trainer's question bank.</summary>
[ApiController]
[Route("api/v1/question-bank")]
[Authorize(Policy = Policies.Trainer)]
public sealed class QuestionBankController(QuestionBankService bank) : ControllerBase
{
    /// <summary><c>?q=&amp;courseId=&amp;recipeVersionId=&amp;questionType=</c></summary>
    [HttpGet]
    public Task<PagedResult<QuestionBankDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] long? courseId, [FromQuery] long? recipeVersionId, [FromQuery] string? questionType,
        CancellationToken cancellationToken) =>
        bank.ListAsync(paging, q, courseId, recipeVersionId, questionType, cancellationToken);

    [HttpGet("{id:long}")]
    public Task<QuestionBankDto> Get(long id, CancellationToken cancellationToken) => bank.GetAsync(id, cancellationToken);

    [HttpPost]
    public async Task<ActionResult<QuestionBankDto>> Create(QuestionBankRequest request, CancellationToken cancellationToken)
    {
        var created = await bank.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    public Task<QuestionBankDto> Update(long id, QuestionBankRequest request, CancellationToken cancellationToken) =>
        bank.UpdateAsync(id, request, cancellationToken);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await bank.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Copies the question into the quiz of the course, on the module it tests.</summary>
    [HttpPost("{id:long}/add-to-course/{courseId:long}")]
    public Task<QuizQuestionDto> AddToCourse(long id, long courseId, CancellationToken cancellationToken) =>
        bank.AddToCourseAsync(id, courseId, cancellationToken);
}

/// <summary>Giving a published course to the staff of a branch with a deadline and a reason.</summary>
[ApiController]
[Route("api/v1/assignments")]
public sealed class AssignmentsController(AssignmentService assignments) : ControllerBase
{
    /// <summary><c>?branchId=&amp;courseId=&amp;stage=ASSIGNED|IN_PROGRESS|THEORY_COMPLETED|QUIZ_PASSED|PRACTICAL_PENDING|COMPLETED|LOCKED|CLOSED&amp;overdue=true|false</c></summary>
    [HttpGet, AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager, RoleName.BranchManager)]
    public Task<PagedResult<AssignmentDto>> List([FromQuery] PageQuery paging, [FromQuery] long? branchId,
        [FromQuery] long? courseId, [FromQuery] string? stage, [FromQuery] bool? overdue,
        CancellationToken cancellationToken) =>
        assignments.ListAsync(paging, branchId, courseId, stage, overdue, cancellationToken);

    [HttpPost, AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public async Task<ActionResult<AssignmentResultDto>> Assign(AssignmentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await assignments.AssignAsync(request, cancellationToken));
}

/// <summary>The appointment of a practical assessment.</summary>
[ApiController]
[Route("api/v1")]
public sealed class PracticalSchedulesController(PracticalScheduleService schedules) : ControllerBase
{
    /// <summary>The trainer's agenda: <c>?status=SCHEDULED&amp;from=&amp;to=</c>.</summary>
    [HttpGet("practical-schedules"), Authorize(Policy = Policies.Trainer)]
    public Task<PagedResult<PracticalScheduleDto>> List([FromQuery] PageQuery paging, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken) =>
        schedules.ListAsync(paging, status, from, to, cancellationToken);

    [HttpGet("enrollments/{id:long}/practical-schedules"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<PracticalScheduleDto>> ForEnrollment(long id, CancellationToken cancellationToken) =>
        schedules.ForEnrollmentAsync(id, cancellationToken);

    /// <summary>Allowed once the quiz is passed (409 BR-39 before), one open appointment per enrolment.</summary>
    [HttpPost("enrollments/{id:long}/practical-schedules"), Authorize(Policy = Policies.Trainer)]
    public async Task<ActionResult<PracticalScheduleDto>> Create(long id, PracticalScheduleRequest request,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await schedules.CreateAsync(id, request, cancellationToken));

    [HttpPut("practical-schedules/{id:long}"), Authorize(Policy = Policies.Trainer)]
    public Task<PracticalScheduleDto> Reschedule(long id, PracticalScheduleRequest request,
        CancellationToken cancellationToken) =>
        schedules.RescheduleAsync(id, request, cancellationToken);

    [HttpPost("practical-schedules/{id:long}/cancel"), Authorize(Policy = Policies.Trainer)]
    public Task<PracticalScheduleDto> Cancel(long id, CancellationToken cancellationToken) =>
        schedules.CancelAsync(id, cancellationToken);

    [HttpPost("practical-schedules/{id:long}/complete"), Authorize(Policy = Policies.Trainer)]
    public Task<PracticalScheduleDto> Complete(long id, CancellationToken cancellationToken) =>
        schedules.CompleteAsync(id, cancellationToken);
}

/// <summary>The Skill Passport and the branch view of everyone's skills.</summary>
[ApiController]
[Route("api/v1")]
public sealed class SkillsController(SkillService skills) : ControllerBase
{
    [HttpGet("me/skills"), AuthorizeRoles(RoleName.Trainee, RoleName.Trainer)]
    public Task<IReadOnlyList<SkillDto>> Mine(CancellationToken cancellationToken) => skills.MySkillsAsync(cancellationToken);

    /// <summary>One row per employee: certified skills out of required, in training, overdue. A branch manager sees their own branch.</summary>
    [HttpGet("dashboards/employees"), AuthorizeRoles(RoleName.BranchManager, RoleName.Trainer, RoleName.TrainingManager, RoleName.Admin)]
    public Task<IReadOnlyList<EmployeeSkillSummaryDto>> Employees([FromQuery] long? branchId,
        CancellationToken cancellationToken) =>
        skills.EmployeesAsync(branchId, cancellationToken);
}

/// <summary>The few values an administrator maintains.</summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = Policies.Admin)]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SettingDto>> List(CancellationToken cancellationToken) => settings.ListAsync(cancellationToken);

    [HttpPut("{key}")]
    public Task<SettingDto> Update(string key, SettingRequest request, CancellationToken cancellationToken) =>
        settings.UpdateAsync(key, request, cancellationToken);
}
