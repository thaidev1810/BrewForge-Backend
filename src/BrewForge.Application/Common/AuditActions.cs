namespace BrewForge.Application.Common;

/// <summary>The values written to <c>audit_log.action</c>.</summary>
public static class AuditActions
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string Deactivate = "DEACTIVATE";
    public const string Login = "LOGIN";
    public const string Logout = "LOGOUT";
    public const string Revoke = "REVOKE";

    public const string Validate = "VALIDATE";
    public const string Submit = "SUBMIT";
    public const string AiDraft = "AI_DRAFT";

    /// <summary>
    /// One automatic AI repair of a draft. The schema has no counter column
    /// on <c>recipe_version</c>, so the number of repairs a draft has had is
    /// the number of these entries it has - which cannot be reset, because
    /// the log is append-only.
    /// </summary>
    public const string AiRepair = "AI_REPAIR";
    public const string ManualRepair = "MANUAL_REPAIR";

    public const string Review = "REVIEW";
    public const string Release = "RELEASE";
    public const string ReleaseRefused = "RELEASE_REFUSED";
    public const string Supersede = "SUPERSEDE";
    public const string Rollback = "ROLLBACK";
    public const string ImportExisting = "IMPORT_EXISTING";

    public const string Delete = "DELETE";
    public const string Regenerate = "REGENERATE";
    public const string UpdateQuiz = "UPDATE_QUIZ";
    public const string AddQuestion = "ADD_QUESTION";
    public const string UpdateChecklist = "UPDATE_CHECKLIST";
    public const string Approve = "APPROVE";
    public const string Return = "RETURN";
    public const string Rebuild = "REBUILD";

    public const string ScheduleSession = "SCHEDULE_SESSION";
    public const string RescheduleSession = "RESCHEDULE_SESSION";
    public const string RemoveSession = "REMOVE_SESSION";
    public const string Open = "OPEN";
    public const string Close = "CLOSE";
    public const string Assign = "ASSIGN";
    public const string Reset = "RESET";
    public const string CompleteModule = "COMPLETE_MODULE";
    public const string RecordAttendance = "RECORD_ATTENDANCE";
    public const string CorrectAttendance = "CORRECT_ATTENDANCE";
    public const string QuizAttempt = "QUIZ_ATTEMPT";
    public const string PracticalEvaluation = "PRACTICAL_EVALUATION";
    public const string IssueCertificate = "ISSUE_CERTIFICATE";

    public const string CorrectSales = "CORRECT_SALES";
    public const string ReplaceSales = "REPLACE_SALES";
    public const string PosImport = "POS_IMPORT";

    public const string GoLive = "GO_LIVE";

    /// <summary>
    /// LIVE to WITHDRAWN, and a move of a live branch to another version. The
    /// launch status keeps only the present, so these entries are also how a
    /// sale entered late finds the version that was live on its day (BR-24).
    /// </summary>
    public const string Withdraw = "WITHDRAW";
    public const string MoveVersion = "MOVE_VERSION";

    public const string Plan = "PLAN";
    public const string Start = "START";
    public const string End = "END";
    public const string Cancel = "CANCEL";
    public const string Decide = "DECIDE";

    /// <summary>
    /// A notification to a user. The schema has no notification table, so
    /// the message is recorded here, against the user it is for.
    /// </summary>
    public const string Notify = "NOTIFY";
}

/// <summary>The values written to <c>audit_log.entity_type</c>.</summary>
public static class AuditEntities
{
    public const string User = "AppUser";
    public const string Branch = "Branch";
    public const string Ingredient = "Ingredient";
    public const string StandardEquipment = "StandardEquipment";
    public const string Recipe = "Recipe";
    public const string RecipeVersion = "RecipeVersion";
    public const string Course = "Course";
    public const string CourseModule = "CourseModule";
    public const string Lesson = "Lesson";
    public const string TrainingRegulation = "TrainingRegulation";
    public const string TrainingClass = "TrainingClass";
    public const string TrainingSession = "TrainingSession";
    public const string Enrollment = "Enrollment";
    public const string Attendance = "Attendance";
    public const string Certificate = "Certificate";
    public const string BranchLaunchStatus = "BranchLaunchStatus";
    public const string PilotProgram = "PilotProgram";
    public const string SalesRecord = "SalesRecord";

    /// <summary>
    /// One POS import, identified by its job number. The schema has no table
    /// for import jobs, so the result of an import is its audit entry.
    /// </summary>
    public const string SalesImport = "SalesImport";

    /// <summary>
    /// A refresh token, identified by its numeric token id. The schema has no
    /// table for refresh tokens, so a revocation is recorded here and a token
    /// is revoked exactly when such an entry exists for its id.
    /// </summary>
    public const string RefreshToken = "RefreshToken";
}
