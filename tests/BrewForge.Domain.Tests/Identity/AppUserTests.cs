using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;

namespace BrewForge.Domain.Tests.Identity;

public sealed class AppUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static Role RoleOf(RoleName name) => new(name, Permissions.DefaultsFor(name));

    private static AppUser Create(RoleName role, long? branchId, string email = "someone@brewforge.local") =>
        AppUser.Create("someone", email, "$argon2id$hash", "Some One", RoleOf(role), branchId, Now);

    [Theory]
    [InlineData(RoleName.BranchManager)]
    [InlineData(RoleName.Trainee)]
    public void Store_level_role_requires_a_branch(RoleName role)
    {
        var refusal = Assert.Throws<DomainException>(() => Create(role, branchId: null));

        Assert.Equal(ErrorKind.Validation, refusal.Kind);
        Assert.Contains(refusal.Details, d => d.Field == "branchId");
        Assert.NotNull(Create(role, branchId: 7));
    }

    [Theory]
    [InlineData(RoleName.Admin)]
    [InlineData(RoleName.RdSpecialist)]
    [InlineData(RoleName.RdManager)]
    [InlineData(RoleName.QualityAuditor)]
    [InlineData(RoleName.TrainingManager)]
    public void Head_office_role_may_not_carry_a_branch(RoleName role)
    {
        var refusal = Assert.Throws<DomainException>(() => Create(role, branchId: 7));

        Assert.Contains(refusal.Details, d => d.Field == "branchId");
        Assert.Null(Create(role, branchId: null).BranchId);
    }

    [Fact]
    public void Trainer_may_be_attached_to_a_branch_or_not()
    {
        Assert.Equal(7, Create(RoleName.Trainer, 7).BranchId);
        Assert.Null(Create(RoleName.Trainer, null).BranchId);
    }

    [Fact]
    public void Only_branch_manager_and_trainee_are_confined_to_a_branch()
    {
        var confined = Enum.GetValues<RoleName>().Where(role => role.IsBranchScoped());

        Assert.Equal([RoleName.Trainee, RoleName.BranchManager], confined);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("two@@brewforge.local")]
    [InlineData("spaces in@brewforge.local")]
    [InlineData("nodomain@x")]
    public void Invalid_email_is_rejected(string email)
    {
        var refusal = Assert.Throws<DomainException>(() => Create(RoleName.Trainer, null, email));

        Assert.Contains(refusal.Details, d => d.Field == "email");
    }

    [Fact]
    public void All_invalid_fields_are_reported_together()
    {
        var refusal = Assert.Throws<DomainException>(() =>
            AppUser.Create("someone", "bad", "$argon2id$hash", "", RoleOf(RoleName.Trainee), null, Now));

        Assert.Equal(["email", "fullName", "branchId"], refusal.Details.Select(d => d.Field));
    }

    [Fact]
    public void Value_longer_than_its_column_is_reported_with_MSG_E11()
    {
        var refusal = Assert.Throws<DomainException>(() =>
            AppUser.Create(new string('u', 65), "someone@brewforge.local", "$argon2id$hash", "Some One",
                RoleOf(RoleName.Trainer), null, Now));

        Assert.Equal("MSG-E11", refusal.Code);
        Assert.Equal("Exceed max length of 64.", Assert.Single(refusal.Details).Issue);
    }

    [Fact]
    public void User_is_deactivated_not_removed()
    {
        var user = Create(RoleName.Trainer, null);

        user.Deactivate();

        Assert.Equal(UserStatus.Inactive, user.Status);
        Assert.False(user.IsActive);
        Assert.Equal("someone", user.Username); // the record is intact
        user.Reactivate();
        Assert.True(user.IsActive);
    }

    [Fact]
    public void Moving_a_user_to_a_store_level_role_without_a_branch_is_rejected_and_changes_nothing()
    {
        var user = Create(RoleName.Trainer, null);

        Assert.Throws<DomainException>(() =>
            user.Update("someone@brewforge.local", "Some One", RoleOf(RoleName.Trainee), branchId: null));

        Assert.Equal(RoleName.Trainer, user.Role.RoleName);
    }
}
