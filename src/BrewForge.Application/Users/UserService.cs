using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Training;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Users;

public sealed record UserDto(long Id, string Username, string Email, string FullName, RoleName Role,
    long? BranchId, UserStatus Status, DateTimeOffset CreatedAt)
{
    public static UserDto From(AppUser user) =>
        new(user.Id, user.Username, user.Email, user.FullName, user.Role.RoleName, user.BranchId,
            user.Status, user.CreatedAt);
}

public sealed record CreateUserRequest(string? Username, string? Email, string? Password, string? FullName,
    RoleName? Role, long? BranchId);

/// <summary>
/// <c>Password</c> is optional and resets the password when present.
/// <c>Status</c> is optional and reactivates or deactivates the user.
/// </summary>
public sealed record UpdateUserRequest(string? Username, string? Email, string? Password, string? FullName,
    RoleName? Role, long? BranchId, UserStatus? Status);

/// <summary>UC-01: user and role management (SCR-03).</summary>
public sealed class UserService(IBrewForgeDbContext db, IPasswordHasher hasher, LearningPathService learningPath,
    ICurrentUser currentUser, TimeProvider clock)
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    private static readonly SortMap<AppUser> Sorting = new SortMap<AppUser>("username", u => u.Id)
        .Add("id", u => u.Id)
        .Add("username", u => u.Username)
        .Add("email", u => u.Email)
        .Add("fullName", u => u.FullName)
        .Add("status", u => u.Status)
        .Add("createdAt", u => u.CreatedAt);

    public async Task<PagedResult<UserDto>> ListAsync(PageQuery paging, string? keyword, string? role,
        long? branchId, string? status, CancellationToken cancellationToken)
    {
        var roleFilter = PagingExtensions.ParseFilter<RoleName>(role, "role");
        var statusFilter = PagingExtensions.ParseFilter<UserStatus>(status, "status");

        var query = db.Users.AsNoTracking().Include(u => u.Role).AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(u => u.Username.ToLower().Contains(term)
                                     || u.FullName.ToLower().Contains(term)
                                     || u.Email.ToLower().Contains(term));
        }
        if (roleFilter is not null) query = query.Where(u => u.Role.RoleName == roleFilter);
        if (branchId is not null) query = query.Where(u => u.BranchId == branchId);
        if (statusFilter is not null) query = query.Where(u => u.Status == statusFilter);

        return await query.ToPagedAsync(paging, Sorting, UserDto.From, cancellationToken);
    }

    public async Task<UserDto> GetAsync(long id, CancellationToken cancellationToken) =>
        UserDto.From(await FindAsync(id, tracking: false, cancellationToken));

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(request.Role is not null, "role", "is required")
            .Check(IsAcceptablePassword(request.Password), "password",
                $"must be between {MinPasswordLength} and {MaxPasswordLength} characters")
            .ThrowIfAny();

        var role = await RoleAsync(request.Role!.Value, cancellationToken);
        await EnsureBranchExistsAsync(request.BranchId, cancellationToken);
        await EnsureUniqueAsync(request.Username, request.Email, exceptUserId: null, cancellationToken);

        var user = AppUser.Create(request.Username!, request.Email!, hasher.Hash(request.Password!),
            request.FullName!, role, request.BranchId, clock.GetUtcNow());

        return await db.InTransactionAsync(async () =>
        {
            db.Users.Add(user);
            db.Audit(AuditEntities.User, () => user.Id, AuditActions.Create, Snapshot(user));
            await db.SaveChangesAsync(cancellationToken);

            // A new member of staff starts on what the regulation makes mandatory for their role.
            await learningPath.AssignOpenStagesAsync(user.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return UserDto.From(user);
        }, cancellationToken);
    }


    public async Task<UserDto> UpdateAsync(long id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: true, cancellationToken);

        new FieldErrors()
            .Check(request.Username is null || request.Username.Trim() == user.Username, "username",
                "cannot be changed after creation")
            .Check(request.Role is not null, "role", "is required")
            .Check(request.Password is null || IsAcceptablePassword(request.Password), "password",
                $"must be between {MinPasswordLength} and {MaxPasswordLength} characters")
            .ThrowIfAny();
        if (request.Status == UserStatus.Inactive) EnsureNotSelf(id);

        var role = await RoleAsync(request.Role!.Value, cancellationToken);
        if (request.BranchId != user.BranchId) await EnsureBranchExistsAsync(request.BranchId, cancellationToken);
        await EnsureUniqueAsync(null, request.Email, exceptUserId: id, cancellationToken);

        user.Update(request.Email!, request.FullName!, role, request.BranchId);
        if (request.Password is not null) user.ChangePasswordHash(hasher.Hash(request.Password));
        if (request.Status == UserStatus.Inactive) user.Deactivate();
        if (request.Status == UserStatus.Active) user.Reactivate();

        db.Audit(AuditEntities.User, () => user.Id, AuditActions.Update,
            new { user = Snapshot(user), passwordReset = request.Password is not null });
        await db.SaveChangesAsync(cancellationToken);
        return UserDto.From(user);
    }

    /// <summary>A user is deactivated, never deleted (BR-16).</summary>
    public async Task<UserDto> DeactivateAsync(long id, CancellationToken cancellationToken)
    {
        EnsureNotSelf(id);
        var user = await FindAsync(id, tracking: true, cancellationToken);

        user.Deactivate();
        db.Audit(AuditEntities.User, () => user.Id, AuditActions.Deactivate);
        await db.SaveChangesAsync(cancellationToken);
        return UserDto.From(user);
    }

    private static bool IsAcceptablePassword(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength };

    private void EnsureNotSelf(long id)
    {
        if (currentUser.UserId == id)
        {
            throw DomainException.RuleViolation("SELF_DEACTIVATION",
                "You cannot deactivate your own account. Ask another administrator.");
        }
    }

    private async Task<AppUser> FindAsync(long id, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Users.Include(u => u.Role).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
               ?? throw DomainException.NotFound("User", id);
    }

    private async Task<Role> RoleAsync(RoleName name, CancellationToken cancellationToken) =>
        await db.Roles.SingleOrDefaultAsync(r => r.RoleName == name, cancellationToken)
        ?? throw DomainException.Validation("The role is not configured.",
            new ErrorDetail("role", $"{name.Code()} does not exist"));

    private async Task EnsureBranchExistsAsync(long? branchId, CancellationToken cancellationToken)
    {
        if (branchId is null) return;
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw DomainException.Validation("The branch does not exist.",
                new ErrorDetail("branchId", $"branch {branchId} does not exist"));
        }
    }

    private async Task EnsureUniqueAsync(string? username, string? email, long? exceptUserId,
        CancellationToken cancellationToken)
    {
        username = username?.Trim();
        email = email?.Trim();
        var others = db.Users.IgnoreQueryFilters().Where(u => exceptUserId == null || u.Id != exceptUserId);

        var clashes = new List<ErrorDetail>();
        if (!string.IsNullOrEmpty(username) && await others.AnyAsync(u => u.Username == username, cancellationToken))
        {
            clashes.Add(new ErrorDetail("username", $"'{username}' is already taken"));
        }
        if (!string.IsNullOrEmpty(email) && await others.AnyAsync(u => u.Email == email, cancellationToken))
        {
            clashes.Add(new ErrorDetail("email", $"'{email}' is already registered"));
        }
        if (clashes.Count > 0)
        {
            throw DomainException.RuleViolation("UNIQUE", "A user with the same username or email already exists.",
                details: [.. clashes]);
        }
    }

    // Deliberately excludes the password hash: the audit log is readable by auditors.
    private static object Snapshot(AppUser user) => new
    {
        user.Username,
        user.Email,
        user.FullName,
        role = user.Role.RoleName.Code(),
        user.BranchId,
        status = user.Status.Code(),
    };
}
