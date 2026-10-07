using BrewForge.Domain.Audit;
using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Abstractions;

/// <summary>
/// The persistence port of the application layer. Reads through it are already
/// restricted to the caller's branch for store-level roles, and it refuses to
/// delete anything the business rules say must be retained.
/// </summary>
public interface IBrewForgeDbContext
{
    DbSet<Role> Roles { get; }
    DbSet<AppUser> Users { get; }
    DbSet<Branch> Branches { get; }
    DbSet<StandardEquipment> StandardEquipment { get; }
    DbSet<Ingredient> Ingredients { get; }
    DbSet<AuditLog> AuditLogs { get; }

    /// <summary>
    /// Queues an audit entry that is written in the same transaction as the
    /// next <see cref="SaveChangesAsync"/>. The entity id is read after the
    /// save, so a newly created entity can be audited before it has an id.
    /// </summary>
    /// <param name="actorUserId">
    /// Overrides the acting user. Only needed where there is no authenticated
    /// caller yet, such as a login.
    /// </param>
    void Audit(string entityType, Func<long> entityId, string action, object? payload = null,
        long? actorUserId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
