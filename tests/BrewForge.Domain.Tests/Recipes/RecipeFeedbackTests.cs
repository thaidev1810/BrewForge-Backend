using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>
/// Feedback on a recipe from the counter: who may give it, and what it has to say.
/// </summary>
public sealed class RecipeFeedbackTests
{
    private const long Barista = 61;
    private const long Branch = 1;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static DomainException Refused(Action action) => Assert.Throws<DomainException>(action);

    /// <summary>Version 1 of drink 12, released, its steps numbered 1001, 1002 and 1003.</summary>
    private static RecipeVersion Version(long versionId = 310, long recipeId = 12, int versionNo = 1)
    {
        var version = ReleasedVersion(versionId, recipeId, versionNo);
        foreach (var step in version.Steps) WithId(step, versionId * 10 + step.StepOrder - 2100);
        return version;
    }

    private static RecipeFeedback StaffNote(RecipeVersion? version = null, string content = "Stir before the milk goes in, or it splits.") =>
        RecipeFeedback.FromStaff(version ?? Version(), null, Barista, Branch, trained: true, FeedbackCategory.PreparationNote,
            content, Now);

    // ---------------------------------------------------------------- staff

    [Fact]
    public void Staff_certified_on_a_drink_remark_on_a_step_of_the_version_they_learned()
    {
        var version = Version();
        var step = version.OrderedSteps()[1];

        var feedback = RecipeFeedback.FromStaff(version, step.Id, Barista, Branch, trained: true,
            FeedbackCategory.CommonMistake, "  Milk poured too early curdles on the hot tea.  ", Now);

        Assert.Equal((12, 310, (long?)step.Id), (feedback.RecipeId, feedback.RecipeVersionId, feedback.RecipeStepId));
        Assert.Equal((FeedbackSource.Staff, FeedbackCategory.CommonMistake, FeedbackState.New),
            (feedback.Source, feedback.Category, feedback.State));
        Assert.Equal("Milk poured too early curdles on the hot tea.", feedback.Content);
        Assert.Equal((Barista, (long?)Branch, Now), (feedback.SubmittedBy, feedback.BranchId, feedback.CreatedAt));
        // Staff remark; they do not rate.
        Assert.Null(feedback.Rating);
        Assert.Null(feedback.ReviewedBy);
    }

    [Fact]
    public void Staff_who_were_never_certified_on_the_drink_are_refused()
    {
        var refusal = Refused(() => RecipeFeedback.FromStaff(Version(), null, Barista, Branch, trained: false,
            FeedbackCategory.PreparationNote, "I think it needs more sugar.", Now));

        Assert.Equal(RecipeFeedback.TrainedRule, refusal.Rule);
        Assert.Equal(ErrorKind.RuleViolation, refusal.Kind);
    }

    [Fact]
    public void Feedback_needs_something_to_say_about_a_version_that_was_released()
    {
        var version = Version();

        Assert.Contains(Refused(() => StaffNote(content: "   ")).Details, d => d.Field == "content");
        Assert.Contains(Refused(() => StaffNote(content: new string('x', RecipeFeedback.MaxContentLength + 1))).Details,
            d => d.Field == "content");
        // A step of some other version is not a step of this one.
        Assert.Contains(Refused(() => RecipeFeedback.FromStaff(version, 424242, Barista, Branch, true,
            FeedbackCategory.PreparationNote, "About a step that is not here.", Now)).Details, d => d.Field == "recipeStepId");
        // A draft is nobody's to make yet.
        Assert.Contains(Refused(() => StaffNote(Draft(Step(1)))).Details, d => d.Field == "recipeVersionId");
    }

    // ---------------------------------------------------------------- customers

    [Fact]
    public void Customer_feedback_carries_a_rating_and_the_branch_that_heard_it()
    {
        var feedback = RecipeFeedback.FromCustomer(Version(), null, Barista, Branch, FeedbackCategory.Taste,
            "Too sweet for me.", 2, Now);

        Assert.Equal((FeedbackSource.Customer, 2, (long?)Branch, Barista),
            (feedback.Source, feedback.Rating!.Value, feedback.BranchId, feedback.SubmittedBy));

        Assert.Contains(Refused(() => RecipeFeedback.FromCustomer(Version(), null, Barista, Branch, FeedbackCategory.Taste,
            "Lovely.", null, Now)).Details, d => d.Field == "rating");
        Assert.All(new[] { 0, 6 }, bad => Assert.Contains(Refused(() => RecipeFeedback.FromCustomer(Version(), null,
            Barista, Branch, FeedbackCategory.Taste, "Lovely.", bad, Now)).Details, d => d.Field == "rating"));
    }
}
