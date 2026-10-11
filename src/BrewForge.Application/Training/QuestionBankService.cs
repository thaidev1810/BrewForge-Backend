using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Courses;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Training;

public sealed record QuestionBankDto(long Id, QuestionType QuestionType, string QuestionText,
    IReadOnlyList<QuizOption> Options, IReadOnlyList<string> CorrectOptions, long? CourseId, ModuleType? ModuleType,
    long CreatedBy, DateTimeOffset CreatedAt);

public sealed record QuestionBankRequest(QuestionType? QuestionType, string? QuestionText,
    IReadOnlyList<QuizOptionRequest>? Options, IReadOnlyList<string>? CorrectOptions, long? CourseId,
    ModuleType? ModuleType);

/// <summary>
/// The trainer's question bank: single choice, multiple choice and true/false
/// questions kept for reuse. Adding one to a course copies it into the quiz.
/// </summary>
public sealed class QuestionBankService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock,
    CourseService courses)
{
    private static readonly SortMap<QuestionBankItem> Sorting = new SortMap<QuestionBankItem>("createdAt,desc", q => q.Id)
        .Add("id", q => q.Id)
        .Add("createdAt", q => q.CreatedAt)
        .Add("questionType", q => q.QuestionType);

    public async Task<PagedResult<QuestionBankDto>> ListAsync(PageQuery paging, string? keyword, long? courseId,
        long? recipeVersionId, string? type, CancellationToken cancellationToken)
    {
        var typeFilter = PagingExtensions.ParseFilter<QuestionType>(type, "questionType");

        var query = db.QuestionBank.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(q => q.QuestionText.ToLower().Contains(term));
        }
        if (courseId is not null) query = query.Where(q => q.CourseId == courseId);
        if (recipeVersionId is not null)
        {
            // "Filter by SOP": the questions of the courses bound to that version.
            query = query.Where(q => db.Courses.Any(c => c.Id == q.CourseId && c.RecipeVersionId == recipeVersionId));
        }
        if (typeFilter is not null) query = query.Where(q => q.QuestionType == typeFilter);

        return await query.ToPagedAsync(paging, Sorting, ToDto, cancellationToken);
    }

    public async Task<QuestionBankDto> GetAsync(long id, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(id, cancellationToken));

    public async Task<QuestionBankDto> CreateAsync(QuestionBankRequest request, CancellationToken cancellationToken)
    {
        await EnsureCourseAsync(request.CourseId, cancellationToken);
        var item = QuestionBankItem.Create(request.QuestionType, request.QuestionText, ToOptions(request),
            request.CorrectOptions, request.CourseId, request.ModuleType, currentUser.RequireUserId(), clock.GetUtcNow());

        db.QuestionBank.Add(item);
        db.Audit(AuditEntities.QuestionBank, () => item.Id, AuditActions.Create, new { item.QuestionType, item.CourseId });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<QuestionBankDto> UpdateAsync(long id, QuestionBankRequest request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        await EnsureCourseAsync(request.CourseId, cancellationToken);
        item.Set(request.QuestionType, request.QuestionText, ToOptions(request), request.CorrectOptions,
            request.CourseId, request.ModuleType);

        db.Audit(AuditEntities.QuestionBank, () => item.Id, AuditActions.Update);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    /// <summary>A bank question is not part of any quiz, so it can be removed. Copies already in quizzes stay.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        db.QuestionBank.Remove(item);
        db.Audit(AuditEntities.QuestionBank, () => item.Id, AuditActions.Delete);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Copies the question into the quiz of a course, on the module it tests. The quiz grades one right
    /// answer, so a MULTIPLE_CHOICE question cannot be copied (409).
    /// </summary>
    public async Task<QuizQuestionDto> AddToCourseAsync(long id, long courseId, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        if (item.QuestionType == QuestionType.MultipleChoice)
        {
            throw DomainException.RuleViolation("QUESTION_NOT_COPYABLE",
                "A quiz grades a single right answer; a multiple-choice question cannot be added to it.");
        }
        var moduleType = item.ModuleType ?? throw DomainException.RuleViolation(Quiz.TaggingRule,
            "The question names no module, so it cannot be copied into a quiz.");
        var moduleId = await db.Courses.Where(c => c.Id == courseId)
            .SelectMany(c => c.Modules).Where(m => m.ModuleType == moduleType).Select(m => (long?)m.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw DomainException.NotFound("Course module", $"{moduleType} of course {courseId}");

        return await courses.AddQuestionAsync(courseId, new QuizQuestionRequest(moduleId, item.QuestionText,
            [.. item.Options().Select(o => new QuizOptionRequest(o.Key, o.Text))], item.CorrectKeys()[0]),
            cancellationToken);
    }

    private async Task EnsureCourseAsync(long? courseId, CancellationToken cancellationToken)
    {
        if (courseId is { } id && !await db.Courses.AnyAsync(c => c.Id == id, cancellationToken))
        {
            throw DomainException.Validation("The course does not exist.", new ErrorDetail("courseId", "does not exist"));
        }
    }

    private static List<QuizOption>? ToOptions(QuestionBankRequest request) =>
        request.Options?.Select(o => new QuizOption(o.Key ?? "", o.Text ?? "")).ToList();

    private async Task<QuestionBankItem> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.QuestionBank.SingleOrDefaultAsync(q => q.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Question", id);

    private static QuestionBankDto ToDto(QuestionBankItem q) =>
        new(q.Id, q.QuestionType, q.QuestionText, q.Options(), q.CorrectKeys(), q.CourseId, q.ModuleType, q.CreatedBy,
            q.CreatedAt);
}
