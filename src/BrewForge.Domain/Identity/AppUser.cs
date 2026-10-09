using BrewForge.Domain.Common;

namespace BrewForge.Domain.Identity;

public enum UserStatus
{
    Active,
    Inactive,
}

/// <summary>
/// A system user holding exactly one role and, for store-level roles, one
/// branch.
/// </summary>
public sealed class AppUser : INeverDeleted
{
    private AppUser() { }

    public long Id { get; private set; }
    public string Username { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public long RoleId { get; private set; }
    public Role Role { get; private set; } = null!;
    public long? BranchId { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => Status == UserStatus.Active;

    string INeverDeleted.RetentionRule => "BR-16";

    public static AppUser Create(string username, string email, string passwordHash, string fullName,
        Role role, long? branchId, DateTimeOffset now)
    {
        username = username?.Trim() ?? "";
        new FieldErrors()
            .RequiredMax("username", username, 64)
            .Check(username.Length == 0 || !username.Any(char.IsWhiteSpace), "username", "must not contain spaces")
            .Required("passwordHash", passwordHash)
            .ThrowIfAny();

        var user = new AppUser { Username = username, PasswordHash = passwordHash, CreatedAt = now };
        user.SetProfile(email, fullName, role, branchId);
        return user;
    }

    public void Update(string email, string fullName, Role role, long? branchId) =>
        SetProfile(email, fullName, role, branchId);

    public void ChangePasswordHash(string passwordHash)
    {
        new FieldErrors().Required("passwordHash", passwordHash).ThrowIfAny();
        PasswordHash = passwordHash;
    }

    /// <summary>A user is never deleted, only made inactive (BR-16).</summary>
    public void Deactivate() => Status = UserStatus.Inactive;

    public void Reactivate() => Status = UserStatus.Active;

    private void SetProfile(string email, string fullName, Role role, long? branchId)
    {
        ArgumentNullException.ThrowIfNull(role);
        email = email?.Trim() ?? "";
        fullName = fullName?.Trim() ?? "";

        new FieldErrors()
            .RequiredMax("email", email, 160)
            .Check(email.Length == 0 || IsPlausibleEmail(email), "email", "is not a valid email address")
            .RequiredMax("fullName", fullName, 120)
            .Check(!role.RoleName.IsBranchScoped() || branchId is not null, "branchId",
                $"is required for the {role.RoleName.Code()} role")
            .Check(role.RoleName.MayHaveBranch() || branchId is null, "branchId",
                $"must be null for the head-office role {role.RoleName.Code()}")
            .ThrowIfAny();

        Email = email;
        FullName = fullName;
        Role = role;
        RoleId = role.Id;
        BranchId = branchId;
    }

    private static bool IsPlausibleEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 3
               && email.IndexOf('.', at) > at + 1 && !email.Any(char.IsWhiteSpace);
    }
}
