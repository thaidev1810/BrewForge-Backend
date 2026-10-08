using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Recipes.Validation;
using BrewForge.Domain.Training;
using BrewForge.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>
    /// The eight roles are always seeded, because the system cannot work
    /// without them. This switch covers the demonstration data on top.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Initial password of every seeded user. Required when <see cref="Enabled"/> is set.</summary>
    public string DefaultPassword { get; set; } = "";
}

/// <summary>Inserts what is missing and never touches what is already there.</summary>
public sealed class DataSeeder(BrewForgeDbContext db, IPasswordHasher hasher, IOptions<SeedOptions> options,
    TimeProvider clock, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var roles = await SeedRolesAsync(cancellationToken);
        if (!options.Value.Enabled) return;

        if (string.IsNullOrWhiteSpace(options.Value.DefaultPassword))
        {
            throw new InvalidOperationException(
                $"{SeedOptions.Section}:DefaultPassword must be configured when {SeedOptions.Section}:Enabled is true.");
        }

        var branches = await SeedBranchesAsync(cancellationToken);
        await SeedUsersAsync(roles, branches, cancellationToken);
        await SeedIngredientsAsync(cancellationToken);
        await SeedEquipmentAsync(cancellationToken);
        await SeedRecipesAsync(cancellationToken);
        await SeedLaunchStatusesAsync(branches, cancellationToken);
        await SeedRegulationsAsync(cancellationToken);
    }

    /// <summary>The day the seeded drinks of origin EXISTING have been on sale since.</summary>
    public static readonly DateTimeOffset ExistingDrinksLiveSince =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TradingCalendar.Offset).ToUniversalTime();

    /// <summary>
    /// BR-36: the drinks the chain sold before this system existed are on
    /// sale at every branch without a pilot, LIVE with coverage not met until
    /// enough staff are certified on them.
    /// </summary>
    private async Task SeedLaunchStatusesAsync(Dictionary<string, Branch> branches, CancellationToken cancellationToken)
    {
        var codes = SeedRecipes.All.Where(seed => seed.Origin == RecipeOrigin.Existing).Select(seed => seed.Code).ToList();
        var drinks = await db.RecipeVersions.Where(v => v.State == VersionState.Released)
            .Join(db.Recipes.Where(r => codes.Contains(r.RecipeCode)), v => v.RecipeId, r => r.Id,
                (v, r) => new { RecipeId = r.Id, VersionId = v.Id })
            .ToListAsync(cancellationToken);
        var covered = (await db.BranchLaunchStatuses.IgnoreQueryFilters()
                .Select(l => new { l.BranchId, l.RecipeId }).ToListAsync(cancellationToken))
            .Select(l => (l.BranchId, l.RecipeId)).ToHashSet();

        foreach (var drink in drinks)
        {
            foreach (var branch in SeedData.Branches.Select(seed => branches[seed.Code]))
            {
                if (covered.Contains((branch.Id, drink.RecipeId))) continue;
                db.BranchLaunchStatuses.Add(BranchLaunchStatus.LiveForExistingRecipe(branch.Id, drink.RecipeId,
                    drink.VersionId, minCertifiedStaff: 2, ExistingDrinksLiveSince));
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One regulation per course type, in force since the start of the year:
    /// induction and product training are mandatory for trainees, and product
    /// training comes after induction.
    /// </summary>
    private async Task SeedRegulationsAsync(CancellationToken cancellationToken)
    {
        var covered = await db.TrainingRegulations.Select(r => r.CourseType).Distinct().ToListAsync(cancellationToken);
        var author = await FirstUserOfAsync(RoleName.TrainingManager, cancellationToken);
        var effectiveFrom = new DateOnly(2026, 1, 1);

        (CourseType Type, RoleName? MandatoryFor, CourseType? Prerequisite, int DueDays)[] defaults =
        [
            (CourseType.Induction, RoleName.Trainee, null, 7),
            (CourseType.Product, RoleName.Trainee, CourseType.Induction, 14),
            (CourseType.Equipment, null, null, 14),
            (CourseType.Recertification, null, null, 7),
        ];
        foreach (var (type, mandatoryFor, prerequisite, dueDays) in defaults.Where(d => !covered.Contains(d.Type)))
        {
            db.TrainingRegulations.Add(TrainingRegulation.Create(type, mandatoryFor, prerequisite, dueDays,
                TrainingRules.Default.MaxRetakes, TrainingRules.Default.MinAttendancePct, effectiveFrom, author));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The reference recipes, entered through the aggregate like any other
    /// draft, so the seed cannot contain content the domain would refuse.
    /// </summary>
    private async Task SeedRecipesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Recipes.Select(r => r.RecipeCode).ToListAsync(cancellationToken);
        var missing = SeedRecipes.All.Where(seed => !existing.Contains(seed.Code)).ToList();
        if (missing.Count == 0) return;

        var author = await FirstUserOfAsync(RoleName.RdSpecialist, cancellationToken);
        var approver = await FirstUserOfAsync(RoleName.RdManager, cancellationToken);
        var ingredients = await db.Ingredients.ToListAsync(cancellationToken);
        var ingredientIds = ingredients.ToDictionary(i => i.IngredientCode, i => i.Id);
        var catalog = new ValidationCatalog(await db.StandardEquipment.ToListAsync(cancellationToken), ingredients);
        var now = clock.GetUtcNow();

        foreach (var seed in missing)
        {
            var recipe = Recipe.Create(seed.Code, seed.Name, seed.Category, seed.Origin, author, now);
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(cancellationToken);

            var version = RecipeVersion.CreateDraft(recipe.Id, versionNo: 1, author);
            version.ReplaceContent(
            [
                .. seed.Steps.Select((step, index) => new StepSpec(index + 1, step.Action, step.Equipment, step.Gate,
                    step.Seconds,
                    [.. step.Uses.Select(use => new IngredientSpec(ingredientIds[use.Code], use.Quantity, use.Unit))],
                    [.. step.After.Select(after => new DependencySpec(after.Step, after.Type))])),
            ], author);
            db.RecipeVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken);

            if (seed.Code == SeedRecipes.BrokenDemoCode) continue;
            await SubmitAndReleaseAsync(seed.Code, version, catalog, author, approver, now, cancellationToken);
        }
        logger.LogInformation("Seeded {Count} reference recipes", missing.Count);
    }

    /// <summary>
    /// Takes a seeded draft down the same path as any other: validated,
    /// submitted by its author and released by a different user. A reference
    /// recipe that does not pass stops the seed, because every slice after
    /// this one is tested against these recipes.
    /// </summary>
    private async Task SubmitAndReleaseAsync(string code, RecipeVersion version, ValidationCatalog catalog,
        long author, long approver, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = version.Validate(catalog);
        if (!report.Passed)
        {
            throw new InvalidOperationException(
                $"Seed recipe {code} does not pass validation: {string.Join("; ", report.Violations.Select(v => v.Message))}");
        }

        db.ValidationResults.AddRange(ValidationResult.FromReport(version.Id, report, now));
        version.Submit(report);
        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Submit, new { version.VersionNo },
            actorUserId: author);
        await db.SaveChangesAsync(cancellationToken);

        if (code == SeedRecipes.AwaitingReviewCode) return;

        var release = RecipeRelease.Prepare(version, currentlyReleased: null, report, approver,
            highestVersionNo: version.VersionNo, now);
        release.SupersedePrevious();
        release.Seal();
        db.Audit(AuditEntities.RecipeVersion, () => version.Id, AuditActions.Release,
            new { version.VersionNo, version.ContentHash, author, approver }, actorUserId: approver);
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<long> FirstUserOfAsync(RoleName role, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters()
            .Where(u => u.Role.RoleName == role)
            .OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(cancellationToken);

    private async Task<Dictionary<RoleName, Role>> SeedRolesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Roles.ToDictionaryAsync(r => r.RoleName, cancellationToken);
        foreach (var name in Enum.GetValues<RoleName>())
        {
            if (existing.ContainsKey(name)) continue;
            var role = new Role(name, Permissions.DefaultsFor(name));
            db.Roles.Add(role);
            existing[name] = role;
        }
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private async Task<Dictionary<string, Branch>> SeedBranchesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Branches.IgnoreQueryFilters()
            .ToDictionaryAsync(b => b.BranchCode, cancellationToken);
        foreach (var seed in SeedData.Branches)
        {
            if (existing.ContainsKey(seed.Code)) continue;
            var branch = Branch.Create(seed.Code, seed.Name, seed.Address);
            db.Branches.Add(branch);
            existing[seed.Code] = branch;
        }
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private async Task SeedUsersAsync(Dictionary<RoleName, Role> roles, Dictionary<string, Branch> branches,
        CancellationToken cancellationToken)
    {
        var existing = await db.Users.IgnoreQueryFilters().Select(u => u.Username).ToListAsync(cancellationToken);
        var missing = SeedData.Users.Where(seed => !existing.Contains(seed.Username)).ToList();
        if (missing.Count == 0) return;

        var now = clock.GetUtcNow();
        foreach (var seed in missing)
        {
            long? branchId = seed.BranchCode is null ? null : branches[seed.BranchCode].Id;
            db.Users.Add(AppUser.Create(seed.Username, seed.Email, hasher.Hash(options.Value.DefaultPassword),
                seed.FullName, roles[seed.Role], branchId, now));
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} users", missing.Count);
    }

    private async Task SeedIngredientsAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Ingredients.Select(i => i.IngredientCode).ToListAsync(cancellationToken);
        foreach (var seed in SeedData.Ingredients.Where(seed => !existing.Contains(seed.Code)))
        {
            db.Ingredients.Add(Ingredient.Create(seed.Code, seed.Name, seed.Unit, seed.ShelfLifeHours,
                seed.StorageRule));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedEquipmentAsync(CancellationToken cancellationToken)
    {
        var existing = await db.StandardEquipment.Select(e => e.EquipmentCode).ToListAsync(cancellationToken);
        foreach (var seed in SeedData.Equipment.Where(seed => !existing.Contains(seed.Code)))
        {
            db.StandardEquipment.Add(Domain.MasterData.StandardEquipment.Create(seed.Code, seed.Class, seed.Min,
                seed.Max, seed.Unit));
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
