using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Recipes.Drafting;

/// <summary>
/// UC-06 (generate an AI draft) and UC-08 (repair). An AI draft is only ever
/// content of a DRAFT version (BR-05): it is validated like any other and
/// released by a person, never by this service.
/// </summary>
public sealed class RecipeDraftingService(IBrewForgeDbContext db, IRecipeDraftModel model,
    RecipeValidationService validation, ICurrentUser currentUser, TimeProvider clock)
{
    /// <summary>BR-07: a non-conforming response is retried once, then rejected.</summary>
    public const int MaxAttempts = 2;

    public async Task<DraftResultDto> GenerateDraftAsync(long versionId, GenerateDraftRequest request,
        CancellationToken cancellationToken)
    {
        new FieldErrors()
            .RequiredMax("description", request.Description?.Trim(), 4000)
            .ThrowIfAny();

        var version = await db.FindVersionAsync(versionId, cancellationToken);
        version.EnsureMutable();
        var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);
        var (equipment, ingredients) = await db.LoadCatalogAsync(cancellationToken);

        var prompt = DraftPromptBuilder.ForNewDraft(recipe, request.Description!, ingredients, equipment);
        var draft = await AskModelAsync(recipe.Id, version, prompt,
            raw => RecipeDraftParser.Parse(raw, ingredients, equipment), AuditActions.AiDraft,
            () => new { version.VersionNo, model = model.ModelName }, cancellationToken);

        return await ValidateAndReturnAsync(version, draft.Notes, draft.ServingSizeMl, cancellationToken);
    }

    public async Task<DraftResultDto> RepairAsync(long versionId, RepairRequest request,
        CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.Mode is not null, "mode", "is required: AI or MANUAL").ThrowIfAny();

        var version = await db.FindVersionAsync(versionId, cancellationToken);
        version.EnsureMutable();

        return request.Mode == RepairMode.Manual
            ? await RepairManuallyAsync(version, request, cancellationToken)
            : await RepairWithModelAsync(version, cancellationToken);
    }

    private async Task<DraftResultDto> RepairManuallyAsync(RecipeVersion version, RepairRequest request,
        CancellationToken cancellationToken)
    {
        var patch = request.Patch ?? throw DomainException.Validation("A manual repair needs the corrected content.",
            new ErrorDetail("patch", "is required when mode is MANUAL"));

        var specs = await db.ResolveAsync(patch, version, cancellationToken);
        version.ReplaceContent(specs, currentUser.RequireUserId());

        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.ManualRepair,
            new { version.VersionNo, steps = specs.Count });
        return await ValidateAndReturnAsync(version, null, null, cancellationToken);
    }

    private async Task<DraftResultDto> RepairWithModelAsync(RecipeVersion version, CancellationToken cancellationToken)
    {
        // The limit is checked before the model is called: the fourth attempt
        // costs nothing and changes nothing.
        AiRepairPolicy.EnsureAnotherRepairAllowed(await CountAiRepairsAsync(version.Id, cancellationToken));

        var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);
        var (equipment, ingredients) = await db.LoadCatalogAsync(cancellationToken);

        var report = version.Validate(new ValidationCatalog(equipment, ingredients));
        if (report.Passed)
        {
            // Nothing to repair: do not spend one of the three attempts.
            return await ValidateAndReturnAsync(version, null, null, cancellationToken);
        }

        var prompt = DraftPromptBuilder.ForRepair(recipe, version, report, ingredients, equipment);
        var violationsBefore = report.Violations.Count();
        var draft = await AskModelAsync(recipe.Id, version, prompt,
            raw => RecipeDraftParser.Parse(raw, ingredients, equipment), AuditActions.AiRepair,
            () => new { version.VersionNo, model = model.ModelName, violationsBefore }, cancellationToken);

        return await ValidateAndReturnAsync(version, draft.Notes, draft.ServingSizeMl, cancellationToken);
    }

    /// <summary>
    /// Calls the model, at most twice, and applies the first conforming
    /// answer to the version. Every call is written to <c>ai_draft_log</c>
    /// and saved at once, so the record survives whatever happens next
    /// (BR-06). A response that does not conform is never returned to the
    /// caller (BR-07).
    ///
    /// The audit entry of an applied answer is stored in the same transaction
    /// as the content it describes. For a repair that entry is the count
    /// towards the limit of three, so it must not be possible to change the
    /// draft without being counted.
    /// </summary>
    private async Task<ParsedDraft> AskModelAsync(long recipeId, RecipeVersion version, DraftModelRequest prompt,
        Func<string, DraftParseResult> parse, string auditAction, Func<object> auditPayload,
        CancellationToken cancellationToken)
    {
        var editorId = currentUser.RequireUserId();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string raw;
            try
            {
                raw = await model.CompleteAsync(prompt, cancellationToken);
            }
            catch (DraftModelException exception)
            {
                await LogAsync(recipeId, prompt, rawResponse: null, schemaValid: false, cancellationToken);
                throw exception.Failure switch
                {
                    DraftModelFailure.TimedOut => DomainException.Refused(ErrorCodes.LlmTimeout,
                        "The AI service did not respond in time. You can continue by authoring the draft manually."),
                    DraftModelFailure.NotConfigured => DomainException.Refused("LLM_NOT_CONFIGURED",
                        "AI drafting is not configured on this server. Author the draft manually."),
                    _ => DomainException.Refused("LLM_UNAVAILABLE",
                        "The AI service is unavailable. You can continue by authoring the draft manually."),
                };
            }

            var parsed = parse(raw);
            var conforms = parsed.Conforms && TryApply(version, parsed.Draft!, editorId);
            if (conforms) db.Audit(AuditEntities.RecipeVersion, () => version.Id, auditAction, auditPayload());
            await LogAsync(recipeId, prompt, raw, conforms, cancellationToken);

            if (conforms) return parsed.Draft!;
        }

        throw DomainException.Refused(ErrorCodes.LlmSchemaMismatch,
            "The AI service returned a response that does not match the required format. " +
            "Please author the draft manually or retry.", "BR-07");
    }

    /// <summary>
    /// A response can satisfy the JSON schema and still not be a storable
    /// draft. The aggregate is the judge of that, and a refusal from it
    /// leaves the version exactly as it was.
    /// </summary>
    private static bool TryApply(RecipeVersion version, ParsedDraft draft, long editorId)
    {
        try
        {
            version.ReplaceContent(draft.Steps, editorId);
            return true;
        }
        catch (DomainException exception) when (exception.Kind == ErrorKind.Validation || exception.Rule == "BR-09")
        {
            return false;
        }
    }

    private async Task LogAsync(long recipeId, DraftModelRequest prompt, string? rawResponse, bool schemaValid,
        CancellationToken cancellationToken)
    {
        db.AiDraftLogs.Add(new AiDraftLog(recipeId, prompt.PromptText, model.ModelName, rawResponse, schemaValid,
            clock.GetUtcNow()));
        // Saved now. On a conforming answer this also stores the new content
        // of the version, which is what the log entry describes.
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<DraftResultDto> ValidateAndReturnAsync(RecipeVersion version, string? notes,
        int? servingSizeMl, CancellationToken cancellationToken)
    {
        // Step ids exist only once the content is stored, and the validation rows refer to them.
        await db.SaveChangesAsync(cancellationToken);

        var (report, runAt) = await validation.RunAsync(version, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var repairs = await CountAiRepairsAsync(version.Id, cancellationToken);
        return new DraftResultDto(await db.ToDtoAsync(version, cancellationToken),
            ValidationResultDto.From(version.Id, report, runAt), notes, servingSizeMl, repairs,
            Math.Max(0, AiRepairPolicy.MaxAutomaticRepairs - repairs));
    }

    private Task<int> CountAiRepairsAsync(long versionId, CancellationToken cancellationToken) =>
        db.AuditLogs.CountAsync(a => a.EntityType == AuditEntities.RecipeVersion && a.EntityId == versionId
                                     && a.Action == AuditActions.AiRepair, cancellationToken);
}
