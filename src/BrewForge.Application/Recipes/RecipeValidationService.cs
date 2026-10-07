using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Application.Recipes;

/// <summary>
/// UC-07: runs the three checks on a candidate and keeps the outcome. The
/// rules themselves live in the domain; this service only supplies the
/// catalogue as it stands now and stores what the aggregate reports.
/// </summary>
public sealed class RecipeValidationService(IBrewForgeDbContext db, TimeProvider clock)
{
    public async Task<ValidationResultDto> ValidateAsync(long versionId, CancellationToken cancellationToken)
    {
        var version = await db.FindVersionAsync(versionId, cancellationToken);
        var (report, runAt) = await RunAsync(version, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ValidationResultDto.From(version.Id, report, runAt);
    }

    /// <summary>DRAFT to VALIDATED, once the three checks pass (BR-08).</summary>
    public async Task<ValidationResultDto> SubmitAsync(long versionId, CancellationToken cancellationToken)
    {
        var version = await db.FindVersionAsync(versionId, cancellationToken);
        version.EnsureMutable();

        // The run is stored first, so a refused submission still leaves its
        // violations on record for the repair screen.
        var (report, runAt) = await RunAsync(version, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        version.Submit(report);
        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Submit, new { version.VersionNo });
        await db.SaveChangesAsync(cancellationToken);
        return ValidationResultDto.From(version.Id, report, runAt);
    }

    /// <summary>
    /// Runs the checks and queues the result rows and the audit entry on the
    /// context. The caller saves, so the run lands in the caller's unit of work.
    /// </summary>
    internal async Task<(ValidationReport Report, DateTimeOffset RunAt)> RunAsync(RecipeVersion version,
        CancellationToken cancellationToken)
    {
        var catalog = await db.LoadValidationCatalogAsync(cancellationToken);
        var report = version.Validate(catalog);
        var runAt = clock.GetUtcNow();

        db.ValidationResults.AddRange(ValidationResult.FromReport(version.Id, report, runAt));
        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Validate, new
        {
            report.Passed,
            checks = report.Checks.ToDictionary(c => c.CheckType.Code(), c => c.Violations.Count),
        });
        return (report, runAt);
    }
}
