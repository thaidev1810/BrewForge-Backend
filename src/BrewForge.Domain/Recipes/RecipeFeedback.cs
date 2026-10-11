using BrewForge.Domain.Common;

namespace BrewForge.Domain.Recipes;

/// <summary>Whose remark it is.</summary>
public enum FeedbackSource
{
    /// <summary>A member of staff who was trained on the drink and makes it.</summary>
    Staff,

    /// <summary>A customer, as recorded at the branch that served them.</summary>
    Customer,
}

/// <summary>What the remark is about.</summary>
public enum FeedbackCategory
{
    /// <summary>Something to watch for while making the drink, which the recipe does not say.</summary>
    PreparationNote,

    /// <summary>A way the drink goes wrong in practice.</summary>
    CommonMistake,

    /// <summary>How the drink tastes, looks or is served.</summary>
    Taste,
    Other,
}

/// <summary>The <c>state</c> of a remark: what R&amp;D made of it.</summary>
public enum FeedbackState
{
    New,
    Accepted,
    Rejected,

    /// <summary>Accepted, and taken into a later version of the recipe.</summary>
    Addressed,
}

/// <summary>
/// One remark about a released version of a drink, from somebody who makes
/// it or somebody who drank it. A recipe is written at head office and
/// learned at the counter; this is the way back. The remark never changes
/// the recipe: it is there for R&amp;D to read.
/// </summary>
public sealed class RecipeFeedback
{
    /// <summary>Named when staff feedback comes from somebody who was never certified on the drink.</summary>
    public const string TrainedRule = "FEEDBACK_REQUIRES_TRAINING";

    /// <summary>Named when a customer's remark is recorded at a branch that does not sell the drink.</summary>
    public const string OnSaleRule = "FEEDBACK_NOT_ON_SALE";

    public const int MaxContentLength = 1000;

    private RecipeFeedback() { }

    public long Id { get; private set; }
    public long RecipeId { get; private set; }

    /// <summary>The version the remark is about: the one its author was trained on, or the one the branch sells.</summary>
    public long RecipeVersionId { get; private set; }

    /// <summary>The step of that version the remark is about, if it is about one.</summary>
    public long? RecipeStepId { get; private set; }

    /// <summary>The branch the remark comes from. Always set for a customer's.</summary>
    public long? BranchId { get; private set; }
    public FeedbackSource Source { get; private set; }
    public FeedbackCategory Category { get; private set; }
    public string Content { get; private set; } = null!;

    /// <summary>One to five, given by a customer. Staff do not rate.</summary>
    public int? Rating { get; private set; }

    /// <summary>The member of staff who wrote the remark, or who wrote down what the customer said.</summary>
    public long SubmittedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public FeedbackState State { get; private set; } = FeedbackState.New;
    public long? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReviewNote { get; private set; }

    /// <summary>The later version of the drink that took the remark in.</summary>
    public long? AddressedInVersionId { get; private set; }

    /// <summary>Accepted and still waiting for a version that takes it in.</summary>
    public bool IsOpen => State == FeedbackState.Accepted;

    /// <summary>
    /// A remark from somebody who makes the drink. Only somebody who was
    /// certified on it may make one: the remark is worth reading because its
    /// author learned the recipe as written and then made it for real.
    /// </summary>
    /// <param name="trained">Whether the author holds, or held, a certificate on a version of the drink.</param>
    public static RecipeFeedback FromStaff(RecipeVersion version, long? recipeStepId, long userId, long? branchId,
        bool trained, FeedbackCategory category, string? content, DateTimeOffset now)
    {
        if (!trained) throw NotTrained();
        return Create(version, recipeStepId, userId, branchId, FeedbackSource.Staff, category, content, rating: null, now);
    }

    /// <summary>The refusal of staff feedback from somebody who was never certified on the drink.</summary>
    public static DomainException NotTrained() =>
        DomainException.RuleViolation(TrainedRule,
            "Feedback on how a drink is made comes from staff who were certified on it.");

    /// <summary>
    /// What a customer said about the drink, written down by the member of
    /// staff who heard it, at a branch that sells the drink. It carries the
    /// customer's rating, one to five.
    /// </summary>
    /// <param name="version">The version on sale at that branch.</param>
    public static RecipeFeedback FromCustomer(RecipeVersion version, long? recipeStepId, long userId, long branchId,
        FeedbackCategory category, string? content, int? rating, DateTimeOffset now)
    {
        new FieldErrors()
            .Check(rating is not null, "rating", "is required for a customer's feedback")
            .ThrowIfAny();
        return Create(version, recipeStepId, userId, branchId, FeedbackSource.Customer, category, content, rating, now);
    }

    private static RecipeFeedback Create(RecipeVersion version, long? recipeStepId, long userId, long? branchId,
        FeedbackSource source, FeedbackCategory category, string? content, int? rating, DateTimeOffset now)
    {
        content = content?.Trim();
        new FieldErrors()
            .RequiredMax("content", content, MaxContentLength)
            .Check(Enum.IsDefined(category), "category", "is not a valid category")
            .Check(rating is null or (>= 1 and <= 5), "rating", "must be between 1 and 5")
            .Check(recipeStepId is null || version.Steps.Any(step => step.Id == recipeStepId), "recipeStepId",
                "is not a step of the version the feedback is about")
            // A draft is nobody's to make yet: there is nothing to report from the counter.
            .Check(version.State is VersionState.Released or VersionState.Superseded, "recipeVersionId",
                "must be a version that was released")
            .ThrowIfAny();

        return new RecipeFeedback
        {
            RecipeId = version.RecipeId,
            RecipeVersionId = version.Id,
            RecipeStepId = recipeStepId,
            BranchId = branchId,
            Source = source,
            Category = category,
            Content = content!,
            Rating = rating,
            SubmittedBy = userId,
            CreatedAt = now,
        };
    }
}
