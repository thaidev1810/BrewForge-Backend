using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes.Validation;

namespace BrewForge.Domain.Recipes;

public enum ReviewAction
{
    Accept,
    Edit,
    Reject,
}

public enum ReviewOutcome
{
    Accepted,
    Edited,
    Rejected,
}

/// <summary>The manager's decision on one step (UC-09).</summary>
public sealed record StepDecision(long StepId, ReviewAction Action, string? EditedText);

/// <summary>
/// UC-10. The release of a version, as one decision of the domain: every
/// condition is checked before anything changes, and the only way to seal a
/// version is through this class, which supersedes the version that was
/// released before it. That is what keeps a recipe at one RELEASED version
/// at most (BR-02).
///
/// The two steps are separate because the database holds BR-02 as a unique
/// index that is checked per statement: the previous version has to leave
/// RELEASED in a statement of its own before the new one enters it.
/// </summary>
public sealed class RecipeRelease
{
    private readonly RecipeVersion _candidate;
    private readonly RecipeVersion? _currentlyReleased;
    private readonly long _approverId;
    private readonly int _versionNo;
    private readonly DateTimeOffset _now;

    private RecipeRelease(RecipeVersion candidate, RecipeVersion? currentlyReleased, long approverId, int versionNo,
        DateTimeOffset now)
    {
        _candidate = candidate;
        _currentlyReleased = currentlyReleased;
        _approverId = approverId;
        _versionNo = versionNo;
        _now = now;
    }

    /// <summary>The number the version will carry once released (BR-03).</summary>
    public int VersionNo => _versionNo;

    public RecipeVersion? Superseded => _currentlyReleased;

    /// <summary>
    /// Checks the release, in the order UC-10 fixes: re-validation against
    /// current master data, then separation of duty. Changes nothing.
    /// </summary>
    /// <param name="candidate">The VALIDATED version to release.</param>
    /// <param name="currentlyReleased">The RELEASED version of the same recipe, if it has one.</param>
    /// <param name="freshReport">A validation run made now, not the one made at submission.</param>
    /// <param name="highestVersionNo">The highest version number the recipe has used so far.</param>
    public static RecipeRelease Prepare(RecipeVersion candidate, RecipeVersion? currentlyReleased,
        ValidationReport freshReport, long approverId, int highestVersionNo, DateTimeOffset now)
    {
        if (currentlyReleased is not null)
        {
            if (currentlyReleased.RecipeId != candidate.RecipeId || currentlyReleased.State != VersionState.Released)
            {
                throw new ArgumentException("Must be the RELEASED version of the same recipe.", nameof(currentlyReleased));
            }
            if (ReferenceEquals(currentlyReleased, candidate))
            {
                candidate.EnsureMutable(); // always throws: it is already released (BR-01)
            }
        }

        candidate.EnsureReleasable(freshReport, approverId);

        return new RecipeRelease(candidate, currentlyReleased, approverId,
            AllocateVersionNo(candidate, currentlyReleased, highestVersionNo), now);
    }

    /// <summary>Step 1: the previously released version becomes SUPERSEDED.</summary>
    public void SupersedePrevious() => _currentlyReleased?.Supersede(_now);

    /// <summary>Step 2: the candidate becomes RELEASED and immutable.</summary>
    public void Seal()
    {
        if (_currentlyReleased is { State: VersionState.Released })
        {
            throw DomainException.RuleViolation("BR-02",
                "The recipe already has a released version. It must be superseded before another is released.");
        }
        _candidate.Seal(_approverId, _versionNo, _now);
    }

    /// <summary>
    /// BR-03: version numbers increase monotonically and are never reused. A
    /// draft takes the next free number when it is created, which is normally
    /// the number it is released under. But drafts can be released out of
    /// order; a draft that would be released under a number lower than the
    /// one already released takes the next free number instead, so that the
    /// released sequence only ever goes up.
    /// </summary>
    private static int AllocateVersionNo(RecipeVersion candidate, RecipeVersion? currentlyReleased,
        int highestVersionNo) =>
        currentlyReleased is not null && currentlyReleased.VersionNo > candidate.VersionNo
            ? Math.Max(highestVersionNo, currentlyReleased.VersionNo) + 1
            : candidate.VersionNo;
}
