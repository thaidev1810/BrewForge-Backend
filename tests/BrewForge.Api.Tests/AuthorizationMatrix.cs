using BrewForge.Domain.Identity;

namespace BrewForge.Api.Tests;

/// <summary>
/// The authorization matrix: for every endpoint of the API, the roles that
/// may reach it. It is written here independently of the controllers, from
/// the role columns of the API contract, and <see cref="AuthorizationMatrixTests"/>
/// holds the running API to it in both directions - every listed role gets
/// in, every other role gets 403, and an endpoint missing from this table
/// fails the build.
/// </summary>
public static class AuthorizationMatrix
{
    private static readonly RoleName[] Everyone = Enum.GetValues<RoleName>();

    private static readonly RoleName[] Nobody = [];

    private static readonly RoleName[] AdminOnly = [RoleName.Admin];

    private static readonly RoleName[] Specialist = [RoleName.RdSpecialist];

    private static readonly RoleName[] Manager = [RoleName.RdManager];

    private static readonly RoleName[] TrainerOnly = [RoleName.Trainer];

    private static readonly RoleName[] Learners = [RoleName.Trainee, RoleName.Trainer];

    /// <summary>"All head-office roles" in the contract: every role not confined to a branch.</summary>
    private static readonly RoleName[] HeadOffice =
    [
        RoleName.Admin, RoleName.RdSpecialist, RoleName.RdManager, RoleName.Trainer, RoleName.QualityAuditor,
        RoleName.TrainingManager,
    ];

    public sealed record Entry(string Method, string Route, RoleName[] Allowed, bool Anonymous = false)
    {
        public string Key => $"{Method} {Route}";

        /// <summary>The route with every parameter replaced by an id that does not exist.</summary>
        public string Path => "/api/v1" + System.Text.RegularExpressions.Regex.Replace(Route, @"\{[^}]+\}", "999999999");
    }

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // 2. Authentication
        new("POST", "/auth/login", Nobody, Anonymous: true),
        new("POST", "/auth/refresh", Nobody, Anonymous: true),
        new("POST", "/auth/logout", Everyone),
        new("GET", "/auth/me", Everyone),

        // 3. Master data
        new("GET", "/users", AdminOnly),
        new("POST", "/users", AdminOnly),
        new("GET", "/users/{id}", AdminOnly),
        new("PUT", "/users/{id}", AdminOnly),
        new("POST", "/users/{id}/deactivate", AdminOnly),

        // Reads are wider than the contract's "ADMIN": see BranchesController.
        new("GET", "/branches", Everyone),
        new("POST", "/branches", AdminOnly),
        new("GET", "/branches/{id}", Everyone),
        new("PUT", "/branches/{id}", AdminOnly),
        new("POST", "/branches/{id}/deactivate", AdminOnly),

        new("GET", "/ingredients", HeadOffice),
        new("POST", "/ingredients", AdminOnly),
        new("GET", "/ingredients/{id}", HeadOffice),
        new("PUT", "/ingredients/{id}", AdminOnly),
        new("POST", "/ingredients/{id}/deactivate", AdminOnly),

        new("GET", "/equipment-classes", HeadOffice),
        new("POST", "/equipment-classes", AdminOnly),
        new("GET", "/equipment-classes/{id}", HeadOffice),
        new("PUT", "/equipment-classes/{id}", AdminOnly),
        new("POST", "/equipment-classes/{id}/deactivate", AdminOnly),

        // 4. Recipe authoring and standardization
        new("GET", "/recipes", HeadOffice),
        new("POST", "/recipes", Specialist),
        new("GET", "/recipes/{id}", HeadOffice),
        new("GET", "/recipes/{id}/versions", HeadOffice),
        new("POST", "/recipes/{id}/versions", Specialist),
        new("GET", "/recipe-versions/{id}", HeadOffice),
        new("PUT", "/recipe-versions/{id}", Specialist),
        new("POST", "/recipe-versions/{id}/generate-draft", Specialist),
        new("POST", "/recipe-versions/{id}/validate", [RoleName.RdSpecialist, RoleName.RdManager]),
        new("POST", "/recipe-versions/{id}/repair", Specialist),
        new("POST", "/recipe-versions/{id}/submit", Specialist),
        new("POST", "/recipe-versions/{id}/review", Manager),
        new("POST", "/recipe-versions/{id}/release", Manager),
        new("POST", "/recipe-versions/{id}/rollback", Manager),
        new("POST", "/recipes/import-existing", Specialist),

        // 5. Course authoring
        new("GET", "/courses", [RoleName.Trainer, RoleName.TrainingManager, RoleName.RdManager]),
        new("POST", "/courses", TrainerOnly),
        new("GET", "/courses/{id}", [RoleName.Trainer, RoleName.TrainingManager]),
        new("GET", "/courses/{id}/modules", [RoleName.Trainer, RoleName.TrainingManager]),
        new("PUT", "/course-modules/{id}", TrainerOnly),
        new("POST", "/course-modules/{id}/regenerate", TrainerOnly),
        new("GET", "/course-modules/{id}/lessons", TrainerOnly),
        new("POST", "/course-modules/{id}/lessons", TrainerOnly),
        new("PUT", "/lessons/{id}", TrainerOnly),
        new("DELETE", "/lessons/{id}", TrainerOnly),
        new("GET", "/courses/{id}/quiz", TrainerOnly),
        new("PUT", "/courses/{id}/quiz", TrainerOnly),
        new("GET", "/courses/{id}/quiz/questions", TrainerOnly),
        new("POST", "/courses/{id}/quiz/questions", TrainerOnly),
        new("PUT", "/courses/{id}/practical-checklist", TrainerOnly),
        new("POST", "/courses/{id}/submit", TrainerOnly),
        new("POST", "/courses/{id}/approve", [RoleName.TrainingManager]),
        new("POST", "/courses/{id}/return", [RoleName.TrainingManager]),

        // 6. Training regulation
        new("GET", "/training-regulations", [RoleName.TrainingManager, RoleName.Admin]),
        new("POST", "/training-regulations", [RoleName.TrainingManager]),
        new("PUT", "/training-regulations/{id}", [RoleName.TrainingManager]),

        // 7. Classes, sessions and attendance
        new("GET", "/training-needs", [RoleName.Trainer, RoleName.TrainingManager]),
        new("GET", "/training-classes", TrainerOnly),
        new("POST", "/training-classes", TrainerOnly),
        new("GET", "/training-classes/{id}", TrainerOnly),
        new("PUT", "/training-classes/{id}", TrainerOnly),
        new("POST", "/training-classes/{id}/sessions", TrainerOnly),
        new("POST", "/training-classes/{id}/open", TrainerOnly),
        new("POST", "/training-classes/{id}/close", TrainerOnly),
        new("PUT", "/training-sessions/{id}", TrainerOnly),
        new("DELETE", "/training-sessions/{id}", TrainerOnly),
        new("GET", "/training-sessions/{id}/attendance", TrainerOnly),
        new("PUT", "/training-sessions/{id}/attendance", TrainerOnly),

        // 8. Learning. "Own" endpoints admit a trainer too: see EnrollmentsController.
        new("GET", "/me/enrollments", Learners),
        new("GET", "/enrollments/{id}", Learners),
        new("GET", "/enrollments/{id}/modules", Learners),
        new("POST", "/enrollments/{id}/modules/{moduleId}/complete", Learners),
        new("GET", "/enrollments/{id}/eligibility", Learners),
        new("POST", "/enrollments/{id}/close", [RoleName.TrainingManager]),
        new("POST", "/enrollments/{id}/reset", [RoleName.TrainingManager]),
        new("GET", "/enrollments/{id}/quiz", Learners),
        new("POST", "/enrollments/{id}/quiz-attempts", Learners),
        new("GET", "/enrollments/{id}/quiz-attempts", Learners),
        new("POST", "/enrollments/{id}/practical-evaluation", TrainerOnly),
        new("GET", "/me/certificates", Learners),
        new("GET", "/certificates/{id}", [RoleName.Trainee, RoleName.Trainer, RoleName.QualityAuditor]),

        // 9. Sales capture
        new("GET", "/sales", [RoleName.BranchManager, RoleName.RdManager, RoleName.QualityAuditor]),
        new("GET", "/sales/drinks", [RoleName.BranchManager]),
        new("POST", "/sales", [RoleName.BranchManager]),
        new("PUT", "/sales/{id}", [RoleName.BranchManager]),
        new("POST", "/sales/import", [RoleName.BranchManager]),
        new("GET", "/sales/import/{jobId}", [RoleName.BranchManager]),
        new("GET", "/sales/aggregate", [RoleName.RdManager, RoleName.TrainingManager]),

        // 10. Pilot and rollout
        new("GET", "/pilots", Manager),
        new("POST", "/pilots", Manager),
        new("GET", "/pilots/{id}", [RoleName.RdManager, RoleName.BranchManager]),
        new("PUT", "/pilots/{id}", Manager),
        new("GET", "/pilots/{id}/readiness", Manager),
        new("POST", "/pilots/{id}/start", Manager),
        new("POST", "/pilots/{id}/cancel", Manager),
        new("POST", "/pilots/{id}/branches/{branchId}/go-live", Manager),
        new("GET", "/pilots/{id}/evaluation", Manager),
        new("POST", "/pilots/{id}/decision", Manager),
        new("GET", "/branch-launch-status", [RoleName.RdManager, RoleName.BranchManager]),
        new("POST", "/branch-launch-status/{id}/withdraw", Manager),

        // 12. Dashboards
        new("GET", "/dashboards/training-progress",
            [RoleName.Trainer, RoleName.RdManager, RoleName.BranchManager, RoleName.QualityAuditor]),
        new("GET", "/dashboards/branch-performance",
            [RoleName.RdManager, RoleName.BranchManager, RoleName.QualityAuditor]),
    ];
}
