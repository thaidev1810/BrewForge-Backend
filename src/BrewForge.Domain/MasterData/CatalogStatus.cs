namespace BrewForge.Domain.MasterData;

/// <summary>
/// Lifecycle of a catalogue entry (ingredient or equipment class). An entry is
/// deactivated, never deleted (BR-16); the validator rejects a recipe that
/// uses an inactive one.
/// </summary>
public enum CatalogStatus
{
    Active,
    Inactive,
}
