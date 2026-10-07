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
    /// trainer may be attached to a branch without being restricted to it; the
    /// remaining roles are head-office roles and carry no branch.
    /// </summary>
    public static bool MayHaveBranch(this RoleName role) =>
        role.IsBranchScoped() || role is RoleName.Trainer;
}
