namespace BrewForge.Domain.Launch;

/// <summary>
/// The trading day an instant falls on. The chain trades in Vietnam, which
/// is UTC+7 all year: a branch that opens a drink at 06:00 does so on that
/// calendar day, not on the previous one as UTC would have it.
/// </summary>
public static class TradingCalendar
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);
}

public enum LaunchChangeKind
{
    /// <summary>LIVE to LIVE: the branch moved to another version of the drink.</summary>
    VersionMoved,

    /// <summary>LIVE to WITHDRAWN.</summary>
    Withdrawn,
}

/// <summary>
/// Something that happened to a launch status after the drink went live. The
/// row itself keeps only the present; the past is read from the audit trail,
/// so that a sale entered late is still attached to the version that was
/// live on its day (BR-24).
/// </summary>
/// <param name="PreviousVersionId">For a version move: the version the branch sold until then.</param>
public sealed record LaunchChange(LaunchChangeKind Kind, DateTimeOffset At, long? PreviousVersionId = null);
