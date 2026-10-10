using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Recipes;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Courses;

/// <summary>UC-11 to UC-13 and UC-28: course authoring and approval (SCR-13 to SCR-16, SCR-30).</summary>
public sealed class CourseService(IBrewForgeDbContext db, CourseRenderer renderer, ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly SortMap<Course> Sorting = new SortMap<Course>("title", c => c.Id)
        .Add("id", c => c.Id)
        .Add("title", c => c.Title)
        .Add("courseType", c => c.CourseType)
        .Add("state", c => c.State)
        .Add("publishedAt", c => c.PublishedAt);

    // ---------------------------------------------------------------- courses

    public async Task<PagedResult<CourseDto>> ListAsync(PageQuery paging, string? keyword, string? state,
        string? courseType, long? recipeVersionId, CancellationToken cancellationToken)
    {
        var stateFilter = PagingExtensions.ParseFilter<CourseState>(state, "state");
        var typeFilter = PagingExtensions.ParseFilter<CourseType>(courseType, "courseType");

        var query = db.Courses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(c => c.Title.ToLower().Contains(term));
        }
        if (stateFilter is not null) query = query.Where(c => c.State == stateFilter);
        if (typeFilter is not null) query = query.Where(c => c.CourseType == typeFilter);
        if (recipeVersionId is not null) query = query.Where(c => c.RecipeVersionId == recipeVersionId);

        var page = await query.ToPagedAsync(paging, Sorting, course => course, cancellationToken);
        var versionIds = page.Items.Select(c => c.RecipeVersionId).OfType<long>().Distinct().ToList();
        var versions = await db.RecipeVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, v.VersionNo, r.RecipeCode, r.Name, RecipeId = r.Id })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return new PagedResult<CourseDto>(
        [
            .. page.Items.Select(course =>
            {
                var bound = course.RecipeVersionId is { } id ? versions.GetValueOrDefault(id) : null;
                return new CourseDto(course.Id, course.RecipeVersionId, course.CourseType, course.Title,
                    course.TotalDurationMin, course.CreatedBy, course.ApprovedBy, course.State, course.PublishedAt,
                    bound?.RecipeId, bound?.RecipeCode, bound?.Name, bound?.VersionNo);
            }),
        ], page.Page, page.Size, page.Total);
    }

    public async Task<CourseDetailDto> GetAsync(long id, CancellationToken cancellationToken) =>
        await renderer.RenderAsync(await FindAsync(id, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<CourseModuleDto>> GetModulesAsync(long id, CancellationToken cancellationToken)
    {
        var course = await FindAsync(id, cancellationToken);
        return CourseRenderer.RenderModules(course,
            await renderer.LoadSourceAsync(course.RecipeVersionId, cancellationToken));
    }

    /// <summary>UC-11. Creates the course in DRAFT and generates its seven modules (BR-29).</summary>
    public async Task<CourseDetailDto> CreateAsync(CreateCourseRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.CourseType is not null, "courseType", "is required").ThrowIfAny();

        var version = request.RecipeVersionId is { } versionId
            ? await db.RecipeVersions.WithContent().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
              ?? throw DomainException.Validation("The recipe version does not exist.",
                  new ErrorDetail("recipeVersionId", $"recipe version {versionId} does not exist"))
            : null;

        // BR-20 before the duplicate check: a draft version is refused for what it is.
        Course.EnsureUsableAsSource(version);
        // One course of a kind per version: the PRODUCT course, and the RECERTIFICATION course beside it.
        if (request.CourseType is CourseType.Product or CourseType.Recertification && version is not null)
        {
            var existing = await db.Courses.AsNoTracking()
                .Where(c => c.RecipeVersionId == version.Id && c.CourseType == request.CourseType
                            && c.State != CourseState.Archived)
                .Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                throw DomainException.RuleViolation("BR-18",
                    $"A {request.CourseType.Value.Code()} course already exists for version {version.VersionNo}. Open the existing course instead of creating a duplicate.",
                    "MSG-W04", new ErrorDetail("existingCourseId", existing.Value.ToString()));
            }
        }

        var previous = request.CourseType == CourseType.Recertification && version is not null
            ? await db.FindPreviousVersionAsync(version, cancellationToken)
            : null;
        var course = Course.Create(request.CourseType!.Value, request.Title, version, currentUser.RequireUserId(), previous);

        db.Courses.Add(course);
        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.Create, new
        {
            course.Title, courseType = course.CourseType.Code(), course.RecipeVersionId,
            modules = course.OrderedModules().Select(m => new { type = m.ModuleType.Code(), source = m.Source.Code() }),
        });
        await db.SaveChangesAsync(cancellationToken);
        return await renderer.RenderAsync(course, cancellationToken);
    }

    // ---------------------------------------------------------------- modules and lessons

    /// <summary>Sets the duration of a module. 409 BR-30 on a GENERATED module.</summary>
    public async Task<CourseModuleDto> UpdateModuleAsync(long moduleId, UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        var (course, module) = await FindModuleAsync(moduleId, cancellationToken);

        course.SetModuleDuration(module, request.DurationMinutes);

        db.Audit(AuditEntities.CourseModule, () => module.Id, AuditActions.Update,
            new { type = module.ModuleType.Code(), module.DurationMinutes });
        await db.SaveChangesAsync(cancellationToken);
        return await RenderModuleAsync(course, module, cancellationToken);
    }

    /// <summary>Rebuilds a GENERATED module from the bound version. Refuses an AUTHORED one (BR-30).</summary>
    public async Task<CourseModuleDto> RegenerateModuleAsync(long moduleId, CancellationToken cancellationToken)
    {
        var (course, module) = await FindModuleAsync(moduleId, cancellationToken);
        var version = await BoundVersionAsync(course, cancellationToken);
        var previous = course.CourseType == CourseType.Recertification
            ? await db.FindPreviousVersionAsync(version, cancellationToken)
            : null;

        course.RegenerateModule(module, version, previous);


        db.Audit(AuditEntities.CourseModule, () => module.Id, AuditActions.Regenerate,
            new { type = module.ModuleType.Code(), course.RecipeVersionId });
        await db.SaveChangesAsync(cancellationToken);
        return await RenderModuleAsync(course, module, cancellationToken);
    }

    public async Task<IReadOnlyList<LessonDto>> ListLessonsAsync(long moduleId, CancellationToken cancellationToken)
    {
        var (course, module) = await FindModuleAsync(moduleId, cancellationToken);
        return (await RenderModuleAsync(course, module, cancellationToken)).Lessons;
    }

    /// <summary>UC-12. Adds an authored lesson. 409 BR-30 on a GENERATED module.</summary>
    public async Task<LessonDto> AddLessonAsync(long moduleId, LessonRequest request,
        CancellationToken cancellationToken)
    {
        var (course, module) = await FindModuleAsync(moduleId, cancellationToken);

        var lesson = course.AddLesson(module, request.Title, request.Content, request.MediaUrl);

        db.Audit(AuditEntities.Lesson, () => lesson.Id, AuditActions.Create, new { moduleId, lesson.Title });
        await db.SaveChangesAsync(cancellationToken);
        return CourseRenderer.RenderLesson(module, lesson, null);
    }

    public async Task<LessonDto> UpdateLessonAsync(long lessonId, LessonRequest request,
        CancellationToken cancellationToken)
    {
        var (course, module, lesson) = await FindLessonAsync(lessonId, cancellationToken);

        course.UpdateLesson(module, lesson, request.Title, request.Content, request.MediaUrl);

        db.Audit(AuditEntities.Lesson, () => lesson.Id, AuditActions.Update, new { module.Id, lesson.Title });
        await db.SaveChangesAsync(cancellationToken);
        return (await RenderModuleAsync(course, module, cancellationToken)).Lessons.Single(l => l.Id == lessonId);
    }

    public async Task DeleteLessonAsync(long lessonId, CancellationToken cancellationToken)
    {
        var (course, module, lesson) = await FindLessonAsync(lessonId, cancellationToken);

        course.RemoveLesson(module, lesson);

        db.Audit(AuditEntities.Lesson, () => lessonId, AuditActions.Delete, new { module.Id, lesson.Title });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- quiz and checklist

    public async Task<QuizDto> GetQuizAsync(long courseId, CancellationToken cancellationToken) =>
        CourseRenderer.ToDto((await FindAsync(courseId, cancellationToken)).Quiz);

    public async Task<QuizDto> UpdateQuizAsync(long courseId, QuizRequest request, CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);

        course.UpdateQuiz(request.Title, request.PassScore);

        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.UpdateQuiz,
            new { course.Quiz.Title, course.Quiz.PassScore });
        await db.SaveChangesAsync(cancellationToken);
        return CourseRenderer.ToDto(course.Quiz);
    }

    public async Task<IReadOnlyList<QuizQuestionDto>> ListQuestionsAsync(long courseId,
        CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);
        return [.. course.Quiz.Questions.OrderBy(q => q.Id).Select(question => ToDto(course, question))];
    }

    /// <summary>UC-13. Every question carries the module it tests (BR-35).</summary>
    public async Task<QuizQuestionDto> AddQuestionAsync(long courseId, QuizQuestionRequest request,
        CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);

        var question = course.AddQuizQuestion(request.CourseModuleId, request.QuestionText,
            request.Options is null ? null : [.. request.Options.Select(o => new QuizOption(o.Key ?? "", o.Text ?? ""))],
            request.CorrectOption);

        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.AddQuestion,
            new { question.CourseModuleId, question.QuestionText });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(course, question);
    }

    public async Task<IReadOnlyList<ChecklistItemDto>> SetPracticalChecklistAsync(long courseId,
        ChecklistRequest request, CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);
        var items = request.Items ?? throw DomainException.Validation("The checklist has no items.",
            new ErrorDetail("items", "is required"));
        new FieldErrors()
            .Check(items.All(item => item.RecipeStepId is not null), "items", "every item must name a recipeStepId")
            .ThrowIfAny();
        var version = await BoundVersionAsync(course, cancellationToken);

        course.SetPracticalChecklist([.. items.Select(item => item.RecipeStepId!.Value)], version);

        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.UpdateChecklist,
            new { steps = items.Select(item => item.RecipeStepId) });
        await db.SaveChangesAsync(cancellationToken);
        return CourseRenderer.RenderChecklist(course,
            await renderer.LoadSourceAsync(course.RecipeVersionId, cancellationToken));
    }

    // ---------------------------------------------------------------- approval

    /// <summary>DRAFT to PENDING_APPROVAL. 409 BR-31 while any module is empty.</summary>
    public async Task<CourseDetailDto> SubmitAsync(long courseId, CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);
        course.Submit();
        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.Submit);
        await db.SaveChangesAsync(cancellationToken);
        return await renderer.RenderAsync(course, cancellationToken);
    }

    /// <summary>UC-28. PENDING_APPROVAL to PUBLISHED.</summary>
    public async Task<CourseDetailDto> ApproveAsync(long courseId, CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);
        course.Approve(currentUser.RequireUserId(), clock.GetUtcNow());
        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.Approve, new { course.Title, course.RecipeVersionId });
        await db.SaveChangesAsync(cancellationToken);
        return await renderer.RenderAsync(course, cancellationToken);
    }

    /// <summary>UC-28. Back to DRAFT. The comment is kept in the audit log, the schema having no column for it.</summary>
    public async Task<CourseDetailDto> ReturnAsync(long courseId, ReturnCourseRequest request,
        CancellationToken cancellationToken)
    {
        var comment = request.Comment?.Trim();
        new FieldErrors().RequiredMax("comment", comment, 1000).ThrowIfAny();

        var course = await FindAsync(courseId, cancellationToken);
        course.Return();
        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.Return, new { comment });
        await db.SaveChangesAsync(cancellationToken);
        return await renderer.RenderAsync(course, cancellationToken);
    }

    /// <summary>
    /// OUT_OF_DATE to DRAFT on the version now released for the recipe:
    /// generated modules are rebuilt, authored modules are kept (BR-30).
    /// </summary>
    public async Task<CourseDetailDto> RebuildAsync(long courseId, CancellationToken cancellationToken)
    {
        var course = await FindAsync(courseId, cancellationToken);
        var previous = await BoundVersionAsync(course, cancellationToken);
        var current = await db.RecipeVersions.WithContent()
            .SingleOrDefaultAsync(v => v.RecipeId == previous.RecipeId && v.State == VersionState.Released, cancellationToken)
            ?? throw DomainException.RuleViolation("BR-20", "The recipe has no released version to rebuild the course on.");

        course.RebuildOn(current, previous);

        db.Audit(AuditEntities.Course, () => course.Id, AuditActions.Rebuild,
            new { fromVersionId = previous.Id, toVersionId = current.Id });
        await db.SaveChangesAsync(cancellationToken);
        return await renderer.RenderAsync(course, cancellationToken);
    }

    // ---------------------------------------------------------------- loading

    internal static IQueryable<Course> WithContent(IQueryable<Course> courses) =>
        courses
            .Include(course => course.Modules).ThenInclude(module => module.Lessons)
            .Include(course => course.Quiz).ThenInclude(quiz => quiz.Questions);

    internal async Task<Course> FindAsync(long id, CancellationToken cancellationToken) =>
        await WithContent(db.Courses).SingleOrDefaultAsync(course => course.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Course", id);

    private async Task<(Course Course, CourseModule Module)> FindModuleAsync(long moduleId,
        CancellationToken cancellationToken)
    {
        var course = await WithContent(db.Courses)
            .SingleOrDefaultAsync(c => c.Modules.Any(m => m.Id == moduleId), cancellationToken)
            ?? throw DomainException.NotFound("Course module", moduleId);
        return (course, course.Module(moduleId));
    }

    private async Task<(Course Course, CourseModule Module, Lesson Lesson)> FindLessonAsync(long lessonId,
        CancellationToken cancellationToken)
    {
        var course = await WithContent(db.Courses)
            .SingleOrDefaultAsync(c => c.Modules.Any(m => m.Lessons.Any(l => l.Id == lessonId)), cancellationToken)
            ?? throw DomainException.NotFound("Lesson", lessonId);
        var module = course.Modules.Single(m => m.Lessons.Any(l => l.Id == lessonId));
        return (course, module, module.Lessons.Single(l => l.Id == lessonId));
    }

    private async Task<RecipeVersion> BoundVersionAsync(Course course, CancellationToken cancellationToken) =>
        course.RecipeVersionId is { } versionId
            ? await db.FindVersionAsync(versionId, cancellationToken)
            : throw DomainException.RuleViolation("BR-18", "This course is not bound to a recipe version.");

    private async Task<CourseModuleDto> RenderModuleAsync(Course course, CourseModule module,
        CancellationToken cancellationToken) =>
        CourseRenderer.RenderModule(module, await renderer.LoadSourceAsync(course.RecipeVersionId, cancellationToken));

    private static QuizQuestionDto ToDto(Course course, QuizQuestion question) =>
        new(question.Id, question.CourseModuleId,
            course.Modules.Single(module => module.Id == question.CourseModuleId).ModuleType,
            question.QuestionText, question.Options(), question.CorrectOption);
}
