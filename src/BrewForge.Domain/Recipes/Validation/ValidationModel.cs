using BrewForge.Domain.MasterData;

namespace BrewForge.Domain.Recipes.Validation;

/// <summary>
/// One finding of a validator check, attached to the step that caused it.
/// <c>Rule</c>, <c>Message</c>, <c>Expected</c> and <c>Actual</c> are what is
/// stored in <c>validation_result.violation_detail</c>.
/// </summary>
public sealed record Violation(long? StepId, int? StepOrder, string Rule, string Message, string Expected,
    string Actual, string? Code = null);

public sealed record CheckResult(CheckType CheckType, IReadOnlyList<Violation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>The outcome of one validation run: the three checks, each with its violations.</summary>
public sealed record ValidationReport(IReadOnlyList<CheckResult> Checks)
{
    /// <summary>
    /// BR-08: a candidate passes only if all three checks pass. A partial
    /// pass is a failure, and so is a report from which a check is missing.
    /// </summary>
    public bool Passed =>
        Enum.GetValues<CheckType>().All(type => Checks.Any(check => check.CheckType == type && check.Passed))
        && Checks.All(check => check.Passed);

    public IEnumerable<Violation> Violations => Checks.SelectMany(check => check.Violations);
}

/// <summary>
/// The master data a validation run checks against, as it stands at that
/// moment: the chain-wide standard equipment profile and the ingredient
/// catalogue. Inactive entries are included on purpose, so that a check can
/// tell "inactive" from "does not exist".
/// </summary>
public sealed class ValidationCatalog
{
    private readonly Dictionary<string, StandardEquipment> _equipmentByClass;
    private readonly Dictionary<long, Ingredient> _ingredientsById;

    public ValidationCatalog(IEnumerable<StandardEquipment> equipment, IEnumerable<Ingredient> ingredients)
    {
        _equipmentByClass = equipment.ToDictionary(e => e.EquipmentClass, StringComparer.Ordinal);
        _ingredientsById = ingredients.ToDictionary(i => i.Id);
    }

    public StandardEquipment? Equipment(string equipmentClass) =>
        _equipmentByClass.GetValueOrDefault(equipmentClass);

    public Ingredient? Ingredient(long ingredientId) => _ingredientsById.GetValueOrDefault(ingredientId);
}

/// <summary>
/// One independent check. A new constraint category is a new implementation
/// added to <see cref="RecipeValidator"/>; nothing else changes (MA-01).
/// </summary>
public interface IRecipeCheck
{
    CheckType CheckType { get; }

    IReadOnlyList<Violation> Run(RecipeVersion version, ValidationCatalog catalog);
}

public static class RecipeValidator
{
    private static readonly IRecipeCheck[] Checks = [new EquipmentCheck(), new OrderingCheck(), new IngredientCheck()];

    /// <summary>
    /// Runs the three checks independently: each one runs to the end whatever
    /// the others found, so the author sees every violation in one pass.
    /// </summary>
    public static ValidationReport Validate(RecipeVersion version, ValidationCatalog catalog) =>
        new([.. Checks.Select(check => new CheckResult(check.CheckType, check.Run(version, catalog)))]);
}
