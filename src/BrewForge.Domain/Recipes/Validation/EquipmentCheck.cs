using System.Globalization;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;

namespace BrewForge.Domain.Recipes.Validation;

/// <summary>
/// Equipment feasibility (BR-10). For every step that names an equipment
/// class: the class must be an active entry of the standard equipment
/// catalogue, and the dose the step puts through it must lie within the
/// class's Min-Max range, after conversion to the class's dosing unit.
///
/// What counts as "the dose" depends on the dosing unit of the class:
/// <list type="bullet">
/// <item><c>g</c> / <c>ml</c>: each ingredient quantity of the step whose unit
/// is of the same dimension. An ingredient measured in another dimension
/// (the water of a brewer that is dosed in grams of leaf) is not a dose of
/// this machine and is not range-checked here; that its unit suits the
/// ingredient is the ingredient check's concern.</item>
/// <item><c>sec</c>: the step's <c>duration_seconds</c>, which must be given.</item>
/// <item><c>degC</c> / <c>bar</c>: the schema records no numeric temperature
/// or pressure for a step (the technique gate is free text for the trainer),
/// so there is no value to compare and only the catalogue membership is
/// checked.</item>
/// </list>
/// </summary>
public sealed class EquipmentCheck : IRecipeCheck
{
    public const string Rule = "BR-10";

    public CheckType CheckType => CheckType.Equipment;

    public IReadOnlyList<Violation> Run(RecipeVersion version, ValidationCatalog catalog)
    {
        var violations = new List<Violation>();

        foreach (var step in version.Steps.Where(step => step.EquipmentClass is not null))
        {
            var equipment = catalog.Equipment(step.EquipmentClass!);
            if (equipment is null)
            {
                violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                    $"Equipment class {step.EquipmentClass} is not in the standard equipment catalogue",
                    "a class of the standard equipment catalogue", step.EquipmentClass!));
                continue;
            }
            if (!equipment.IsActive)
            {
                violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                    $"Equipment class {equipment.EquipmentClass} is inactive",
                    CatalogStatus.Active.Code(), equipment.Status.Code()));
                continue;
            }

            switch (equipment.DosingUnit)
            {
                case DosingUnit.Gram or DosingUnit.Millilitre:
                    CheckIngredientDoses(step, equipment, violations);
                    break;
                case DosingUnit.Second:
                    CheckDuration(step, equipment, violations);
                    break;
            }
        }

        return violations;
    }

    private static void CheckIngredientDoses(RecipeStep step, StandardEquipment equipment, List<Violation> violations)
    {
        var dosingUnit = equipment.DosingUnit.Code();
        foreach (var ingredient in step.Ingredients)
        {
            if (!UnitConverter.TryConvert(ingredient.Quantity, ingredient.Unit, dosingUnit, out var dose)) continue;
            if (dose >= equipment.MinThreshold && dose <= equipment.MaxThreshold) continue;

            violations.Add(OutOfRange(step, equipment, dose));
        }
    }

    private static void CheckDuration(RecipeStep step, StandardEquipment equipment, List<Violation> violations)
    {
        if (step.DurationSeconds is not { } seconds)
        {
            violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                $"{equipment.EquipmentClass} is dosed by time, so the step needs a duration",
                Range(equipment), "no duration"));
            return;
        }
        if (seconds >= equipment.MinThreshold && seconds <= equipment.MaxThreshold) return;

        violations.Add(OutOfRange(step, equipment, seconds));
    }

    private static Violation OutOfRange(RecipeStep step, StandardEquipment equipment, decimal dose)
    {
        var unit = equipment.DosingUnit.Code();
        return new Violation(step.Id, step.StepOrder, Rule,
            $"{Number(dose)} {unit} is outside the {Number(equipment.MinThreshold)}-{Number(equipment.MaxThreshold)} {unit} range of {equipment.EquipmentClass}",
            Range(equipment), Number(dose), ErrorCodes.DoseOutOfRange);
    }

    private static string Range(StandardEquipment equipment) =>
        $"{Number(equipment.MinThreshold)} - {Number(equipment.MaxThreshold)}";

    /// <summary>At least one decimal and no trailing zeros: 18 -> "18.0", 25.100 -> "25.1".</summary>
    internal static string Number(decimal value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
}
