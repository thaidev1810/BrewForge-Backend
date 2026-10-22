using BrewForge.Domain.Audit;
using BrewForge.Domain.Courses;
using BrewForge.Domain.Identity;
using BrewForge.Domain.Impact;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Notifications;
using BrewForge.Domain.Quality;
using BrewForge.Domain.Recipes;
using BrewForge.Domain.Sales;
using BrewForge.Domain.Training;
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
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<QuestionBankItem> QuestionBank { get; }
    DbSet<PracticalSchedule> PracticalSchedules { get; }

    DbSet<Recipe> Recipes { get; }

    /// <summary>The aggregate root. Steps, dependencies and ingredients are reached through it.</summary>
    DbSet<RecipeVersion> RecipeVersions { get; }
    DbSet<ValidationResult> ValidationResults { get; }
    DbSet<AiDraftLog> AiDraftLogs { get; }

    /// <summary>What staff and customers said about a version of a drink, and what R&amp;D made of it.</summary>
    DbSet<RecipeFeedback> RecipeFeedback { get; }

    /// <summary>The aggregate root. Modules, lessons and the quiz are reached through it.</summary>
    DbSet<Course> Courses { get; }
    DbSet<LessonMedia> LessonMedia { get; }

    DbSet<TrainingRegulation> TrainingRegulations { get; }

    /// <summary>The aggregate root. Sessions and the modules they cover are reached through it.</summary>
    DbSet<TrainingClass> TrainingClasses { get; }

    /// <summary>The aggregate root. Module progress is reached through it.</summary>
    DbSet<Enrollment> Enrollments { get; }
    DbSet<Attendance> Attendances { get; }
    DbSet<PracticalVideo> PracticalVideos { get; }
    DbSet<Certificate> Certificates { get; }
    DbSet<BranchLaunchStatus> BranchLaunchStatuses { get; }
    DbSet<SalesRecord> SalesRecords { get; }

    /// <summary>The idempotency ledger that makes a retried POS import a replay, not a second import.</summary>
    DbSet<PosImportIdempotency> PosImportIdempotencies { get; }

    /// <summary>The aggregate root. Pilot branches and the launch decision are reached through it.</summary>
    DbSet<PilotProgram> PilotPrograms { get; }
    DbSet<ChangeImpact> ChangeImpacts { get; }
    DbSet<ImpactRunHeader> ImpactRuns { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<PushSubscription> PushSubscriptions { get; }

    /// <summary>The aggregate root. Checklist items are reached through it.</summary>
    DbSet<BranchAudit> BranchAudits { get; }
    DbSet<CorrectiveAction> CorrectiveActions { get; }

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

    /// <summary>
    /// Runs several saves as one unit: all of them are stored, or none is.
    /// Needed where the order of two changes matters to a database constraint.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);
}
