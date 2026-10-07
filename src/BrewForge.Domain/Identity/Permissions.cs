namespace BrewForge.Domain.Identity;

/// <summary>
/// Permission codes stored in <c>role.permissions</c> and returned by
/// <c>GET /auth/me</c>, so the client can decide which widgets and controls to
/// render. They describe the interface; the server authorizes every endpoint
/// on the role itself and never trusts what the client chose to show (SE-03).
/// </summary>
public static class Permissions
{
    public const string UsersManage = "users.manage";
    public const string BranchesRead = "branches.read";
    public const string BranchesManage = "branches.manage";
    public const string CatalogRead = "catalog.read";
    public const string CatalogManage = "catalog.manage";
    public const string RecipesRead = "recipes.read";
    public const string RecipesAuthor = "recipes.author";
    public const string RecipesValidate = "recipes.validate";
    public const string RecipesReview = "recipes.review";
    public const string RecipesRelease = "recipes.release";
    public const string CoursesRead = "courses.read";
    public const string CoursesAuthor = "courses.author";
    public const string CoursesApprove = "courses.approve";
    public const string RegulationsRead = "regulations.read";
    public const string RegulationsManage = "regulations.manage";
    public const string ClassesManage = "classes.manage";
    public const string AttendanceRecord = "attendance.record";
    public const string LearningOwn = "learning.own";
    public const string PracticalEvaluate = "practical.evaluate";
    public const string CertificatesRead = "certificates.read";
    public const string SalesRead = "sales.read";
    public const string SalesRecord = "sales.record";
    public const string PilotsRead = "pilots.read";
    public const string PilotsManage = "pilots.manage";
    public const string ImpactAnalyze = "impact.analyze";
    public const string DashboardTraining = "dashboard.training";
    public const string DashboardBranch = "dashboard.branch";
    public const string DashboardCourse = "dashboard.course";
    public const string AuditRead = "audit.read";
    public const string ReportsCompliance = "reports.compliance";

    /// <summary>The permission set each role is seeded with.</summary>
    public static IReadOnlyList<string> DefaultsFor(RoleName role) => role switch
    {
        RoleName.Admin =>
        [
            UsersManage, BranchesRead, BranchesManage, CatalogRead, CatalogManage, RecipesRead,
            RegulationsRead, AuditRead,
        ],
        RoleName.RdSpecialist =>
        [
            BranchesRead, CatalogRead, RecipesRead, RecipesAuthor, RecipesValidate,
        ],
        RoleName.RdManager =>
        [
            BranchesRead, CatalogRead, RecipesRead, RecipesValidate, RecipesReview, RecipesRelease,
            CoursesRead, SalesRead, PilotsRead, PilotsManage, ImpactAnalyze, DashboardTraining,
            DashboardBranch, DashboardCourse, AuditRead, ReportsCompliance,
        ],
        RoleName.Trainer =>
        [
            BranchesRead, CatalogRead, RecipesRead, CoursesRead, CoursesAuthor, ClassesManage,
            AttendanceRecord, PracticalEvaluate, CertificatesRead, DashboardTraining,
        ],
        RoleName.Trainee =>
        [
            BranchesRead, LearningOwn,
        ],
        RoleName.QualityAuditor =>
        [
            BranchesRead, CatalogRead, RecipesRead, CertificatesRead, SalesRead, DashboardTraining,
            DashboardBranch, AuditRead, ReportsCompliance,
        ],
        RoleName.BranchManager =>
        [
            BranchesRead, SalesRead, SalesRecord, PilotsRead, DashboardTraining, DashboardBranch,
        ],
        RoleName.TrainingManager =>
        [
            BranchesRead, CatalogRead, RecipesRead, CoursesRead, CoursesApprove, RegulationsRead,
            RegulationsManage, SalesRead, DashboardCourse,
        ],
        _ => [],
    };
}
