using BrewForge.Api.Auth;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrewForge.Api.Controllers;

/// <summary>
/// UC-11 to UC-13 and UC-28 - API contract section 5. There is no DELETE: a
/// course is flagged out of date or archived, never deleted (BR-15).
/// </summary>
[ApiController]
[Route("api/v1/courses")]
public sealed class CoursesController(CourseService courses) : ControllerBase
{
    [HttpGet, AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager, RoleName.RdManager)]
    public Task<PagedResult<CourseDto>> List([FromQuery] PageQuery paging, [FromQuery] string? q,
        [FromQuery] string? state, [FromQuery] string? courseType, [FromQuery] long? recipeVersionId,
        CancellationToken cancellationToken) =>
        courses.ListAsync(paging, q, state, courseType, recipeVersionId, cancellationToken);

    /// <summary>UC-11. <c>{ recipeVersionId, courseType, title }</c>. Generates modules 1 to 4.</summary>
    [HttpPost, Authorize(Policy = Policies.Trainer)]
    public async Task<ActionResult<CourseDetailDto>> Create(CreateCourseRequest request,
        CancellationToken cancellationToken)
    {
        var created = await courses.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Includes the modules, the quiz and the practical checklist.</summary>
    [HttpGet("{id:long}"), AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public Task<CourseDetailDto> Get(long id, CancellationToken cancellationToken) =>
        courses.GetAsync(id, cancellationToken);

    /// <summary>The seven modules in order, each lesson rendered from the bound version (BR-22).</summary>
    [HttpGet("{id:long}/modules"), AuthorizeRoles(RoleName.Trainer, RoleName.TrainingManager)]
    public Task<IReadOnlyList<CourseModuleDto>> Modules(long id, CancellationToken cancellationToken) =>
        courses.GetModulesAsync(id, cancellationToken);

    [HttpGet("{id:long}/quiz"), Authorize(Policy = Policies.Trainer)]
    public Task<QuizDto> GetQuiz(long id, CancellationToken cancellationToken) =>
        courses.GetQuizAsync(id, cancellationToken);

    /// <summary>UC-13. <c>{ passScore }</c>.</summary>
    [HttpPut("{id:long}/quiz"), Authorize(Policy = Policies.Trainer)]
    public Task<QuizDto> UpdateQuiz(long id, QuizRequest request, CancellationToken cancellationToken) =>
        courses.UpdateQuizAsync(id, request, cancellationToken);

    [HttpGet("{id:long}/quiz/questions"), Authorize(Policy = Policies.Trainer)]
    public Task<IReadOnlyList<QuizQuestionDto>> ListQuestions(long id, CancellationToken cancellationToken) =>
        courses.ListQuestionsAsync(id, cancellationToken);

    /// <summary>Every question carries <c>courseModuleId</c> (BR-35).</summary>
    [HttpPost("{id:long}/quiz/questions"), Authorize(Policy = Policies.Trainer)]
    public async Task<ActionResult<QuizQuestionDto>> AddQuestion(long id, QuizQuestionRequest request,
        CancellationToken cancellationToken)
    {
        var created = await courses.AddQuestionAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(ListQuestions), new { id }, created);
    }

    /// <summary><c>{ items: [ { recipeStepId } ] }</c>. Items are steps of the bound version, never free text.</summary>
    [HttpPut("{id:long}/practical-checklist"), Authorize(Policy = Policies.Trainer)]
    public Task<IReadOnlyList<ChecklistItemDto>> SetPracticalChecklist(long id, ChecklistRequest request,
        CancellationToken cancellationToken) =>
        courses.SetPracticalChecklistAsync(id, request, cancellationToken);

    /// <summary>DRAFT to PENDING_APPROVAL. 409 BR-31 if any module is empty.</summary>
    [HttpPost("{id:long}/submit"), Authorize(Policy = Policies.Trainer)]
    public Task<CourseDetailDto> Submit(long id, CancellationToken cancellationToken) =>
        courses.SubmitAsync(id, cancellationToken);

    /// <summary>UC-28. PENDING_APPROVAL to PUBLISHED.</summary>
    [HttpPost("{id:long}/approve"), Authorize(Policy = Policies.TrainingManager)]
    public Task<CourseDetailDto> Approve(long id, CancellationToken cancellationToken) =>
        courses.ApproveAsync(id, cancellationToken);

    /// <summary>UC-28. <c>{ comment }</c>. Back to DRAFT.</summary>
    [HttpPost("{id:long}/return"), Authorize(Policy = Policies.TrainingManager)]
    public Task<CourseDetailDto> Return(long id, ReturnCourseRequest request, CancellationToken cancellationToken) =>
        courses.ReturnAsync(id, request, cancellationToken);

    /// <summary>
    /// OUT_OF_DATE to DRAFT on the version now released for the recipe:
    /// generated modules are regenerated, authored modules are kept (BR-30).
    /// Not in the contract table; the state model gives it to the Trainer.
    /// </summary>
    [HttpPost("{id:long}/rebuild"), Authorize(Policy = Policies.Trainer)]
    public Task<CourseDetailDto> Rebuild(long id, CancellationToken cancellationToken) =>
        courses.RebuildAsync(id, cancellationToken);
}

[ApiController]
[Route("api/v1/course-modules")]
[Authorize(Policy = Policies.Trainer)]
public sealed class CourseModulesController(CourseService courses) : ControllerBase
{
    /// <summary><c>{ durationMinutes }</c>. 409 BR-30 on a GENERATED module.</summary>
    [HttpPut("{id:long}")]
    public Task<CourseModuleDto> Update(long id, UpdateModuleRequest request, CancellationToken cancellationToken) =>
        courses.UpdateModuleAsync(id, request, cancellationToken);

    /// <summary>Only for GENERATED modules. Never overwrites what the trainer authored (BR-30).</summary>
    [HttpPost("{id:long}/regenerate")]
    public Task<CourseModuleDto> Regenerate(long id, CancellationToken cancellationToken) =>
        courses.RegenerateModuleAsync(id, cancellationToken);

    [HttpGet("{id:long}/lessons")]
    public Task<IReadOnlyList<LessonDto>> ListLessons(long id, CancellationToken cancellationToken) =>
        courses.ListLessonsAsync(id, cancellationToken);

    /// <summary>UC-12. <c>{ title, content, mediaUrl }</c>.</summary>
    [HttpPost("{id:long}/lessons")]
    public async Task<ActionResult<LessonDto>> AddLesson(long id, LessonRequest request,
        CancellationToken cancellationToken)
    {
        var created = await courses.AddLessonAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(ListLessons), new { id }, created);
    }
}

[ApiController]
[Route("api/v1/lessons")]
[Authorize(Policy = Policies.Trainer)]
public sealed class LessonsController(CourseService courses) : ControllerBase
{
    [HttpPut("{id:long}")]
    public Task<LessonDto> Update(long id, LessonRequest request, CancellationToken cancellationToken) =>
        courses.UpdateLessonAsync(id, request, cancellationToken);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await courses.DeleteLessonAsync(id, cancellationToken);
        return NoContent();
    }
}
