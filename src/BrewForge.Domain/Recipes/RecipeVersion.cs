using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Domain.Recipes;

/// <summary>
/// A numbered revision of a recipe, and the aggregate root of its steps,
/// their dependencies and their ingredients. All changes to the content go
/// through this class, which is where the lifecycle of the data dictionary
/// and the rules BR-01 to BR-05 and BR-12 are enforced.
/// </summary>
public sealed class RecipeVersion : INeverDeleted
{
    public const int MaxSteps = 40;
    public const decimal MaxQuantity = 9_999_999.999m; // NUMERIC(10,3)

    /// <summary>Named in the envelope when a transition is not in the state model.</summary>
    public const string StateRule = "STATE_TRANSITION";

    /// <summary>The only transitions that exist (data dictionary, section 7).</summary>
    private static readonly HashSet<(VersionState From, VersionState To)> Transitions =
    [
        (VersionState.Draft, VersionState.Draft),
        (VersionState.Draft, VersionState.Validated),
        (VersionState.Validated, VersionState.Rejected),
        (VersionState.Rejected, VersionState.Draft),
        (VersionState.Validated, VersionState.Released),
        (VersionState.Released, VersionState.Superseded),
    ];

    private readonly List<RecipeStep> _steps = [];

    private RecipeVersion() { }

    public long Id { get; private set; }
    public long RecipeId { get; private set; }

    /// <summary>Monotonic per recipe and never reused (BR-03).</summary>
    public int VersionNo { get; private set; }
    public VersionState State { get; private set; } = VersionState.Draft;

    /// <summary>Set on release; nothing may change afterwards (BR-01).</summary>
    public bool IsImmutable { get; private set; }

    /// <summary>SHA-256 of the sealed content.</summary>
    public string? ContentHash { get; private set; }

    /// <summary>The author: the user who created or last edited the content (BR-12).</summary>
    public long CreatedBy { get; private set; }

    /// <summary>Must differ from <see cref="CreatedBy"/> (BR-12).</summary>
    public long? ApprovedBy { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>The steps, in no particular order. Use <see cref="OrderedSteps"/> to read them in sequence.</summary>
    public IReadOnlyList<RecipeStep> Steps => _steps;

    public IReadOnlyList<RecipeStep> OrderedSteps() => [.. _steps.OrderBy(step => step.StepOrder)];

    // A version is retained forever, released or not: its number is never reused (BR-01, BR-03).
    string INeverDeleted.RetentionRule => "BR-01";

    /// <summary>BR-05: every draft, authored or AI-generated, starts in DRAFT.</summary>
    public static RecipeVersion CreateDraft(long recipeId, int versionNo, long authorId)
    {
        if (versionNo < 1) throw new ArgumentOutOfRangeException(nameof(versionNo));
        return new RecipeVersion { RecipeId = recipeId, VersionNo = versionNo, CreatedBy = authorId };
    }

    /// <summary>
    /// Replaces the whole content of the draft. Allowed in DRAFT, and in
    /// REJECTED, which it reopens as DRAFT. The editor becomes the author on
    /// record, so that whoever last touched the content cannot also approve
    /// it (BR-12).
    /// </summary>
    public void ReplaceContent(IReadOnlyList<StepSpec> steps, long editorId)
    {
        EnsureMutable();
        if (State == VersionState.Validated)
        {
            throw DomainException.RuleViolation(StateRule,
                "A VALIDATED version is awaiting review and can only be changed through the review.");
        }
        ValidateContent(steps);

        TransitionTo(VersionState.Draft);
        CreatedBy = editorId;

        _steps.Clear();
        var created = steps.OrderBy(spec => spec.StepOrder).Select(spec => (Spec: spec, Step: new RecipeStep(spec))).ToList();
        var byOrder = created.ToDictionary(pair => pair.Spec.StepOrder, pair => pair.Step);
        foreach (var (spec, step) in created)
        {
            foreach (var dependency in spec.DependsOn)
            {
                step.DependOn(byOrder[dependency.StepOrder], dependency.Type);
            }
            _steps.Add(step);
        }
    }

    /// <summary>
    /// The aggregate decides whether it is valid: the three independent
    /// checks, run against the master data as it stands now (BR-08).
    /// </summary>
    public ValidationReport Validate(ValidationCatalog catalog) => RecipeValidator.Validate(this, catalog);

    /// <summary>DRAFT to VALIDATED, and only on a report in which all three checks passed (BR-08).</summary>
    public void Submit(ValidationReport report)
    {
        EnsureMutable();
        EnsureCanMoveTo(VersionState.Validated);
        if (!report.Passed)
        {
            var count = report.Violations.Count();
            throw DomainException.Refused(ErrorCodes.ValidationFailedOnChecks,
                $"Validation failed: {count} violations found. Review the details on each step.", "BR-08",
                [.. report.Violations.Select(v => new ErrorDetail(
                    v.StepOrder is { } order ? $"steps[{order}]" : "steps", $"{v.Rule}: {v.Message}"))]);
        }
        TransitionTo(VersionState.Validated);
    }

    /// <summary>
    /// UC-09: the manager's decision on every step of a VALIDATED version.
    /// One rejected step rejects the version. Otherwise an edited step has
    /// its text replaced and the version goes back to DRAFT, with the
    /// reviewer as its author on record: having changed the content, the
    /// reviewer may no longer be the one who releases it (BR-12). Only when
    /// every step is accepted does the version stay VALIDATED.
    /// </summary>
    public ReviewOutcome Review(IReadOnlyList<StepDecision> decisions, long reviewerId)
    {
        EnsureMutable();
        if (State != VersionState.Validated)
        {
            throw DomainException.RuleViolation(StateRule,
                $"Only a VALIDATED version can be reviewed; this one is {State.Code()}.");
        }

        var stepIds = _steps.Select(step => step.Id).ToHashSet();
        var decided = decisions.Select(decision => decision.StepId).ToList();
        var errors = new FieldErrors()
            .Check(decided.All(stepIds.Contains), "decisions", "refers to a step that is not part of this version")
            .Check(decided.Distinct().Count() == decided.Count, "decisions", "decides the same step more than once")
            .Check(stepIds.All(decided.Contains), "decisions", "must decide every step of the version");
        for (var i = 0; i < decisions.Count; i++)
        {
            if (decisions[i].Action != ReviewAction.Edit) continue;
            errors.RequiredMax($"decisions[{i}].editedText", decisions[i].EditedText?.Trim(), 500);
        }
        errors.ThrowIfAny();

        if (decisions.Any(decision => decision.Action == ReviewAction.Reject))
        {
            TransitionTo(VersionState.Rejected);
            return ReviewOutcome.Rejected;
        }

        var edits = decisions.Where(decision => decision.Action == ReviewAction.Edit).ToList();
        if (edits.Count == 0) return ReviewOutcome.Accepted;

        foreach (var edit in edits)
        {
            _steps.Single(step => step.Id == edit.StepId).SetActionText(edit.EditedText!);
        }
        // The state model has no direct path from VALIDATED to DRAFT: the
        // content no longer is what was validated, so it leaves through
        // REJECTED and is reopened as a draft.
        TransitionTo(VersionState.Rejected);
        TransitionTo(VersionState.Draft);
        CreatedBy = reviewerId;
        return ReviewOutcome.Edited;
    }

    /// <summary>
    /// The conditions of release, checked in the order UC-10 fixes and before
    /// anything changes: the version is VALIDATED, it still passes against
    /// the master data of this moment (MSG-E08), and the approver is not the
    /// author (BR-12).
    /// </summary>
    internal void EnsureReleasable(ValidationReport freshReport, long approverId)
    {
        EnsureMutable();
        EnsureCanMoveTo(VersionState.Released);
        if (!freshReport.Passed)
        {
            throw DomainException.RuleViolation("BR-08",
                "Master data changed after approval, so this draft must be re-validated before release.",
                ErrorCodes.MasterDataChanged,
                [.. freshReport.Violations.Select(v => new ErrorDetail(
                    v.StepOrder is { } order ? $"steps[{order}]" : "steps", $"{v.Rule}: {v.Message}"))]);
        }
        if (approverId == CreatedBy)
        {
            throw DomainException.RuleViolation("BR-12",
                "You cannot approve a draft you authored. Approval must be performed by another R&D manager.",
                ErrorCodes.ApproverIsAuthor, new ErrorDetail("approvedBy", "equals createdBy"));
        }
    }

    /// <summary>Seals the version: from here on nothing about it may change (BR-01).</summary>
    internal void Seal(long approverId, int versionNo, DateTimeOffset now)
    {
        TransitionTo(VersionState.Released);
        VersionNo = versionNo;
        ContentHash = ComputeContentHash();
        ApprovedBy = approverId;
        ReleasedAt = now;
        IsImmutable = true;
    }

    /// <summary>RELEASED to SUPERSEDED when a newer version of the recipe is released. The row is kept forever.</summary>
    internal void Supersede(DateTimeOffset now)
    {
        TransitionTo(VersionState.Superseded);
        SupersededAt = now;
    }

    /// <summary>
    /// A VALIDATED version that no longer passes at release time is handed
    /// back for repair: VALIDATED to REJECTED.
    /// </summary>
    public void RejectAfterFailedRevalidation() => TransitionTo(VersionState.Rejected);

    /// <summary>
    /// SHA-256 over a canonical rendering of the content: every step in
    /// order, with its ingredients and its dependencies in a fixed order.
    /// Two versions have the same hash exactly when they prescribe the same
    /// procedure.
    /// </summary>
    public string ComputeContentHash()
    {
        var canonical = new StringBuilder();
        foreach (var step in OrderedSteps())
        {
            canonical.Append(CultureInfo.InvariantCulture,
                $"S|{step.StepOrder}|{step.ActionText}|{step.EquipmentClass}|{step.TechniqueGate}|{step.DurationSeconds}\n");
            foreach (var ingredient in step.Ingredients.OrderBy(i => i.IngredientId))
            {
                canonical.Append(CultureInfo.InvariantCulture,
                    $"I|{ingredient.IngredientId}|{ingredient.Quantity:0.000}|{ingredient.Unit}\n");
            }
            foreach (var dependency in step.Dependencies.OrderBy(d => d.DependsOnStep.StepOrder))
            {
                canonical.Append(CultureInfo.InvariantCulture,
                    $"D|{dependency.DependsOnStep.StepOrder}|{dependency.DependencyType.Code()}\n");
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>BR-01: nothing about a released version may change.</summary>
    public void EnsureMutable()
    {
        if (IsImmutable)
        {
            throw DomainException.RuleViolation("BR-01",
                "A released recipe version cannot be modified. Create a new version instead.",
                ErrorCodes.ReleasedVersionImmutable);
        }
    }

    private void EnsureCanMoveTo(VersionState target)
    {
        if (!Transitions.Contains((State, target)))
        {
            throw DomainException.RuleViolation(StateRule,
                $"A recipe version cannot move from {State.Code()} to {target.Code()}.");
        }
    }

    private void TransitionTo(VersionState target)
    {
        EnsureCanMoveTo(target);
        State = target;
    }

    /// <summary>
    /// The structural rules a draft must meet before it can even be stored.
    /// Whether the recipe is feasible is the validator's question, not this one.
    /// </summary>
    private static void ValidateContent(IReadOnlyList<StepSpec> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var errors = new FieldErrors();
        var orders = steps.Select(step => step.StepOrder).ToList();

        errors.Check(steps.Count <= MaxSteps, "steps", $"a recipe may have at most {MaxSteps} steps");
        errors.Check(orders.Order().SequenceEqual(Enumerable.Range(1, orders.Count)), "steps",
            "stepOrder values must be unique, start at 1 and be contiguous");

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = $"steps[{i}]";
            errors.RequiredMax($"{at}.actionText", step.ActionText?.Trim(), 500)
                .MaxLength($"{at}.equipmentClass", step.EquipmentClass?.Trim(), 40)
                .MaxLength($"{at}.techniqueGate", step.TechniqueGate?.Trim(), 255)
                .Check(step.DurationSeconds is null or > 0, $"{at}.durationSeconds", "must be greater than 0");

            for (var j = 0; j < step.Ingredients.Count; j++)
            {
                var ingredient = step.Ingredients[j];
                var ingredientAt = $"{at}.ingredients[{j}]";
                errors.Check(ingredient.Quantity > 0, $"{ingredientAt}.quantity", "must be greater than 0")
                    .Check(ingredient.Quantity <= MaxQuantity, $"{ingredientAt}.quantity", $"must not exceed {MaxQuantity}")
                    .Check(decimal.Round(ingredient.Quantity, 3) == ingredient.Quantity, $"{ingredientAt}.quantity",
                        "may have at most 3 decimal places")
                    .RequiredMax($"{ingredientAt}.unit", ingredient.Unit?.Trim(), 12);
            }
            errors.Check(step.Ingredients.Select(x => x.IngredientId).Distinct().Count() == step.Ingredients.Count,
                $"{at}.ingredients", "an ingredient may appear only once in a step");

            errors.Check(step.DependsOn.All(d => orders.Contains(d.StepOrder)), $"{at}.dependsOn",
                    "refers to a stepOrder that is not in this draft")
                .Check(step.DependsOn.Select(d => d.StepOrder).Distinct().Count() == step.DependsOn.Count,
                    $"{at}.dependsOn", "lists the same step more than once");
        }
        errors.ThrowIfAny();

        // A longer cycle can be stored and is the validator's to report; a
        // step that depends on itself cannot even be stored (BR-09).
        var selfDependent = steps.FirstOrDefault(step => step.DependsOn.Any(d => d.StepOrder == step.StepOrder));
        if (selfDependent is not null)
        {
            throw DomainException.RuleViolation("BR-09",
                $"Step {selfDependent.StepOrder} depends on itself. A step may not depend on itself.",
                ErrorCodes.CircularDependency,
                new ErrorDetail($"steps[{steps.ToList().IndexOf(selfDependent)}].dependsOn", "depends on itself"));
        }
    }
}
