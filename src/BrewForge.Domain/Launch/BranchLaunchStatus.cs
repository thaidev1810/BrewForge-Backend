using BrewForge.Domain.Common;

namespace BrewForge.Domain.Launch;

/// <summary>The <c>launch_status</c> enumeration.</summary>
public enum LaunchStatus
{
    Preparing,
    Ready,
    Live,
    Withdrawn,
}

/// <summary>
/// Whether a drink is live at a branch, on which version, and whether its
/// certificate coverage is met. This is the launch gate: the link between
/// the training half of the product and the sales half.
/// </summary>
public sealed class BranchLaunchStatus
{
    public const string StateRule = "STATE_TRANSITION";

    private BranchLaunchStatus() { }

    public long Id { get; private set; }
    public long BranchId { get; private set; }
    public long RecipeId { get; private set; }
    public long? RecipeVersionId { get; private set; }
    public LaunchStatus Status { get; private set; } = LaunchStatus.Preparing;
    public int MinCertifiedStaff { get; private set; } = 2;
    public int CertifiedCount { get; private set; }
    public bool CoverageMet { get; private set; }
    public DateTimeOffset? LiveSince { get; private set; }

    public bool IsLive => Status == LaunchStatus.Live;

    /// <summary>A drink is planned for the branch: PREPARING until enough staff are certified.</summary>
    public static BranchLaunchStatus Plan(long branchId, long recipeId, long recipeVersionId, int minCertifiedStaff)
    {
        EnsureThreshold(minCertifiedStaff);
        return new BranchLaunchStatus
        {
            BranchId = branchId,
            RecipeId = recipeId,
            RecipeVersionId = recipeVersionId,
            MinCertifiedStaff = minCertifiedStaff,
        };
    }

    /// <summary>
    /// BR-36: a drink the chain already sells is created LIVE with
    /// <c>coverage_met = false</c>. It is not blocked; the dashboard shows
    /// the shortfall until enough staff are certified.
    /// </summary>
    public static BranchLaunchStatus LiveForExistingRecipe(long branchId, long recipeId, long recipeVersionId,
        int minCertifiedStaff, DateTimeOffset now)
    {
        EnsureThreshold(minCertifiedStaff);
        return new BranchLaunchStatus
        {
            BranchId = branchId,
            RecipeId = recipeId,
            RecipeVersionId = recipeVersionId,
            MinCertifiedStaff = minCertifiedStaff,
            Status = LaunchStatus.Live,
            LiveSince = now.ToUniversalTime(),
            CoverageMet = false,
        };
    }

    /// <summary>
    /// The readiness checker. Stores the number of staff of the branch who
    /// hold a valid certificate on the bound version; PREPARING becomes READY
    /// once that number reaches the threshold.
    /// </summary>
    public void RecomputeCoverage(int certifiedCount)
    {
        CertifiedCount = Math.Max(0, certifiedCount);
        CoverageMet = CertifiedCount >= MinCertifiedStaff;
        if (Status == LaunchStatus.Preparing && CoverageMet) Status = LaunchStatus.Ready;
    }

    /// <summary>
    /// READY to LIVE. BR-23: a branch may not go from PREPARING straight to
    /// LIVE; the gate is the only path for a drink being launched.
    /// </summary>
    public void GoLive(DateTimeOffset now)
    {
        if (Status == LaunchStatus.Preparing)
        {
            throw DomainException.RuleViolation("BR-23",
                $"This branch is not ready: {CertifiedCount} of the {MinCertifiedStaff} certified staff it needs.",
                details: new ErrorDetail("certifiedCount", $"{CertifiedCount} is below {MinCertifiedStaff}"));
        }
        if (Status != LaunchStatus.Ready)
        {
            throw DomainException.RuleViolation(StateRule,
                $"A drink cannot go from {Status.Code()} to LIVE at a branch.");
        }
        Status = LaunchStatus.Live;
        LiveSince = now.ToUniversalTime();
    }

    /// <summary>LIVE to WITHDRAWN: the drink is discontinued at the branch.</summary>
    public void Withdraw()
    {
        if (Status != LaunchStatus.Live)
        {
            throw DomainException.RuleViolation(StateRule,
                $"Only a LIVE drink can be withdrawn; this one is {Status.Code()}.");
        }
        Status = LaunchStatus.Withdrawn;
    }

    /// <summary>
    /// LIVE to LIVE: the bound version changes after a rollout. Coverage is
    /// recomputed for the new version and may fall to false.
    /// </summary>
    public void MoveToVersion(long recipeVersionId, int certifiedCountOnThatVersion)
    {
        RecipeVersionId = recipeVersionId;
        RecomputeCoverage(certifiedCountOnThatVersion);
    }

    /// <summary>
    /// BR-24: the version a sale on that trading day is attached to. A drink
    /// is on sale at a branch from the day it went live until the day it was
    /// withdrawn, and at no other time. The version is the one the branch
    /// sold on that day, which is not the present one if the branch has
    /// moved to another version since.
    /// </summary>
    public long VersionSoldOn(DateOnly tradingDate, IReadOnlyCollection<LaunchChange> history)
    {
        var liveFrom = LiveSince is { } since ? TradingCalendar.DateOf(since) : (DateOnly?)null;
        var withdrawnOn = history.Where(change => change.Kind == LaunchChangeKind.Withdrawn)
            .Select(change => (DateOnly?)TradingCalendar.DateOf(change.At)).Max();

        var onSale = Status switch
        {
            LaunchStatus.Live => tradingDate >= liveFrom,
            // Without a record of when it was withdrawn, no day can be shown to lie before it.
            LaunchStatus.Withdrawn => tradingDate >= liveFrom && tradingDate <= withdrawnOn,
            _ => false,
        };
        if (!onSale) throw NotLiveOn(tradingDate);

        // A move on day M applies from M on: an earlier day belongs to the version before it.
        var versionThen = history
            .Where(change => change.Kind == LaunchChangeKind.VersionMoved && change.PreviousVersionId is not null
                             && TradingCalendar.DateOf(change.At) > tradingDate)
            .OrderBy(change => change.At)
            .Select(change => change.PreviousVersionId)
            .FirstOrDefault() ?? RecipeVersionId;
        return versionThen ?? throw NotLiveOn(tradingDate);
    }

    /// <summary>The refusal of BR-24, also raised when a branch has no launch status for the drink at all.</summary>
    public static DomainException NotLiveOn(DateOnly tradingDate) =>
        DomainException.RuleViolation("BR-24",
            $"The drink was not live at this branch on {tradingDate:yyyy-MM-dd}. Sales can only be recorded for a day on which it was on sale there.",
            ErrorCodes.ImportBranchNotLive, new ErrorDetail("tradingDate", "the branch was not live for this drink on that day"));

    private static void EnsureThreshold(int minCertifiedStaff) =>
        new FieldErrors().Check(minCertifiedStaff >= 0, "minCertifiedStaff", "must not be negative").ThrowIfAny();
}
