namespace BrewForge.Domain.Common;

/// <summary>
/// Marks a record the system retains forever. Master data is deactivated
/// through its status and never deleted (BR-16); released versions,
/// certificates and audit entries are kept for the historical record (BR-01,
/// BR-15). The persistence layer refuses to delete anything carrying this
/// marker, whichever code path asks.
/// </summary>
public interface INeverDeleted
{
    /// <summary>The business rule that forbids the deletion, for the error envelope.</summary>
    string RetentionRule { get; }
}
