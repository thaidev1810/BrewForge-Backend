using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Notifications;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Recipes;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Recipes;

public sealed record RecipeFeedbackDto(long Id, long RecipeId, string? RecipeCode, string? RecipeName,
    long RecipeVersionId, int? VersionNo, long? RecipeStepId, int? StepOrder, long? BranchId, string? BranchCode,
    FeedbackSource Source, FeedbackCategory Category, string Content, int? Rating, long SubmittedBy,
    string? SubmittedByName, DateTimeOffset CreatedAt, FeedbackState State, long? ReviewedBy,
    DateTimeOffset? ReviewedAt, string? ReviewNote, long? AddressedInVersionId, int? AddressedInVersionNo);

/// <summary>
/// <c>RecipeVersionId</c> is for staff feedback only, and optional: left out,
/// it is the latest version the author is certified on. A customer's
/// feedback is about the version the branch sells, which the server knows.
/// </summary>
public sealed record RecipeFeedbackRequest(FeedbackSource? Source, FeedbackCategory? Category, string? Content,
    int? Rating, long? RecipeVersionId, long? RecipeStepId);

/// <summary>
/// Feedback on a recipe from the people who make the drink and the people
/// who drink it.
/// </summary>
public sealed class RecipeFeedbackService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    // ---------------------------------------------------------------- submitting

    /// <summary>
    /// A member of staff remarks on a drink they were certified on, or writes
    /// down what a customer said about a drink their branch sells.
    /// </summary>
    public async Task<RecipeFeedbackDto> SubmitAsync(long recipeId, RecipeFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        new FieldErrors()
            .Check(request.Source is not null, "source", "is required")
            .Check(request.Category is not null, "category", "is required")
            .ThrowIfAny();
        var recipe = await db.Recipes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken)
                     ?? throw DomainException.NotFound("Recipe", recipeId);
        var now = clock.GetUtcNow();

        var feedback = request.Source == FeedbackSource.Customer
            ? await FromCustomerAsync(recipeId, request, userId, now, cancellationToken)
            : await FromStaffAsync(recipeId, request, userId, now, cancellationToken);

        db.RecipeFeedback.Add(feedback);
        db.Audit(AuditEntities.RecipeFeedback, () => feedback.Id, AuditActions.Create, new
        {
            feedback.RecipeId, feedback.RecipeVersionId, feedback.RecipeStepId, feedback.BranchId, feedback.Source,
            feedback.Category, feedback.Rating,
        });
        // The author of the recipe hears of it; the rest of R&D finds it in the list.
        if (recipe.CreatedBy != userId)
        {
            db.Notify(recipe.CreatedBy, "New feedback on a recipe",
                $"'{recipe.Name}' received feedback from {(feedback.Source == FeedbackSource.Customer ? "a customer" : "staff")}.",
                now, new { recipeId });
        }
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([feedback], cancellationToken))[0];
    }

    private async Task<RecipeFeedback> FromStaffAsync(long recipeId, RecipeFeedbackRequest request, long userId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.Rating is null, "rating", "is given by a customer, not by staff").ThrowIfAny();

        // Trained on the drink: certified on a version of it, whatever became of the certificate since.
        var certifiedOn = await db.Certificates.AsNoTracking()
            .Where(c => c.UserId == userId && c.RecipeVersionId != null)
            .Join(db.RecipeVersions.Where(v => v.RecipeId == recipeId), c => c.RecipeVersionId, v => (long?)v.Id,
                (c, v) => new { v.Id, v.VersionNo })
            .OrderByDescending(v => v.VersionNo)
            .ToListAsync(cancellationToken);
        if (certifiedOn.Count == 0) throw RecipeFeedback.NotTrained();

        var version = await VersionOfAsync(recipeId, request.RecipeVersionId ?? certifiedOn[0].Id, cancellationToken);
        return RecipeFeedback.FromStaff(version, request.RecipeStepId, userId, currentUser.BranchId, trained: true,
            request.Category!.Value, request.Content, now);
    }

    private async Task<RecipeFeedback> FromCustomerAsync(long recipeId, RecipeFeedbackRequest request, long userId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var branchId = currentUser.BranchId
                       ?? throw DomainException.Validation("A customer's feedback is recorded at a branch.",
                           new ErrorDetail("source", "CUSTOMER feedback is recorded by the staff of a branch"));
        new FieldErrors()
            .Check(request.RecipeVersionId is null, "recipeVersionId",
                "is not given for a customer's feedback: it is the version the branch sells")
            .ThrowIfAny();

        var onSale = await db.BranchLaunchStatuses.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.BranchId == branchId && l.RecipeId == recipeId && l.Status == LaunchStatus.Live
                        && l.RecipeVersionId != null)
            .Select(l => l.RecipeVersionId).FirstOrDefaultAsync(cancellationToken);
        if (onSale is not { } versionId)
        {
            throw DomainException.RuleViolation(RecipeFeedback.OnSaleRule,
                "A customer's feedback is recorded at a branch where the drink is on sale.");
        }

        var version = await VersionOfAsync(recipeId, versionId, cancellationToken);
        return RecipeFeedback.FromCustomer(version, request.RecipeStepId, userId, branchId, request.Category!.Value,
            request.Content, request.Rating, now);
    }

    private async Task<RecipeVersion> VersionOfAsync(long recipeId, long versionId, CancellationToken cancellationToken) =>
        await db.RecipeVersions.AsNoTracking().Include(v => v.Steps)
            .SingleOrDefaultAsync(v => v.Id == versionId && v.RecipeId == recipeId, cancellationToken)
        ?? throw DomainException.Validation("The version is not a version of this recipe.",
            new ErrorDetail("recipeVersionId", "is not a version of this recipe"));

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// What the caller may see. Head office sees everything. A branch manager
    /// sees what came from their branch. A trainee sees what they wrote, and
    /// the staff remarks R&amp;D accepted: those are the notes worth knowing
    /// when making the drink.
    /// </summary>
    private IQueryable<RecipeFeedback> Visible()
    {
        var query = db.RecipeFeedback.AsNoTracking();
        var userId = currentUser.RequireUserId();
        var branchId = currentUser.BranchId;
        return currentUser.Role switch
        {
            RoleName.Trainee => query.Where(f => f.SubmittedBy == userId
                                                 || (f.Source == FeedbackSource.Staff
                                                     && (f.State == FeedbackState.Accepted || f.State == FeedbackState.Addressed))),
            RoleName.BranchManager => query.Where(f => f.SubmittedBy == userId || (branchId != null && f.BranchId == branchId)),
            _ => query,
        };
    }

    public async Task<RecipeFeedbackDto> GetAsync(long id, CancellationToken cancellationToken)
    {
        // What the caller may not see does not exist for them.
        var feedback = await Visible().SingleOrDefaultAsync(f => f.Id == id, cancellationToken)
                       ?? throw DomainException.NotFound("Recipe feedback", id);
        return (await ToDtosAsync([feedback], cancellationToken))[0];
    }

    // ---------------------------------------------------------------- rendering

    private async Task<List<RecipeFeedbackDto>> ToDtosAsync(IReadOnlyCollection<RecipeFeedback> rows,
        CancellationToken cancellationToken)
    {
        var recipeIds = rows.Select(f => f.RecipeId).Distinct().ToList();
        var versionIds = rows.Select(f => f.RecipeVersionId)
            .Concat(rows.Select(f => f.AddressedInVersionId).OfType<long>()).Distinct().ToList();
        var stepIds = rows.Select(f => f.RecipeStepId).OfType<long>().Distinct().ToList();
        var branchIds = rows.Select(f => f.BranchId).OfType<long>().Distinct().ToList();
        var userIds = rows.Select(f => f.SubmittedBy).Distinct().ToList();

        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => new { r.RecipeCode, r.Name }, cancellationToken);
        var versionNos = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);
        var stepOrders = stepIds.Count == 0
            ? []
            : await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
                .SelectMany(v => v.Steps).Where(s => stepIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.StepOrder, cancellationToken);
        // A caller confined to a branch still reads the name of whoever wrote a remark it may see.
        var branches = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return
        [
            .. rows.Select(f =>
            {
                var recipe = recipes.GetValueOrDefault(f.RecipeId);
                return new RecipeFeedbackDto(f.Id, f.RecipeId, recipe?.RecipeCode, recipe?.Name, f.RecipeVersionId,
                    versionNos.TryGetValue(f.RecipeVersionId, out var versionNo) ? versionNo : null, f.RecipeStepId,
                    f.RecipeStepId is { } stepId && stepOrders.TryGetValue(stepId, out var stepOrder) ? stepOrder : null,
                    f.BranchId, f.BranchId is { } branchId ? branches.GetValueOrDefault(branchId) : null, f.Source,
                    f.Category, f.Content, f.Rating, f.SubmittedBy, users.GetValueOrDefault(f.SubmittedBy), f.CreatedAt,
                    f.State, f.ReviewedBy, f.ReviewedAt, f.ReviewNote, f.AddressedInVersionId,
                    f.AddressedInVersionId is { } addressedId && versionNos.TryGetValue(addressedId, out var addressedNo)
                        ? addressedNo
                        : null);
            }),
        ];
    }
}
