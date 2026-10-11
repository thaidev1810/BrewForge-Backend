using System.Text;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Audit;
using BrewForge.Domain.Common;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Impact;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Notifications;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Sales;
using BrewForge.Domain.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BrewForge.Infrastructure.Persistence;

/// <summary>
/// Maps the domain onto the schema of <c>db/migrations</c>.
/// The SQL files are authoritative: this context never creates or alters tables,
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
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();
    public DbSet<ValidationResult> ValidationResults => Set<ValidationResult>();
    public DbSet<AiDraftLog> AiDraftLogs => Set<AiDraftLog>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<TrainingRegulation> TrainingRegulations => Set<TrainingRegulation>();
    public DbSet<TrainingClass> TrainingClasses => Set<TrainingClass>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<PracticalVideo> PracticalVideos => Set<PracticalVideo>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<BranchLaunchStatus> BranchLaunchStatuses => Set<BranchLaunchStatus>();
    public DbSet<SalesRecord> SalesRecords => Set<SalesRecord>();
    public DbSet<PilotProgram> PilotPrograms => Set<PilotProgram>();
    public DbSet<ChangeImpact> ChangeImpacts => Set<ChangeImpact>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

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

        modelBuilder.Entity<Ingredient>(ingredient =>
        {
            ingredient.Property(i => i.BrewTempMinC).HasPrecision(5, 1);
            ingredient.Property(i => i.BrewTempMaxC).HasPrecision(5, 1);
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

        ConfigureRecipes(modelBuilder);
        ConfigureCourses(modelBuilder);
        ConfigureTraining(modelBuilder);

        ApplySchemaConventions(modelBuilder);
    }

    /// <summary>
    /// The recipe aggregate. The unique indexes repeat constraints the schema
    /// already has: declaring them tells EF Core that a step number freed by
    /// a deleted step may be reused by an inserted one, so that replacing the
    /// content of a draft deletes before it inserts.
    /// </summary>
    private static void ConfigureRecipes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recipe>();

        modelBuilder.Entity<RecipeVersion>(version =>
        {
            version.Property(v => v.ContentHash).HasMaxLength(64).IsFixedLength();
            version.HasMany(v => v.Steps).WithOne().HasForeignKey(s => s.RecipeVersionId)
                .OnDelete(DeleteBehavior.Cascade);
            version.Navigation(v => v.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
            version.HasIndex(v => new { v.RecipeId, v.VersionNo }).IsUnique();
        });

        modelBuilder.Entity<RecipeStep>(step =>
        {
            step.Property(s => s.TemperatureC).HasPrecision(5, 1);
            step.Property(s => s.PressureBar).HasPrecision(4, 1);
            step.HasMany(s => s.Ingredients).WithOne().HasForeignKey(i => i.StepId)
                .OnDelete(DeleteBehavior.Cascade);
            step.HasMany(s => s.Dependencies).WithOne(d => d.Step).HasForeignKey(d => d.StepId)
                .OnDelete(DeleteBehavior.Cascade);
            step.Navigation(s => s.Ingredients).UsePropertyAccessMode(PropertyAccessMode.Field);
            step.Navigation(s => s.Dependencies).UsePropertyAccessMode(PropertyAccessMode.Field);
            step.HasIndex(s => new { s.RecipeVersionId, s.StepOrder }).IsUnique();
        });

        modelBuilder.Entity<StepDependency>(dependency =>
        {
            dependency.HasOne(d => d.DependsOnStep).WithMany().HasForeignKey(d => d.DependsOnStepId)
                .OnDelete(DeleteBehavior.Cascade);
            dependency.HasIndex(d => new { d.StepId, d.DependsOnStepId }).IsUnique();
        });

        modelBuilder.Entity<StepIngredient>(ingredient =>
        {
            ingredient.Property(i => i.Quantity).HasPrecision(10, 3);
            ingredient.HasIndex(i => new { i.StepId, i.IngredientId }).IsUnique();
        });

        modelBuilder.Entity<ValidationResult>(result =>
        {
            result.Property(r => r.ViolationDetail).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AiDraftLog>(log =>
        {
            log.Property(l => l.RawResponse).HasColumnType("jsonb");
        });
    }

    /// <summary>The course aggregate: seven modules, their lessons, and one quiz with its questions.</summary>
    private static void ConfigureCourses(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Course>(course =>
        {
            course.HasMany(c => c.Modules).WithOne().HasForeignKey(m => m.CourseId).OnDelete(DeleteBehavior.Cascade);
            course.Navigation(c => c.Modules).UsePropertyAccessMode(PropertyAccessMode.Field);
            course.HasOne(c => c.Quiz).WithOne().HasForeignKey<Quiz>(q => q.CourseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CourseModule>(module =>
        {
            module.HasMany(m => m.Lessons).WithOne().HasForeignKey(l => l.CourseModuleId)
                .OnDelete(DeleteBehavior.Cascade);
            module.Navigation(m => m.Lessons).UsePropertyAccessMode(PropertyAccessMode.Field);
            module.HasIndex(m => new { m.CourseId, m.ModuleType }).IsUnique();
            module.HasIndex(m => new { m.CourseId, m.ModuleOrder }).IsUnique();
        });

        modelBuilder.Entity<Lesson>(lesson =>
        {
            lesson.HasIndex(l => new { l.CourseModuleId, l.LessonOrder }).IsUnique();
        });

        modelBuilder.Entity<Quiz>(quiz =>
        {
            quiz.HasMany(q => q.Questions).WithOne().HasForeignKey(question => question.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
            quiz.Navigation(q => q.Questions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<QuizQuestion>(question =>
        {
            question.Property(q => q.OptionsJson).HasColumnType("jsonb");
        });
    }

    /// <summary>
    /// Classes, enrolments, attendance and certificates. A store-level caller
    /// sees the classes of its own branch and the enrolments and certificates
    /// of the staff of its own branch.
    /// </summary>
    private void ConfigureTraining(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingRegulation>();

        modelBuilder.Entity<TrainingClass>(trainingClass =>
        {
            trainingClass.HasMany(c => c.Sessions).WithOne().HasForeignKey(s => s.TrainingClassId)
                .OnDelete(DeleteBehavior.Cascade);
            trainingClass.Navigation(c => c.Sessions).UsePropertyAccessMode(PropertyAccessMode.Field);
            trainingClass.HasQueryFilter(c => BranchScope == null || c.BranchId == BranchScope);
        });

        modelBuilder.Entity<TrainingSession>(session =>
        {
            session.HasMany(s => s.Modules).WithOne().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
            session.Navigation(s => s.Modules).UsePropertyAccessMode(PropertyAccessMode.Field);
            session.HasIndex(s => new { s.TrainingClassId, s.SessionNo }).IsUnique();
        });

        modelBuilder.Entity<SessionModule>(module =>
        {
            module.HasIndex(m => new { m.SessionId, m.CourseModuleId }).IsUnique();
        });

        modelBuilder.Entity<Enrollment>(enrollment =>
        {
            enrollment.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
            enrollment.HasMany(e => e.Modules).WithOne().HasForeignKey(m => m.EnrollmentId)
                .OnDelete(DeleteBehavior.Cascade);
            enrollment.Navigation(e => e.Modules).UsePropertyAccessMode(PropertyAccessMode.Field);
            enrollment.HasMany(e => e.QuizAttempts).WithOne().HasForeignKey(a => a.EnrollmentId)
                .OnDelete(DeleteBehavior.Cascade);
            enrollment.Navigation(e => e.QuizAttempts).UsePropertyAccessMode(PropertyAccessMode.Field);
            enrollment.HasMany(e => e.PracticalEvaluations).WithOne().HasForeignKey(p => p.EnrollmentId)
                .OnDelete(DeleteBehavior.Cascade);
            enrollment.Navigation(e => e.PracticalEvaluations).UsePropertyAccessMode(PropertyAccessMode.Field);
            enrollment.HasMany(e => e.PracticalVideos).WithOne().HasForeignKey(v => v.EnrollmentId)
                .OnDelete(DeleteBehavior.Cascade);
            enrollment.Navigation(e => e.PracticalVideos).UsePropertyAccessMode(PropertyAccessMode.Field);
            enrollment.HasQueryFilter(e => BranchScope == null || e.User.BranchId == BranchScope);
        });

        modelBuilder.Entity<QuizAttempt>(attempt =>
        {
            attempt.Property(a => a.AnswersJson).HasColumnType("jsonb");
            attempt.HasIndex(a => new { a.EnrollmentId, a.AttemptNo }).IsUnique();
        });

        modelBuilder.Entity<PracticalEvaluation>(evaluation =>
        {
            evaluation.Property(p => p.ChecklistJson).HasColumnType("jsonb");
            // One recording is the evidence of one evaluation.
            evaluation.HasOne(p => p.Video).WithOne().HasForeignKey<PracticalEvaluation>(p => p.PracticalVideoId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PracticalVideo>(video =>
        {
            video.Property(v => v.Sha256).HasMaxLength(64).IsFixedLength();
            video.HasIndex(v => v.StorageKey).IsUnique();
        });

        modelBuilder.Entity<BranchLaunchStatus>(launch =>
        {
            launch.HasQueryFilter(l => BranchScope == null || l.BranchId == BranchScope);
        });

        // A store-level caller sees the pilots its branch takes part in, and of those its own branch only.
        modelBuilder.Entity<PilotProgram>(pilot =>
        {
            pilot.Property(p => p.CriteriaJson).HasColumnType("jsonb");
            pilot.HasMany(p => p.Branches).WithOne().HasForeignKey(b => b.PilotProgramId)
                .OnDelete(DeleteBehavior.Cascade);
            pilot.Navigation(p => p.Branches).UsePropertyAccessMode(PropertyAccessMode.Field);
            pilot.HasOne(p => p.Decision).WithOne().HasForeignKey<LaunchDecision>(d => d.PilotProgramId)
                .OnDelete(DeleteBehavior.Cascade);
            pilot.HasQueryFilter(p => BranchScope == null || p.Branches.Any(b => b.BranchId == BranchScope));
        });

        modelBuilder.Entity<PilotBranch>(branch =>
        {
            branch.HasIndex(b => new { b.PilotProgramId, b.BranchId }).IsUnique();
            branch.HasQueryFilter(b => BranchScope == null || b.BranchId == BranchScope);
        });

        modelBuilder.Entity<ChangeImpact>();

        modelBuilder.Entity<Notification>();
        modelBuilder.Entity<PushSubscription>(subscription =>
        {
            subscription.HasIndex(s => s.Endpoint).IsUnique();
        });

        modelBuilder.Entity<LaunchDecision>(decision =>
        {
            decision.Property(d => d.EvaluatedJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SalesRecord>(sales =>
        {
            // BR-25. Declared so that EF Core knows the key a replaced day keeps.
            sales.HasIndex(s => new { s.BranchId, s.RecipeId, s.TradingDate }).IsUnique();
            // A store-level caller sees the sales of its own branch.
            sales.HasQueryFilter(s => BranchScope == null || s.BranchId == BranchScope);
        });

        modelBuilder.Entity<ModuleProgress>(progress =>
        {
            progress.HasIndex(p => new { p.EnrollmentId, p.CourseModuleId }).IsUnique();
        });

        modelBuilder.Entity<Attendance>(attendance =>
        {
            attendance.HasIndex(a => new { a.SessionId, a.EnrollmentId }).IsUnique();
        });

        modelBuilder.Entity<Certificate>(certificate =>
        {
            // superseded_by points at the certificate that replaced this one.
            certificate.HasOne(c => c.Successor).WithMany().HasForeignKey(c => c.SupersededBy)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null) return await work();

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        await transaction.CommitAsync(cancellationToken);
        return result;
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

        GuardReleasedVersions();
    }

    /// <summary>
    /// BR-01 at the door of the database. A version that was already
    /// immutable when it was loaded may change in exactly one way: its state
    /// moves to SUPERSEDED. Its steps, their ingredients and their
    /// dependencies may not change at all. The aggregate refuses these
    /// changes itself; this catches a code path that went around it, and
    /// answers with the same clean 409 rather than a trigger error.
    /// </summary>
    private void GuardReleasedVersions()
    {
        string[] allowedOnceImmutable = [nameof(RecipeVersion.State), nameof(RecipeVersion.SupersededAt)];
        var sealedVersionIds = new HashSet<long>();

        foreach (var entry in ChangeTracker.Entries<RecipeVersion>())
        {
            var wasImmutable = entry.State != EntityState.Added
                               && (bool)entry.Property(nameof(RecipeVersion.IsImmutable)).OriginalValue!;
            if (!wasImmutable) continue;
            sealedVersionIds.Add(entry.Entity.Id);

            if (entry.State == EntityState.Modified
                && entry.Properties.Any(p => p.IsModified && !allowedOnceImmutable.Contains(p.Metadata.Name)))
            {
                throw ReleasedVersionIsImmutable();
            }
        }
        if (sealedVersionIds.Count == 0) return;

        var changedSteps = ChangeTracker.Entries<RecipeStep>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (changedSteps.Any(entry => sealedVersionIds.Contains(entry.Entity.RecipeVersionId)))
        {
            throw ReleasedVersionIsImmutable();
        }

        var sealedStepIds = ChangeTracker.Entries<RecipeStep>()
            .Where(entry => sealedVersionIds.Contains(entry.Entity.RecipeVersionId))
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var childChanged =
            ChangeTracker.Entries<StepIngredient>().Any(entry =>
                entry.State != EntityState.Unchanged && sealedStepIds.Contains(entry.Entity.StepId))
            || ChangeTracker.Entries<StepDependency>().Any(entry =>
                entry.State != EntityState.Unchanged && sealedStepIds.Contains(entry.Entity.StepId));
        if (childChanged) throw ReleasedVersionIsImmutable();
    }

    private static DomainException ReleasedVersionIsImmutable() =>
        DomainException.RuleViolation("BR-01",
            "A released recipe version cannot be modified. Create a new version instead.",
            ErrorCodes.ReleasedVersionImmutable);

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
