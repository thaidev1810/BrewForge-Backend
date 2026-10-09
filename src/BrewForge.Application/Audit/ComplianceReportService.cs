using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Audit;

/// <summary>A table to be written to a file: a name, the column headings and the rows.</summary>
public sealed record TabularReport(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>Writes a table as a spreadsheet file. It knows file formats and nothing about compliance.</summary>
public interface IReportExporter
{
    byte[] ToCsv(TabularReport report);

    byte[] ToXlsx(TabularReport report);
}

public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// UC-20: the compliance report. One line per certificate ever issued: who
/// was certified on which version of which drink, through which course, when,
/// and what has become of that certificate since. Certificates are never
/// deleted (BR-15), so the report is the whole history.
/// </summary>
public sealed class ComplianceReportService(IBrewForgeDbContext db, IReportExporter exporter, ICurrentUser currentUser,
    TimeProvider clock)
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "Branch code", "Branch", "Username", "Staff name", "Course", "Recipe code", "Recipe", "Version no",
        "Version state", "Certificate id", "Certificate status", "Issued at (UTC)", "Superseded by certificate",
    ];

    /// <summary><c>?format=xlsx|csv</c>, narrowed by the day of issue, the branch, the course or the recipe version.</summary>
    public async Task<ReportFile> ExportAsync(string? format, DateOnly? from, DateOnly? to, long? branchId, long? courseId,
        long? recipeVersionId, CancellationToken cancellationToken)
    {
        format = string.IsNullOrWhiteSpace(format) ? "xlsx" : format.Trim().ToLowerInvariant();
        new FieldErrors()
            .Check(format is "xlsx" or "csv", "format", "must be xlsx or csv")
            .Check(from is null || to is null || from <= to, "from", "must not be after 'to'")
            .ThrowIfAny();

        var report = await BuildAsync(from, to, branchId, courseId, recipeVersionId, cancellationToken);
        var content = format == "csv" ? exporter.ToCsv(report) : exporter.ToXlsx(report);

        db.Audit(AuditEntities.User, () => currentUser.RequireUserId(), AuditActions.ExportCompliance,
            new { format, from, to, branchId, courseId, recipeVersionId, rows = report.Rows.Count });
        await db.SaveChangesAsync(cancellationToken);

        var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmm");
        return new ReportFile(content,
            format == "csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"brewforge-compliance-{stamp}.{format}");
    }

    public async Task<TabularReport> BuildAsync(DateOnly? from, DateOnly? to, long? branchId, long? courseId,
        long? recipeVersionId, CancellationToken cancellationToken)
    {
        var certificates = db.Certificates.AsNoTracking().AsQueryable();
        if (courseId is not null) certificates = certificates.Where(c => c.CourseId == courseId);
        if (recipeVersionId is not null) certificates = certificates.Where(c => c.RecipeVersionId == recipeVersionId);
        if (from is { } first)
        {
            var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            certificates = certificates.Where(c => c.IssuedAt >= start);
        }
        if (to is { } last)
        {
            var end = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            certificates = certificates.Where(c => c.IssuedAt < end);
        }

        var rows = await certificates
            .Join(db.Users.IgnoreQueryFilters(), c => c.UserId, u => u.Id, (c, u) => new { Certificate = c, User = u })
            .Where(x => branchId == null || x.User.BranchId == branchId)
            .Join(db.Courses, x => x.Certificate.CourseId, course => course.Id, (x, course) => new
            {
                x.Certificate, x.User.Username, x.User.FullName, x.User.BranchId, CourseTitle = course.Title,
            })
            .ToListAsync(cancellationToken);

        var versionIds = rows.Select(r => r.Certificate.RecipeVersionId).OfType<long>().Distinct().ToList();
        var versions = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, v.VersionNo, v.State, r.RecipeCode, r.Name })
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var branches = await db.Branches.AsNoTracking().IgnoreQueryFilters()
            .ToDictionaryAsync(b => b.Id, b => new { b.BranchCode, b.Name }, cancellationToken);

        return new TabularReport("Certification",
            Columns,
            [
                .. rows.Select(r =>
                    {
                        var branch = r.BranchId is { } id ? branches.GetValueOrDefault(id) : null;
                        var version = r.Certificate.RecipeVersionId is { } versionId ? versions.GetValueOrDefault(versionId) : null;
                        return (Sort: (branch?.BranchCode, r.Username, r.Certificate.IssuedAt), Cells: (IReadOnlyList<object?>)
                        [
                            branch?.BranchCode, branch?.Name, r.Username, r.FullName, r.CourseTitle, version?.RecipeCode,
                            version?.Name, version?.VersionNo, version?.State.Code(), r.Certificate.Id,
                            r.Certificate.Status.Code(), r.Certificate.IssuedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            r.Certificate.SupersededBy,
                        ]);
                    })
                    .OrderBy(x => x.Sort.BranchCode).ThenBy(x => x.Sort.Username).ThenBy(x => x.Sort.IssuedAt)
                    .Select(x => x.Cells),
            ]);
    }
}
