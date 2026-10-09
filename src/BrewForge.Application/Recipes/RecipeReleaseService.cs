using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Impact;
using BrewForge.Application.Launch;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Recipes;

public sealed record ReviewRequest(IReadOnlyList<StepDecisionRequest>? Decisions);

public sealed record StepDecisionRequest(long? StepId, ReviewAction? Action, string? EditedText);

public sealed record ReviewResultDto(ReviewOutcome Outcome, RecipeVersionDto Version);

/// <summary>
/// The released version, with what its release did to its predecessor.
/// <c>Replayed</c> is true when the request repeated an Idempotency-Key and
/// nothing was done a second time.
/// </summary>
public sealed record ReleaseResultDto(RecipeVersionDto Version, long? SupersededVersionId, bool Replayed);

/// <summary>The body of <c>POST /recipes/import-existing</c>: a recipe and its content in one document.</summary>
public sealed record ImportExistingRecipeRequest(string? RecipeCode, string? Name, RecipeCategory? Category,
    IReadOnlyList<StepRequest>? Steps);

public sealed record ImportedRecipeDto(RecipeDto Recipe, RecipeVersionDto Version, ValidationResultDto Validation);

/// <summary>UC-09, UC-10, the rollback of BR-04 and UC-26.</summary>
public sealed class RecipeReleaseService(IBrewForgeDbContext db, RecipeService recipes,
    RecipeValidationService validation, PilotService pilots, LaunchReadinessService launch,
    ImpactAnalysisService impact, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<ReviewResultDto> ReviewAsync(long versionId, ReviewRequest request,
        CancellationToken cancellationToken)
    {
        var version = await db.FindVersionAsync(versionId, cancellationToken);
        version.EnsureMutable();

        var requested = request.Decisions ?? throw DomainException.Validation("The review has no decisions.",
            new ErrorDetail("decisions", "is required"));
        var errors = new FieldErrors();
        for (var i = 0; i < requested.Count; i++)
        {
            errors.Check(requested[i].StepId is not null, $"decisions[{i}].stepId", "is required")
                .Check(requested[i].Action is not null, $"decisions[{i}].action", "is required: ACCEPT, EDIT or REJECT");
        }
        errors.ThrowIfAny();

        var decisions = requested
            .Select(d => new StepDecision(d.StepId!.Value, d.Action!.Value, d.EditedText))
            .ToList();
        var outcome = version.Review(decisions, currentUser.RequireUserId());

        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Review, new
        {
            version.VersionNo,
            outcome = outcome.Code(),
            state = version.State.Code(),
            decisions = decisions.Select(d => new { d.StepId, action = d.Action.Code(), d.EditedText }),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new ReviewResultDto(outcome, await db.ToDtoAsync(version, cancellationToken));
    }

    /// <summary>
    /// UC-10, in the order the use case fixes: re-validate against the master
    /// data of this moment, check separation of duty, allocate the version
    /// number, compute the content hash, seal, supersede the previous
    /// release, and write the audit entry.
    /// </summary>
    public async Task<ReleaseResultDto> ReleaseAsync(long versionId, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var approverId = currentUser.RequireUserId();
        var version = await db.FindVersionAsync(versionId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && await FindReplayAsync(version, idempotencyKey, cancellationToken) is { } replay)
        {
            return replay;
        }
        version.EnsureMutable();

        var currentlyReleased = await db.RecipeVersions
            .SingleOrDefaultAsync(v => v.RecipeId == version.RecipeId && v.State == VersionState.Released
                                       && v.Id != version.Id, cancellationToken);
        var highestVersionNo = await recipes.NextVersionNoAsync(version.RecipeId, cancellationToken) - 1;

        // BR-28: the version that would be superseded may be under test in a pilot.
        await pilots.EndDueAsync(cancellationToken);
        var pilotsOfReleased = currentlyReleased is null
            ? []
            : await db.PilotPrograms.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.RecipeVersionId == currentlyReleased.Id).ToListAsync(cancellationToken);

        // 1. Master data may have changed since the version was validated.
        var (report, _) = await validation.RunAsync(version, cancellationToken);

        RecipeRelease release;
        try
        {
            // 2. Separation of duty, and every other condition of release.
            release = RecipeRelease.Prepare(version, currentlyReleased, report, approverId, highestVersionNo,
                clock.GetUtcNow(), pilotsOfReleased);
        }
        catch (DomainException refusal) when (refusal.Code == ErrorCodes.MasterDataChanged)
        {
            // The failed run stays on record and the version goes back for
            // repair, which is where a version that no longer passes belongs.
            version.RejectAfterFailedRevalidation();
            db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.ReleaseRefused,
                new { version.VersionNo, reason = ErrorCodes.MasterDataChanged, violations = report.Violations.Count() });
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }

        return await db.InTransactionAsync(async () =>
        {
            // The validation run of this release is part of what is committed.
            release.SupersedePrevious();
            if (release.Superseded is { } previous)
            {
                db.Audit(AuditEntities.RecipeVersion, () => previous.Id, AuditActions.Supersede,
                    new { previous.VersionNo, supersededBy = version.Id });
            }
            await db.SaveChangesAsync(cancellationToken);

            // 3-5. Version number, content hash, immutability.
            release.Seal();
            db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Release, new
            {
                version.VersionNo,
                version.ContentHash,
                author = version.CreatedBy,
                approver = approverId,
                superseded = release.Superseded?.Id,
                idempotencyKey,
            });
            await db.SaveChangesAsync(cancellationToken);

            // BR-36: a drink the chain already sells is on sale on its released version without a pilot.
            var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);
            await launch.GoLiveWithoutPilotAsync(recipe, version, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            // UC-10 step 4: what was built on the superseded version is now behind (BR-15).
            if (release.Superseded is { } superseded)
            {
                await impact.PropagateSupersededAsync(superseded.Id, cancellationToken);
            }

            return new ReleaseResultDto(await db.ToDtoAsync(version, cancellationToken), release.Superseded?.Id,
                Replayed: false);
        }, cancellationToken);
    }

    /// <summary>
    /// BR-04: a rollback never restores a version in place. It creates a new
    /// version, with a new number, whose content is copied from the earlier
    /// one. The copy is validated at once and, if it still passes, is
    /// VALIDATED and waits for release by someone other than the manager who
    /// rolled back (BR-12).
    /// </summary>
    public async Task<RecipeVersionDto> RollbackAsync(long sourceVersionId, CancellationToken cancellationToken)
    {
        var managerId = currentUser.RequireUserId();
        var source = await db.FindVersionAsync(sourceVersionId, cancellationToken);
        if (source.State is not (VersionState.Superseded or VersionState.Released))
        {
            throw DomainException.RuleViolation("BR-04",
                $"Only a version that has been released can be rolled back to; this one is {source.State.Code()}.");
        }

        var copy = RecipeVersion.CreateDraft(source.RecipeId,
            await recipes.NextVersionNoAsync(source.RecipeId, cancellationToken), managerId);
        copy.ReplaceContent(source.ToSpecs(), managerId);

        db.RecipeVersions.Add(copy);
        db.Audit(AuditEntities.RecipeVersion, () => copy.Id, AuditActions.Rollback,
            new { copy.VersionNo, copiedFromVersionId = source.Id, copiedFromVersionNo = source.VersionNo });
        await db.SaveChangesAsync(cancellationToken);

        var (report, _) = await validation.RunAsync(copy, cancellationToken);
        if (report.Passed) copy.Submit(report);
        await db.SaveChangesAsync(cancellationToken);

        return await db.ToDtoAsync(copy, cancellationToken);
    }

    /// <summary>
    /// UC-26: brings a drink the chain already sells into the system as
    /// structured data. The origin is EXISTING whatever the caller sends,
    /// and the version starts as a DRAFT like any other (BR-05).
    /// </summary>
    public async Task<ImportedRecipeDto> ImportExistingAsync(ImportExistingRecipeRequest request,
        CancellationToken cancellationToken)
    {
        var authorId = currentUser.RequireUserId();

        return await db.InTransactionAsync(async () =>
        {
            var recipe = await recipes.CreateAsync(
                new CreateRecipeRequest(request.RecipeCode, request.Name, request.Category, RecipeOrigin.Existing),
                cancellationToken);

            var version = RecipeVersion.CreateDraft(recipe.Id, versionNo: 1, authorId);
            var specs = await db.ResolveAsync(new RecipeContentRequest(request.Steps), version, cancellationToken);
            version.ReplaceContent(specs, authorId);

            db.RecipeVersions.Add(version);
            db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.ImportExisting,
                new { recipe.RecipeCode, steps = specs.Count });
            await db.SaveChangesAsync(cancellationToken);

            var (report, runAt) = await validation.RunAsync(version, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return new ImportedRecipeDto(await recipes.GetAsync(recipe.Id, cancellationToken),
                await db.ToDtoAsync(version, cancellationToken),
                ValidationResultDto.From(version.Id, report, runAt));
        }, cancellationToken);
    }

    /// <summary>
    /// The original result of a release that already happened under this
    /// Idempotency-Key, so that a retried request does not act twice.
    /// </summary>
    private async Task<ReleaseResultDto?> FindReplayAsync(RecipeVersion version, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var releases = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.RecipeVersion && a.EntityId == version.Id
                        && a.Action == AuditActions.Release)
            .Select(a => a.PayloadJson)
            .ToListAsync(cancellationToken);

        foreach (var payload in releases.OfType<string>())
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("idempotencyKey", out var key)
                && key.ValueKind == JsonValueKind.String && key.GetString() == idempotencyKey)
            {
                long? superseded = document.RootElement.TryGetProperty("superseded", out var id)
                                   && id.ValueKind == JsonValueKind.Number
                    ? id.GetInt64()
                    : null;
                return new ReleaseResultDto(await db.ToDtoAsync(version, cancellationToken), superseded,
                    Replayed: true);
            }
        }
        return null;
    }
}
