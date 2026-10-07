using BrewForge.Application.Abstractions;
using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;
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
    }

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
