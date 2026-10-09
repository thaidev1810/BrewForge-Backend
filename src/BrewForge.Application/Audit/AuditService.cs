using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Audit;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Audit;

public sealed record AuditEntryDto(long Id, long? UserId, string? Username, string EntityType, long EntityId,
    string Action, JsonElement? Payload, DateTimeOffset CreatedAt);

public sealed record PersonDto(long Id, string Username, string FullName, RoleName Role);

public sealed record TraceVersionDto(long Id, long RecipeId, string RecipeCode, string RecipeName, RecipeOrigin Origin,
    int VersionNo, VersionState State, string? ContentHash, PersonDto? Author, PersonDto? Approver,
    DateTimeOffset? ReleasedAt, DateTimeOffset? SupersededAt);

/// <summary>One check of the latest validation run of the version.</summary>
public sealed record TraceCheckDto(CheckType CheckType, bool Passed, int Violations, DateTimeOffset RunAt);

/// <summary>A call to the language model made for the recipe: what was asked, and whether the answer conformed.</summary>
public sealed record TraceAiDraftDto(long Id, string ModelName, bool SchemaValid, DateTimeOffset CreatedAt, string PromptText);

public sealed record TraceCourseDto(long Id, string Title, CourseType CourseType, CourseState State, PersonDto? CreatedBy,
    PersonDto? ApprovedBy, DateTimeOffset? PublishedAt);

public sealed record TraceCertificateDto(long Id, long UserId, string? Username, string? FullName, long? BranchId,
    string? BranchCode, long CourseId, CertificateStatus Status, DateTimeOffset IssuedAt);

public sealed record TraceBranchDto(long BranchId, string? BranchCode, LaunchStatus Status, DateTimeOffset? LiveSince,
    int CertifiedCount, bool CoverageMet);

/// <summary>
/// A recipe version followed from end to end: who wrote it, who approved it,
/// what the validator said, which courses teach it, who was ever certified on
/// it, where it is sold, and everything the log holds about it.
/// </summary>
public sealed record VersionTraceDto(string EntityType, long EntityId, TraceVersionDto Version,
    IReadOnlyList<TraceCheckDto> Validation, IReadOnlyList<TraceAiDraftDto> AiDrafts, IReadOnlyList<TraceCourseDto> Courses,
    IReadOnlyList<TraceCertificateDto> CertifiedStaff, IReadOnlyList<TraceBranchDto> Branches, int CupsSold,
    IReadOnlyList<AuditEntryDto> Events);

/// <summary>UC-20: the audit log, read-only. There is no operation here that changes an entry, and none anywhere else.</summary>
public sealed class AuditService(IBrewForgeDbContext db)
{
    private static readonly SortMap<AuditLog> Sorting = new SortMap<AuditLog>("createdAt,desc", a => a.Id)
        .Add("id", a => a.Id)
        .Add("createdAt", a => a.CreatedAt)
        .Add("entityType", a => a.EntityType)
        .Add("action", a => a.Action);

    /// <summary>
    /// <c>?entityType=&amp;entityId=&amp;action=&amp;userId=&amp;branchId=&amp;from=&amp;to=</c>.
    /// A branch narrows the log to what the staff of that branch did; the
    /// period is in whole days.
    /// </summary>
    public async Task<PagedResult<AuditEntryDto>> ListAsync(PageQuery paging, string? entityType, long? entityId,
        string? action, long? userId, long? branchId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        new FieldErrors().Check(from is null || to is null || from <= to, "from", "must not be after 'to'").ThrowIfAny();

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (entityId is not null) query = query.Where(a => a.EntityId == entityId);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (userId is not null) query = query.Where(a => a.UserId == userId);
        if (branchId is not null)
        {
            query = query.Where(a => db.Users.IgnoreQueryFilters().Any(u => u.Id == a.UserId && u.BranchId == branchId));
        }
        if (from is { } first)
        {
            var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.CreatedAt >= start);
        }
        if (to is { } last)
        {
            var end = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.CreatedAt < end);
        }

        var page = await query.ToPagedAsync(paging, Sorting, a => a, cancellationToken);
        return new PagedResult<AuditEntryDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size, page.Total);
    }

    /// <summary>
    /// The trace of a recipe version, or of the version behind a course, a
    /// certificate or a recipe (its released version, else its latest).
    /// </summary>
    public async Task<VersionTraceDto> TraceAsync(string entityType, long entityId, CancellationToken cancellationToken)
    {
        var versionId = entityType switch
        {
            AuditEntities.RecipeVersion => entityId,
            AuditEntities.Course => await db.Courses.Where(c => c.Id == entityId).Select(c => c.RecipeVersionId)
                .SingleOrDefaultAsync(cancellationToken),
            AuditEntities.Certificate => await db.Certificates.Where(c => c.Id == entityId).Select(c => c.RecipeVersionId)
                .SingleOrDefaultAsync(cancellationToken),
            AuditEntities.Recipe => await db.RecipeVersions.Where(v => v.RecipeId == entityId)
                .OrderByDescending(v => v.State == VersionState.Released).ThenByDescending(v => v.VersionNo)
                .Select(v => (long?)v.Id).FirstOrDefaultAsync(cancellationToken),
            _ => throw DomainException.Validation("That kind of entity cannot be traced.",
                new ErrorDetail("entityType", "must be one of: RecipeVersion, Recipe, Course, Certificate")),
        };
        var version = versionId is null
            ? null
            : await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (version is null) throw DomainException.NotFound(entityType, entityId);
        var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);

        var results = await db.ValidationResults.AsNoTracking().Where(r => r.RecipeVersionId == version.Id)
            .ToListAsync(cancellationToken);
        var latestRun = results.Count == 0 ? (DateTimeOffset?)null : results.Max(r => r.RunAt);
        var aiDrafts = await db.AiDraftLogs.AsNoTracking().Where(l => l.RecipeId == recipe.Id).OrderBy(l => l.Id)
            .ToListAsync(cancellationToken);
        var courses = await db.Courses.AsNoTracking().Where(c => c.RecipeVersionId == version.Id).OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var certificates = await db.Certificates.AsNoTracking().Where(c => c.RecipeVersionId == version.Id)
            .OrderBy(c => c.IssuedAt).ToListAsync(cancellationToken);
        var launches = await db.BranchLaunchStatuses.AsNoTracking().Where(l => l.RecipeVersionId == version.Id)
            .ToListAsync(cancellationToken);
        var cups = await db.SalesRecords.Where(s => s.RecipeVersionId == version.Id).SumAsync(s => (int?)s.CupsSold, cancellationToken) ?? 0;
        var events = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.RecipeVersion && a.EntityId == version.Id).OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        var peopleIds = new long?[] { version.CreatedBy, version.ApprovedBy }
            .Concat(courses.Select(c => (long?)c.CreatedBy)).Concat(courses.Select(c => c.ApprovedBy))
            .Concat(certificates.Select(c => (long?)c.UserId)).OfType<long>().Distinct().ToList();
        var people = await db.Users.AsNoTracking().IgnoreQueryFilters().Include(u => u.Role).Where(u => peopleIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var branches = await db.Branches.AsNoTracking().IgnoreQueryFilters()
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        PersonDto? Person(long? id) => id is { } key && people.TryGetValue(key, out var user)
            ? new PersonDto(user.Id, user.Username, user.FullName, user.Role.RoleName)
            : null;

        return new VersionTraceDto(entityType, entityId,
            new TraceVersionDto(version.Id, recipe.Id, recipe.RecipeCode, recipe.Name, recipe.Origin, version.VersionNo,
                version.State, version.ContentHash, Person(version.CreatedBy), Person(version.ApprovedBy), version.ReleasedAt,
                version.SupersededAt),
            [
                .. results.Where(r => r.RunAt == latestRun).GroupBy(r => r.CheckType).OrderBy(g => g.Key)
                    .Select(g => new TraceCheckDto(g.Key, g.All(r => r.Passed), g.Count(r => !r.Passed), g.Max(r => r.RunAt))),
            ],
            [.. aiDrafts.Select(l => new TraceAiDraftDto(l.Id, l.ModelName, l.SchemaValid, l.CreatedAt, l.PromptText))],
            [
                .. courses.Select(c => new TraceCourseDto(c.Id, c.Title, c.CourseType, c.State, Person(c.CreatedBy),
                    Person(c.ApprovedBy), c.PublishedAt)),
            ],
            [
                .. certificates.Select(c =>
                {
                    var holder = people.GetValueOrDefault(c.UserId);
                    return new TraceCertificateDto(c.Id, c.UserId, holder?.Username, holder?.FullName, holder?.BranchId,
                        holder?.BranchId is { } branchId ? branches.GetValueOrDefault(branchId) : null, c.CourseId, c.Status,
                        c.IssuedAt);
                }),
            ],
            [
                .. launches.OrderBy(l => l.BranchId).Select(l => new TraceBranchDto(l.BranchId, branches.GetValueOrDefault(l.BranchId),
                    l.Status, l.LiveSince, l.CertifiedCount, l.CoverageMet)),
            ],
            cups, await ToDtosAsync(events, cancellationToken));
    }

    private async Task<IReadOnlyList<AuditEntryDto>> ToDtosAsync(IReadOnlyList<AuditLog> entries,
        CancellationToken cancellationToken)
    {
        var userIds = entries.Select(e => e.UserId).OfType<long>().Distinct().ToList();
        var usernames = await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);

        return
        [
            .. entries.Select(e => new AuditEntryDto(e.Id, e.UserId,
                e.UserId is { } id ? usernames.GetValueOrDefault(id) : null, e.EntityType, e.EntityId, e.Action,
                e.PayloadJson is null ? null : JsonSerializer.Deserialize<JsonElement>(e.PayloadJson), e.CreatedAt)),
        ];
    }
}
