using System.Text;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Audit;
using BrewForge.Domain.Common;
using BrewForge.Domain.Identity;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BrewForge.Infrastructure.Persistence;

/// <summary>
/// Maps the domain onto the schema of <c>db/migrations/001_initial_schema.sql</c>.
/// The SQL file is authoritative: this context never creates or alters tables,
/// it only names them the way the file does.
/// </summary>
public sealed class BrewForgeDbContext(DbContextOptions<BrewForgeDbContext> options, ICurrentUser currentUser,
    TimeProvider clock) : DbContext(options), IBrewForgeDbContext
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly List<PendingAudit> _pendingAudits = [];

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<StandardEquipment> StandardEquipment => Set<StandardEquipment>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// Read by the query filters below. A member of the context, not a
    /// captured value, so it is evaluated for every query of every request.
    /// </summary>
    private long? BranchScope => currentUser.RestrictedToBranchId;

    public void Audit(string entityType, Func<long> entityId, string action, object? payload = null,
        long? actorUserId = null) =>
        _pendingAudits.Add(new PendingAudit(entityType, entityId, action,
            payload is null ? null : JsonSerializer.Serialize(payload, PayloadJson), actorUserId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>(role =>
        {
            role.Property(r => r.Permissions)
                .HasColumnType("jsonb")
                .HasConversion(
                    list => JsonSerializer.Serialize(list, PayloadJson),
                    json => JsonSerializer.Deserialize<List<string>>(json, PayloadJson) ?? new List<string>(),
                    new ValueComparer<List<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                        list => list.ToList()));
        });

        modelBuilder.Entity<AppUser>(user =>
        {
            user.HasOne(u => u.Role).WithMany().HasForeignKey(u => u.RoleId);
            // A store-level caller sees only the staff of its own branch.
            user.HasQueryFilter(u => BranchScope == null || u.BranchId == BranchScope);
        });

        modelBuilder.Entity<Branch>(branch =>
        {
            // A store-level caller sees only its own branch.
            branch.HasQueryFilter(b => BranchScope == null || b.Id == BranchScope);
        });

        modelBuilder.Entity<StandardEquipment>(equipment =>
        {
            equipment.Property(e => e.MinThreshold).HasPrecision(10, 3);
            equipment.Property(e => e.MaxThreshold).HasPrecision(10, 3);
        });

        modelBuilder.Entity<AuditLog>(audit =>
        {
            audit.Property(a => a.PayloadJson).HasColumnType("jsonb");
        });

        ApplySchemaConventions(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardRetainedRecords();
        if (_pendingAudits.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        // The change and its audit entries are one unit: either both are
        // stored or neither is. The ids of new rows only exist after the
        // first save, hence the two steps inside one transaction.
        var ownTransaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            var now = clock.GetUtcNow();
            foreach (var pending in _pendingAudits)
            {
                Set<AuditLog>().Add(new AuditLog(pending.ActorUserId ?? currentUser.UserId, pending.EntityType,
                    pending.EntityId(), pending.Action, pending.PayloadJson, now));
            }
            _pendingAudits.Clear();
            written += await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            if (ownTransaction is not null) await ownTransaction.CommitAsync(cancellationToken);
            return written;
        }
        finally
        {
            _pendingAudits.Clear();
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The last line of defence for BR-16, BR-01 and BR-15 and for the
    /// append-only audit trail: whatever code path asked, a retained record is
    /// not deleted and an audit entry is not changed.
    /// </summary>
    private void GuardRetainedRecords()
    {
        foreach (var entry in ChangeTracker.Entries<INeverDeleted>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw DomainException.RuleViolation(entry.Entity.RetentionRule,
                    $"{entry.Metadata.ClrType.Name} records are retained permanently and cannot be deleted. " +
                    "Deactivate the record instead.");
            }
            if (entry is { State: EntityState.Modified, Entity: AuditLog })
            {
                throw DomainException.RuleViolation("AUDIT", "The audit log is append-only.");
            }
        }
    }

    /// <summary>
    /// snake_case names and string-coded enumerations, exactly as the data
    /// dictionary defines them, applied to every entity so that no mapping can
    /// drift from the convention.
    /// </summary>
    private static void ApplySchemaConventions(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.ClrType.Name));

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));

                var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (enumType.IsEnum)
                {
                    var converterType = typeof(EnumCodeConverter<>).MakeGenericType(enumType);
                    property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    private sealed record PendingAudit(string EntityType, Func<long> EntityId, string Action, string? PayloadJson,
        long? ActorUserId);
}

/// <summary>Stores an enum as the code the data dictionary lists for it.</summary>
internal sealed class EnumCodeConverter<T>() : ValueConverter<T, string>(
    value => EnumCode<T>.ToCode(value),
    code => EnumCode<T>.Parse(code))
    where T : struct, Enum;
