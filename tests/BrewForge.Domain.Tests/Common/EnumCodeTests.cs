using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;

namespace BrewForge.Domain.Tests.Common;

/// <summary>
/// The enumerations of the data dictionary, section 3. "A value not in this
/// list must not appear in the database" - so the codes are pinned here,
/// letter for letter.
/// </summary>
public sealed class EnumCodeTests
{
    [Fact]
    public void Role_name_codes_match_the_data_dictionary() =>
        AssertCodes<RoleName>("ADMIN", "RD_SPECIALIST", "RD_MANAGER", "TRAINER", "TRAINEE", "QUALITY_AUDITOR",
            "BRANCH_MANAGER", "TRAINING_MANAGER");

    [Fact]
    public void User_status_codes_match_the_data_dictionary() => AssertCodes<UserStatus>("ACTIVE", "INACTIVE");

    [Fact]
    public void Branch_status_codes_match_the_data_dictionary() => AssertCodes<BranchStatus>("ACTIVE", "CLOSED");

    [Fact]
    public void Ingredient_unit_codes_match_the_data_dictionary() => AssertCodes<IngredientUnit>("g", "ml", "pcs");

    [Fact]
    public void Dosing_unit_codes_match_the_data_dictionary() =>
        AssertCodes<DosingUnit>("g", "ml", "sec", "degC", "bar");

    [Fact]
    public void Code_round_trips_through_parse()
    {
        Assert.Equal("RD_SPECIALIST", RoleName.RdSpecialist.Code());
        Assert.Equal(RoleName.RdSpecialist, EnumCode<RoleName>.Parse("RD_SPECIALIST"));
        Assert.Equal(DosingUnit.DegreeCelsius, EnumCode<DosingUnit>.Parse("degC"));
    }

    [Theory]
    [InlineData("rd_specialist")] // codes are case-sensitive, as the CHECK constraints are
    [InlineData("RdSpecialist")]  // the C# member name is not a code
    [InlineData("SUPERUSER")]
    [InlineData("")]
    [InlineData(null)]
    public void Value_outside_the_enumeration_is_rejected(string? code)
    {
        Assert.False(EnumCode<RoleName>.TryParse(code, out _));
        if (code is not null) Assert.Throws<FormatException>(() => EnumCode<RoleName>.Parse(code));
    }

    internal static void AssertCodes<T>(params string[] expected) where T : struct, Enum =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), EnumCode<T>.AllCodes.Order(StringComparer.Ordinal));
}
