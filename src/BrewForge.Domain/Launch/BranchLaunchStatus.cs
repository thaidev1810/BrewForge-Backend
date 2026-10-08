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
            LiveSince = now,
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
        LiveSince = now;
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

    private static void EnsureThreshold(int minCertifiedStaff) =>
        new FieldErrors().Check(minCertifiedStaff >= 0, "minCertifiedStaff", "must not be negative").ThrowIfAny();
}
