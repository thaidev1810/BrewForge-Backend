namespace BrewForge.Domain.Identity;

/// <summary>The eight role groups of the <c>role_name</c> enumeration.</summary>
public enum RoleName
{
    Admin,
    RdSpecialist,
    RdManager,
    Trainer,
    Trainee,
    QualityAuditor,
    BranchManager,
    TrainingManager,
}

public static class RoleNameExtensions
{
    /// <summary>
    /// Store-level roles. They may only read and write rows of their own
    /// branch, so a user holding one must be assigned to a branch.
    /// </summary>
    public static bool IsBranchScoped(this RoleName role) =>
        role is RoleName.BranchManager or RoleName.Trainee;

    /// <summary>
    /// Whether a user holding the role may be assigned to a branch at all. A
    /// trainer may: one without a branch trains the whole chain, one with a
    /// branch trains the staff of that branch (see <see cref="IsConfinedTo"/>).
    /// The remaining roles are head-office roles and carry no branch.
    /// </summary>
    public static bool MayHaveBranch(this RoleName role) =>
        role.IsBranchScoped() || role is RoleName.Trainer;

    /// <summary>
    /// Whether a user holding the role, assigned to the given branch, sees and
    /// acts on that branch alone. Always for the store-level roles; for a
    /// trainer exactly when the trainer belongs to a branch (SRS 3.10.2: "a
    /// trainer sees only staff assigned to their own branch").
    /// </summary>
    public static bool IsConfinedTo(this RoleName role, long? branchId) =>
        role.IsBranchScoped() || (role is RoleName.Trainer && branchId is not null);
}
