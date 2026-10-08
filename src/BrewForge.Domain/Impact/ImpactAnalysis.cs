using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Training;

namespace BrewForge.Domain.Impact;

/// <summary>The <c>impact_type</c> enumeration.</summary>
public enum ImpactType
{
    /// <summary>A published course built on an affected version.</summary>
    OutOfDate,

    /// <summary>A valid certificate bound to an affected version.</summary>
    NeedsRecert,

    /// <summary>A branch where an affected version is on sale.</summary>
    BranchAffected,
}

/// <summary>What a change starts from: the entities the reverse dependency graph is entered at.</summary>
public enum TriggerKind
{
    Ingredient,
    StandardEquipment,
    RecipeVersion,
}

/// <param name="EquipmentClass">For an equipment trigger: the class steps refer to it by.</param>
public sealed record ImpactTrigger(TriggerKind Kind, long EntityId, string? EquipmentClass = null)
{
    /// <summary>The name written to <c>trigger_entity</c>; the same names the audit log uses.</summary>
    public string EntityType => Kind.ToString();

    public static bool TryParseKind(string? entityType, out TriggerKind kind) =>
        Enum.TryParse(entityType, ignoreCase: false, out kind) && Enum.IsDefined(kind);
}

/// <summary>One computed row of an impact analysis, linking a trigger to an affected entity.</summary>
public sealed class ChangeImpact
{
    public const string CourseEntity = nameof(Course);
    public const string CertificateEntity = nameof(Certificate);
    public const string LaunchEntity = nameof(BranchLaunchStatus);

    private ChangeImpact() { }

    internal ChangeImpact(Guid runId, ImpactTrigger trigger, string affectedEntity, long affectedId, ImpactType type,
        DateTimeOffset now)
    {
        AnalysisRunId = runId;
        TriggerEntity = trigger.EntityType;
        TriggerId = trigger.EntityId;
        AffectedEntity = affectedEntity;
        AffectedId = affectedId;
        ImpactType = type;
        CreatedAt = now;
    }

    public long Id { get; private set; }
    public Guid AnalysisRunId { get; private set; }
    public string TriggerEntity { get; private set; } = null!;
    public long TriggerId { get; private set; }
    public string AffectedEntity { get; private set; } = null!;
    public long AffectedId { get; private set; }
    public ImpactType ImpactType { get; private set; }

    /// <summary>False while the analysis is what-if.</summary>
    public bool Committed { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal void MarkCommitted() => Committed = true;
}

/// <summary>What a change reaches: the versions that depend on the trigger, and what depends on them.</summary>
public sealed record ImpactSet(IReadOnlyList<long> RecipeVersionIds, IReadOnlyList<Course> Courses,
    IReadOnlyList<Certificate> Certificates, IReadOnlyList<BranchLaunchStatus> LiveBranches)
{
    public bool IsEmpty => Courses.Count == 0 && Certificates.Count == 0 && LiveBranches.Count == 0;
}

/// <summary>
/// The reverse dependency graph (UC-19):
/// <code>
/// Ingredient / StandardEquipment
///         -> RecipeVersion -> Course
///                          -> Certificate
///                          -> BranchLaunchStatus
/// </code>
/// It answers one question completely: given a change, what has to be
/// retrained, rebuilt or looked at. An incomplete answer would leave staff
/// certified on a procedure that no longer exists.
/// </summary>
public static class DependencyGraph
{
    /// <summary>
    /// The first hop. Only versions that have been in production count: a
    /// draft is re-validated against the master data of the moment when it
    /// is released, and nothing was built on it.
    /// </summary>
    public static IReadOnlyList<RecipeVersion> VersionsDependingOn(ImpactTrigger trigger, IEnumerable<RecipeVersion> versions) =>
    [
        .. versions.Where(version => version.State is VersionState.Released or VersionState.Superseded)
            .Where(version => trigger.Kind switch
            {
                TriggerKind.RecipeVersion => version.Id == trigger.EntityId,
                TriggerKind.Ingredient => version.Steps.Any(step =>
                    step.Ingredients.Any(use => use.IngredientId == trigger.EntityId)),
                TriggerKind.StandardEquipment => trigger.EquipmentClass is not null && version.Steps.Any(step =>
                    string.Equals(step.EquipmentClass, trigger.EquipmentClass, StringComparison.Ordinal)),
                _ => false,
            })
            .OrderBy(version => version.Id),
    ];

    /// <summary>
    /// The second hop: of everything bound to those versions, what the
    /// change actually touches. A course is affected while it is PUBLISHED,
    /// a certificate while it is VALID, a branch while the version is LIVE
    /// there; what was already flagged, archived or withdrawn is history.
    /// </summary>
    public static ImpactSet Downstream(IReadOnlyCollection<long> versionIds, IEnumerable<Course> courses,
        IEnumerable<Certificate> certificates, IEnumerable<BranchLaunchStatus> launches)
    {
        bool Bound(long? versionId) => versionId is { } id && versionIds.Contains(id);
        return new ImpactSet([.. versionIds.Order()],
            [.. courses.Where(course => Bound(course.RecipeVersionId) && course.State == CourseState.Published).OrderBy(c => c.Id)],
            [.. certificates.Where(certificate => Bound(certificate.RecipeVersionId) && certificate.IsValid).OrderBy(c => c.Id)],
            [.. launches.Where(launch => Bound(launch.RecipeVersionId) && launch.IsLive).OrderBy(l => l.Id)]);
    }
}

/// <summary>
/// One run of the impact analysis. Computing it changes nothing but its own
/// rows; committing it applies the flags, and deletes nothing (BR-15): the
/// course is still there, OUT_OF_DATE, and the certificate is still there,
/// NEEDS_RECERT, because what a member of staff was taught at a given time
/// is a record the audit needs.
/// </summary>
public sealed class ImpactRun
{
    private readonly List<ChangeImpact> _rows;

    private ImpactRun(Guid runId, ImpactTrigger trigger, ImpactSet affected, List<ChangeImpact> rows)
    {
        RunId = runId;
        Trigger = trigger;
        Affected = affected;
        _rows = rows;
    }

    public Guid RunId { get; }
    public ImpactTrigger Trigger { get; }
    public ImpactSet Affected { get; }

    /// <summary>The rows this run adds to <c>change_impact</c>.</summary>
    public IReadOnlyList<ChangeImpact> NewRows => _rows;

    /// <summary>The what-if: one row per affected entity, none of them committed.</summary>
    public static ImpactRun WhatIf(Guid runId, ImpactTrigger trigger, ImpactSet affected, DateTimeOffset now) =>
        Resume(runId, trigger, affected, [], now);

    /// <summary>
    /// A run taken up again to be committed. The affected set is computed
    /// afresh, and whatever has become affected since the what-if gets a row
    /// of its own, so that the commit is complete as of now.
    /// </summary>
    public static ImpactRun Resume(Guid runId, ImpactTrigger trigger, ImpactSet affected,
        IReadOnlyCollection<ChangeImpact> existingRows, DateTimeOffset now)
    {
        var known = existingRows.Select(row => (row.AffectedEntity, row.AffectedId)).ToHashSet();
        var rows = new List<ChangeImpact>();
        void Add(string entity, long id, ImpactType type)
        {
            if (known.Add((entity, id))) rows.Add(new ChangeImpact(runId, trigger, entity, id, type, now));
        }

        foreach (var course in affected.Courses) Add(ChangeImpact.CourseEntity, course.Id, ImpactType.OutOfDate);
        foreach (var certificate in affected.Certificates) Add(ChangeImpact.CertificateEntity, certificate.Id, ImpactType.NeedsRecert);
        foreach (var launch in affected.LiveBranches) Add(ChangeImpact.LaunchEntity, launch.Id, ImpactType.BranchAffected);
        return new ImpactRun(runId, trigger, affected, rows);
    }

    /// <summary>
    /// Applies the flags: every affected course becomes OUT_OF_DATE and every
    /// affected certificate NEEDS_RECERT. Branches are reported, not changed:
    /// what a branch sells is decided at the launch gate. A run is committed
    /// once.
    /// </summary>
    public void Commit(IReadOnlyCollection<ChangeImpact> existingRows)
    {
        if (existingRows.Count > 0 && existingRows.All(row => row.Committed))
        {
            throw DomainException.RuleViolation("IMPACT_ALREADY_COMMITTED", "This impact analysis has already been committed.");
        }

        foreach (var course in Affected.Courses) course.MarkOutOfDate();
        foreach (var certificate in Affected.Certificates) certificate.FlagForRecertification();
        foreach (var row in existingRows.Concat(_rows)) row.MarkCommitted();
    }
}
