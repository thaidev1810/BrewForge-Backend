using BrewForge.Domain.Common;

namespace BrewForge.Domain.Recipes;

/// <summary>
/// UC-08: the automatic repair loop is limited to three iterations per draft,
/// after which a person has to step in. The count belongs to the draft, not
/// to a session, so starting again does not reset it.
/// </summary>
public static class AiRepairPolicy
{
    public const int MaxAutomaticRepairs = 3;
    public const string Rule = "AI_REPAIR_LIMIT";

    public static void EnsureAnotherRepairAllowed(int repairsSoFar)
    {
        if (repairsSoFar >= MaxAutomaticRepairs)
        {
            throw DomainException.RuleViolation(Rule,
                $"This draft has already had {MaxAutomaticRepairs} automatic AI repairs. Repair it manually.");
        }
    }
}
