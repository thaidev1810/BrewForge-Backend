using System.Globalization;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;

namespace BrewForge.Domain.Recipes.Validation;

/// <summary>
/// Ingredient constraints (BR-11). Every ingredient a step uses must be an
/// active entry of the catalogue; the unit the step states must be
/// convertible to the ingredient's own unit; and the ingredient may not be
/// in use beyond its shelf life.
///
/// The shelf-life window of an ingredient starts when the first step that
/// uses it starts and ends when the last step of the recipe ends. Its length
/// is the sum of the durations of those steps, taken in step order; a step
/// without a duration contributes nothing. The window may equal the shelf
/// life but not exceed it.
///
/// A leaf may have a brewing window, the range of water temperature it is
/// brewed at. A step that uses such a leaf and states its temperature is
/// held to that window. A step that states no temperature is not: the
/// recipes written before a step could state one are not made invalid by
/// the leaf gaining a window.
/// </summary>

public sealed class IngredientCheck : IRecipeCheck
{
    public const string Rule = "BR-11";
    private const decimal SecondsPerHour = 3600m;

    public CheckType CheckType => CheckType.Ingredient;

    public IReadOnlyList<Violation> Run(RecipeVersion version, ValidationCatalog catalog)
    {
        var violations = new List<Violation>();
        var steps = version.Steps.OrderBy(step => step.StepOrder).ToList();

        // remaining[i]: seconds from the start of step i to the end of the last step.
        var remaining = new long[steps.Count + 1];
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            remaining[i] = remaining[i + 1] + (steps[i].DurationSeconds ?? 0);
        }

        var firstUseChecked = new HashSet<long>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            foreach (var used in step.Ingredients)
            {
                var ingredient = catalog.Ingredient(used.IngredientId);
                if (ingredient is null)
                {
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"Ingredient {used.IngredientId} is not in the ingredient catalogue",
                        "an ingredient of the catalogue", $"ingredient id {used.IngredientId}"));
                    continue;
                }

                if (!ingredient.IsActive)
                {
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"Ingredient {ingredient.IngredientCode} is inactive",
                        CatalogStatus.Active.Code(), ingredient.Status.Code()));
                }

                var ingredientUnit = ingredient.Unit.Code();
                if (!UnitConverter.CanConvert(used.Unit, ingredientUnit))
                {
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"Unit '{used.Unit}' cannot be converted to '{ingredientUnit}', the unit of {ingredient.IngredientCode}",
                        $"a unit convertible to {ingredientUnit}", used.Unit));
                }

                if (ingredient.HasBrewingWindow && step.TemperatureC is { } temperature
                    && (temperature < ingredient.BrewTempMinC || temperature > ingredient.BrewTempMaxC))
                {
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"{ingredient.IngredientCode} is brewed at {Degrees(ingredient.BrewTempMinC!.Value)}-{Degrees(ingredient.BrewTempMaxC!.Value)} C, and the step is done at {Degrees(temperature)} C",
                        $"{Degrees(ingredient.BrewTempMinC.Value)} - {Degrees(ingredient.BrewTempMaxC.Value)}", Degrees(temperature)));
                }

                // Only the first use opens the window; later uses are inside it.

                if (!firstUseChecked.Add(ingredient.Id)) continue;

                var windowSeconds = remaining[i];
                if (windowSeconds > ingredient.ShelfLifeHours * SecondsPerHour)
                {
                    var hours = Hours(windowSeconds);
                    violations.Add(new Violation(step.Id, step.StepOrder, Rule,
                        $"{ingredient.IngredientCode} is in use for {hours} h from step {step.StepOrder} to the end of the recipe, beyond its shelf life of {ingredient.ShelfLifeHours} h",
                        $"<= {ingredient.ShelfLifeHours} h", $"{hours} h"));
                }
            }
        }

        return violations;
    }

    private static string Degrees(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Hours(long seconds) =>
        (seconds / SecondsPerHour).ToString("0.##", CultureInfo.InvariantCulture);
}
