using BrewForge.Domain.Common;

namespace BrewForge.Domain.Audit;

/// <summary>
/// One entry of the append-only log of state-changing actions (SE-05). There
/// is deliberately no way to change an entry once it is constructed.
/// </summary>
public sealed class AuditLog : INeverDeleted
{
    private AuditLog() { }

    public AuditLog(long? userId, string entityType, long entityId, string action, string? payloadJson,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        UserId = userId;
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        PayloadJson = payloadJson;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }

    /// <summary>The actor; null for a system action.</summary>
    public long? UserId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public long EntityId { get; private set; }
    public string Action { get; private set; } = null!;
    public string? PayloadJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    string INeverDeleted.RetentionRule => "AUDIT";
}
