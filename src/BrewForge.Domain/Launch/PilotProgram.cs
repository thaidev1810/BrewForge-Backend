using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;

namespace BrewForge.Domain.Launch;

/// <summary>The <c>pilot_state</c> enumeration.</summary>
public enum PilotState
{
    Draft,
    Running,
    Ended,
    Cancelled,
}

/// <summary>The <c>pilot_branch_state</c> enumeration.</summary>
public enum PilotBranchState
{
    Preparing,
    Ready,
    Live,
}

/// <summary>The <c>launch_decision</c> enumeration.</summary>
public enum LaunchDecisionType
{
    Rollout,
    Revise,
    Discontinue,
}

/// <summary>The participation of one branch in a pilot, and how far it is through the launch gate.</summary>
public sealed class PilotBranch
{
    private PilotBranch() { }

    internal PilotBranch(long branchId)
    {
        BranchId = branchId;
    }

    public long Id { get; private set; }
    public long PilotProgramId { get; private set; }
    public long BranchId { get; private set; }
    public PilotBranchState ReadinessState { get; private set; } = PilotBranchState.Preparing;
    public DateTimeOffset? WentLiveAt { get; private set; }

    public bool IsLive => ReadinessState == PilotBranchState.Live;

    /// <summary>READY means the threshold is met now; it is lost again if certified staff are.</summary>
    internal void Recompute(int certifiedCount, int minCertifiedStaff)
    {
        if (IsLive) return;
        ReadinessState = certifiedCount >= minCertifiedStaff ? PilotBranchState.Ready : PilotBranchState.Preparing;
    }

    internal void MarkLive(DateTimeOffset now)
    {
        ReadinessState = PilotBranchState.Live;
        WentLiveAt = now.ToUniversalTime();
    }
}

/// <summary>
/// The decision taken at the end of a pilot, with the figures as they were
/// evaluated at that moment (BR-27). It is never recomputed.
/// </summary>
public sealed class LaunchDecision
{
    private LaunchDecision() { }

    internal LaunchDecision(LaunchDecisionType decision, string evaluatedJson, long decidedBy, DateTimeOffset decidedAt)
    {
        Decision = decision;
        EvaluatedJson = evaluatedJson;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
    }

    public long Id { get; private set; }
    public long PilotProgramId { get; private set; }
    public LaunchDecisionType Decision { get; private set; }
    public string EvaluatedJson { get; private set; } = null!;
    public long DecidedBy { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }
}

/// <summary>
/// A time-boxed market test of one released version at a chosen set of
/// branches (UC-21). The lifecycle of the data dictionary is enforced here.
/// </summary>
public sealed class PilotProgram
{
    public const string StateRule = "STATE_TRANSITION";

    private readonly List<PilotBranch> _branches = [];

    private PilotProgram() { }

    public long Id { get; private set; }
    public long RecipeVersionId { get; private set; }
    public string Name { get; private set; } = null!;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public string CriteriaJson { get; private set; } = null!;
    public int MinCertifiedStaff { get; private set; } = 2;
    public PilotState State { get; private set; } = PilotState.Draft;
    public long CreatedBy { get; private set; }

    public IReadOnlyList<PilotBranch> Branches => _branches;
    public LaunchDecision? Decision { get; private set; }

    /// <summary>DRAFT or RUNNING: the states in which the pilot holds its version (BR-28).</summary>
    public bool IsActive => State is PilotState.Draft or PilotState.Running;

    public PilotCriteria Criteria() => PilotCriteria.FromJson(CriteriaJson);

    /// <summary>
    /// UC-21. A pilot binds one released version of a drink that is being
    /// launched. A drink the chain already sells is live without a pilot
    /// (BR-36), and a version holds one active pilot at a time (BR-28).
    /// </summary>
    /// <param name="pilotsOfVersion">Every pilot that exists for the version.</param>
    public static PilotProgram Create(Recipe recipe, RecipeVersion version, IEnumerable<PilotProgram> pilotsOfVersion,
        string? name, DateOnly startDate, DateOnly endDate, PilotCriteria criteria, int minCertifiedStaff,
        IReadOnlyCollection<long> branchIds, long createdBy)
    {
        if (version.RecipeId != recipe.Id) throw new ArgumentException("Not a version of that recipe.", nameof(version));
        if (version.State != VersionState.Released)
        {
            throw DomainException.RuleViolation("PILOT_VERSION_NOT_RELEASED",
                $"A pilot tests a released version; this one is {version.State.Code()}.",
                details: new ErrorDetail("recipeVersionId", $"is {version.State.Code()}"));
        }
        if (recipe.Origin == RecipeOrigin.Existing)
        {
            throw DomainException.RuleViolation("BR-36",
                "A drink the chain already sells is live at its branches without a pilot.",
                details: new ErrorDetail("recipeVersionId", "belongs to a recipe of origin EXISTING"));
        }
        if (pilotsOfVersion.Any(pilot => pilot.RecipeVersionId == version.Id && pilot.IsActive))
        {
            throw DomainException.RuleViolation("BR-28",
                "This recipe version already has a pilot in DRAFT or RUNNING state.",
                details: new ErrorDetail("recipeVersionId", "already has an active pilot"));
        }

        var pilot = new PilotProgram { RecipeVersionId = version.Id, CreatedBy = createdBy };
        pilot.Set(recipe.Id, name, startDate, endDate, criteria, minCertifiedStaff, branchIds);
        return pilot;
    }

    /// <summary>
    /// Changes the name, period, branches, threshold or criteria. BR-26:
    /// only while the pilot is a DRAFT; from the moment it starts, what it is
    /// measured against is fixed.
    /// </summary>
    public void Update(long recipeId, string? name, DateOnly startDate, DateOnly endDate, PilotCriteria criteria,
        int minCertifiedStaff, IReadOnlyCollection<long> branchIds)
    {
        EnsureEditable();
        Set(recipeId, name, startDate, endDate, criteria, minCertifiedStaff, branchIds);
    }

    /// <summary>BR-26: a pilot is open to change only while it is a DRAFT.</summary>
    public void EnsureEditable()
    {
        if (State != PilotState.Draft)
        {
            throw DomainException.RuleViolation("BR-26",
                $"The pilot is {State.Code()}: its criteria were frozen when it started and can no longer be changed.");
        }
    }

    /// <summary>DRAFT to RUNNING. From here on the criteria are read-only (BR-26).</summary>
    public void Start(DateOnly today)
    {
        EnsureIs(PilotState.Draft, "started");
        if (today > EndDate)
        {
            throw DomainException.RuleViolation("PILOT_PERIOD_OVER",
                $"The pilot period ended on {EndDate:yyyy-MM-dd}. Change the period before starting it.",
                details: new ErrorDetail("endDate", "is in the past"));
        }
        State = PilotState.Running;
    }

    /// <summary>RUNNING to ENDED, by the calendar: the day after <c>end_date</c>. Returns whether it ended now.</summary>
    public bool EndIfDue(DateOnly today)
    {
        if (State != PilotState.Running || today <= EndDate) return false;
        State = PilotState.Ended;
        return true;
    }

    /// <summary>DRAFT or RUNNING to CANCELLED: abandoned, and its version is free for another pilot.</summary>
    public void Cancel()
    {
        if (!IsActive)
        {
            throw DomainException.RuleViolation(StateRule, $"A pilot that is {State.Code()} cannot be cancelled.");
        }
        State = PilotState.Cancelled;
    }

    public PilotBranch BranchOf(long branchId) =>
        _branches.SingleOrDefault(branch => branch.BranchId == branchId)
        ?? throw DomainException.NotFound("Pilot branch", branchId);

    /// <summary>The readiness checker: a branch is READY exactly while enough of its staff are certified.</summary>
    public void RecomputeReadiness(long branchId, int certifiedCount)
    {
        if (!IsActive) return;
        _branches.SingleOrDefault(branch => branch.BranchId == branchId)?.Recompute(certifiedCount, MinCertifiedStaff);
    }

    /// <summary>
    /// The launch gate (BR-23). A branch of a RUNNING pilot is opened for
    /// sale only when enough of its staff hold a valid certificate on the
    /// pilot's version. A branch that does not sell the drink yet goes
    /// READY to LIVE; one that sells an earlier version moves to this one.
    /// Returns the version the branch sold until now, when it sold one.
    /// </summary>
    /// <param name="launch">The launch status of the branch for the pilot's drink.</param>
    /// <param name="certifiedCount">Counted now, not read from what was stored.</param>
    public long? GoLive(long branchId, BranchLaunchStatus launch, int certifiedCount, DateTimeOffset now)
    {
        var branch = EnsureGateApplies(branchId);
        if (launch.BranchId != branchId) throw new ArgumentException("Not the launch status of that branch.", nameof(launch));

        branch.Recompute(certifiedCount, MinCertifiedStaff);
        var previousVersion = launch.OpenForSale(RecipeVersionId, MinCertifiedStaff, certifiedCount, now);
        branch.MarkLive(now);
        return previousVersion;
    }

    /// <summary>
    /// Whether the gate is there to be passed at all: the pilot is RUNNING,
    /// the branch takes part in it, and it has not gone live in it already.
    /// </summary>
    public PilotBranch EnsureGateApplies(long branchId)
    {
        EnsureIs(PilotState.Running, "taken live at a branch");
        var branch = BranchOf(branchId);
        if (branch.IsLive)
        {
            throw DomainException.RuleViolation(StateRule, "This branch is already live in the pilot.");
        }
        return branch;
    }

    /// <summary>
    /// UC-25. BR-27: a decision is recorded only once the pilot has ENDED,
    /// once, and with the evaluated figures as they stand at this moment.
    /// </summary>
    public LaunchDecision Decide(LaunchDecisionType decision, string evaluatedJson, long decidedBy, DateTimeOffset now)
    {
        if (State != PilotState.Ended)
        {
            throw DomainException.RuleViolation("BR-27",
                $"A launch decision is recorded once the pilot has ended; this one is {State.Code()}.",
                details: new ErrorDetail("state", $"is {State.Code()}, not ENDED"));
        }
        if (Decision is not null)
        {
            throw DomainException.RuleViolation("BR-27",
                $"The decision of this pilot was already recorded: {Decision.Decision.Code()}.",
                details: new ErrorDetail("decision", "was already recorded"));
        }
        new FieldErrors()
            .Check(Enum.IsDefined(decision), "decision", "must be ROLLOUT, REVISE or DISCONTINUE")
            .ThrowIfAny();

        Decision = new LaunchDecision(decision, evaluatedJson, decidedBy, now);
        return Decision;
    }

    private void Set(long recipeId, string? name, DateOnly startDate, DateOnly endDate, PilotCriteria criteria,
        int minCertifiedStaff, IReadOnlyCollection<long> branchIds)
    {
        name = name?.Trim() ?? "";
        new FieldErrors()
            .RequiredMax("name", name, 120)
            .Check(endDate >= startDate, "endDate", "must not be before startDate")
            .Check(minCertifiedStaff >= 1, "minCertifiedStaff", "must be at least 1")
            .Check(branchIds.Count > 0, "branchIds", "at least one branch is required")
            .Check(branchIds.Distinct().Count() == branchIds.Count, "branchIds", "names a branch more than once")
            .Check(criteria.ControlRecipeId != recipeId, "criteria", "the control drink must be another drink than the one piloted")
            .ThrowIfAny();

        Name = name;
        StartDate = startDate;
        EndDate = endDate;
        CriteriaJson = criteria.ToJson();
        MinCertifiedStaff = minCertifiedStaff;

        _branches.RemoveAll(branch => !branchIds.Contains(branch.BranchId));
        _branches.AddRange(branchIds.Where(id => _branches.All(branch => branch.BranchId != id))
            .Select(id => new PilotBranch(id)));
    }

    private void EnsureIs(PilotState required, string action)
    {
        if (State != required)
        {
            throw DomainException.RuleViolation(StateRule,
                $"Only a {required.Code()} pilot can be {action}; this one is {State.Code()}.");
        }
    }
}
