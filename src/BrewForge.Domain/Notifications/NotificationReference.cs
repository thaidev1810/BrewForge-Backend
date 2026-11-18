namespace BrewForge.Domain.Notifications;

/// <summary>
/// BE-05: the kinds of row a notification can point at (<c>referenceType</c>), with the endpoint the client
/// opens it by. Opening goes through that endpoint, which checks the reader's role and branch like any other
/// read: a row the reader may no longer see answers 403 or 404, and the client shows the message alone.
/// </summary>
public static class NotificationReference
{
    /// <summary>GET /enrollments/{id}</summary>
    public const string Enrollment = "ENROLLMENT";

    /// <summary>GET /certificates/{id}</summary>
    public const string Certificate = "CERTIFICATE";

    /// <summary>GET /courses/{id}</summary>
    public const string Course = "COURSE";

    /// <summary>GET /practical-schedules/{id}</summary>
    public const string PracticalSchedule = "PRACTICAL_SCHEDULE";

    /// <summary>GET /training-classes/{id}</summary>
    public const string TrainingClass = "TRAINING_CLASS";

    /// <summary>GET /branch-audits/{id}</summary>
    public const string BranchAudit = "BRANCH_AUDIT";

    /// <summary>GET /recipe-feedback/{id}</summary>
    public const string RecipeFeedback = "RECIPE_FEEDBACK";

    /// <summary>GET /pilots/{id}</summary>
    public const string Pilot = "PILOT";

    /// <summary>GET /recipes/{id}</summary>
    public const string Recipe = "RECIPE";

    /// <summary>
    /// The key of the notification context that names each type, most specific first: the first one present
    /// in the context is what the notification is about.
    /// </summary>
    public static readonly IReadOnlyList<(string ContextKey, string Type)> ByPriority =
    [
        ("practicalScheduleId", PracticalSchedule),
        ("enrollmentId", Enrollment),
        ("certificateId", Certificate),
        ("feedbackId", RecipeFeedback),
        ("branchAuditId", BranchAudit),
        ("pilotId", Pilot),
        ("courseId", Course),
        ("trainingClassId", TrainingClass),
        ("recipeId", Recipe),
    ];
}
