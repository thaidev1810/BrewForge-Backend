using BrewForge.Domain.Common;
using BrewForge.Domain.Recipes;
using static BrewForge.Domain.Tests.Recipes.RecipeFixtures;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>
/// Feedback on a recipe from the counter: who may give it, what it has to
/// say, and what R&amp;D can make of it.
/// </summary>
public sealed class RecipeFeedbackTests
{
    private const long Barista = 61;
    private const long Reviewer = 4;
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

    // ---------------------------------------------------------------- review

    [Fact]
    public void Research_accepts_a_remark_which_then_waits_for_a_version_that_takes_it_in()
    {
        var feedback = StaffNote();

        feedback.Accept(Reviewer, "  Good catch.  ", Now.AddHours(2));

        Assert.Equal((FeedbackState.Accepted, (long?)Reviewer, (DateTimeOffset?)Now.AddHours(2), "Good catch."),
            (feedback.State, feedback.ReviewedBy, feedback.ReviewedAt, feedback.ReviewNote));
        Assert.True(feedback.IsOpen);
    }

    [Fact]
    public void Rejection_says_why()
    {
        var feedback = StaffNote();

        Assert.Contains(Refused(() => feedback.Reject(Reviewer, " ", Now)).Details, d => d.Field == "note");
        Assert.Equal(FeedbackState.New, feedback.State);

        feedback.Reject(Reviewer, "The recipe already says this in step 2.", Now);

        Assert.Equal((FeedbackState.Rejected, "The recipe already says this in step 2."), (feedback.State, feedback.ReviewNote));
        Assert.False(feedback.IsOpen);
    }

    [Fact]
    public void Remark_is_reviewed_once()
    {
        var accepted = StaffNote();
        accepted.Accept(Reviewer, null, Now);
        var rejected = StaffNote();
        rejected.Reject(Reviewer, "No.", Now);

        Assert.Equal(RecipeFeedback.StateRule, Refused(() => accepted.Reject(Reviewer, "Changed my mind.", Now)).Rule);
        Assert.Equal(RecipeFeedback.StateRule, Refused(() => accepted.Accept(Reviewer, null, Now)).Rule);
        Assert.Equal(RecipeFeedback.StateRule, Refused(() => rejected.Accept(Reviewer, null, Now)).Rule);
        Assert.Equal((FeedbackState.Accepted, FeedbackState.Rejected), (accepted.State, rejected.State));
    }

    // ---------------------------------------------------------------- addressed

    [Fact]
    public void Accepted_remark_is_addressed_by_a_later_version_of_the_same_drink()
    {
        var about = Version();
        var feedback = StaffNote(about);
        feedback.Accept(Reviewer, null, Now);

        feedback.MarkAddressed(about, Version(versionId: 320, versionNo: 2));

        Assert.Equal((FeedbackState.Addressed, (long?)320), (feedback.State, feedback.AddressedInVersionId));
        Assert.False(feedback.IsOpen);
        // Who reviewed it stays on record.
        Assert.Equal(Reviewer, feedback.ReviewedBy);
    }

    [Fact]
    public void Remark_is_not_addressed_by_its_own_version_an_earlier_one_or_another_drink()
    {
        var about = Version(versionId: 320, versionNo: 2);
        var feedback = StaffNote(about);
        feedback.Accept(Reviewer, null, Now);

        Assert.All(new[] { about, Version(versionId: 310, versionNo: 1), Version(versionId: 990, recipeId: 99, versionNo: 5) },
            other => Assert.Equal(RecipeFeedback.AddressedRule, Refused(() => feedback.MarkAddressed(about, other)).Rule));
        Assert.Equal(FeedbackState.Accepted, feedback.State);
    }

    [Fact]
    public void Only_an_accepted_remark_is_addressed()
    {
        var about = Version();
        var later = Version(versionId: 320, versionNo: 2);
        var fresh = StaffNote(about);
        var rejected = StaffNote(about);
        rejected.Reject(Reviewer, "No.", Now);
        var addressed = StaffNote(about);
        addressed.Accept(Reviewer, null, Now);
        addressed.MarkAddressed(about, later);

        Assert.All(new[] { fresh, rejected, addressed },
            feedback => Assert.Equal(RecipeFeedback.StateRule, Refused(() => feedback.MarkAddressed(about, later)).Rule));
    }

    // ---------------------------------------------------------------- field notes

    [Fact]
    public void Accepted_staff_remark_is_a_field_note_from_its_version_onwards()
    {
        var note = StaffNote(Version(versionId: 320, versionNo: 2));
        // Nothing R&D has not agreed with is put in front of a learner.
        Assert.False(note.IsFieldNoteFor(aboutVersionNo: 2, addressedInVersionNo: null, readVersionNo: 2));

        note.Accept(Reviewer, null, Now);

        Assert.True(note.IsFieldNoteFor(2, null, readVersionNo: 2));
        // Still worth knowing with a later version that did not take it in.
        Assert.True(note.IsFieldNoteFor(2, null, readVersionNo: 3));
        // It says nothing about a version made before anybody had made this one.
        Assert.False(note.IsFieldNoteFor(2, null, readVersionNo: 1));
    }

    [Fact]
    public void Addressed_remark_is_a_field_note_until_the_version_that_took_it_in()
    {
        var about = Version();
        var note = StaffNote(about);
        note.Accept(Reviewer, null, Now);
        note.MarkAddressed(about, Version(versionId: 330, versionNo: 3));

        Assert.True(note.IsFieldNoteFor(1, addressedInVersionNo: 3, readVersionNo: 1));
        Assert.True(note.IsFieldNoteFor(1, addressedInVersionNo: 3, readVersionNo: 2));
        // From version 3 on the recipe says it itself.
        Assert.False(note.IsFieldNoteFor(1, addressedInVersionNo: 3, readVersionNo: 3));
        Assert.False(note.IsFieldNoteFor(1, addressedInVersionNo: 3, readVersionNo: 4));
    }

    [Fact]
    public void Rejected_remark_and_what_a_customer_said_are_never_field_notes()
    {
        var rejected = StaffNote();
        rejected.Reject(Reviewer, "No.", Now);
        var customers = RecipeFeedback.FromCustomer(Version(), null, Barista, Branch, FeedbackCategory.Taste, "Too sweet.", 2, Now);
        customers.Accept(Reviewer, null, Now);

        Assert.False(rejected.IsFieldNoteFor(1, null, 1));
        // Accepted, and for R&D to act on: not something to teach.
        Assert.False(customers.IsFieldNoteFor(1, null, 1));
    }
}
