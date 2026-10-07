using BrewForge.Domain.Common;
using Npgsql;

namespace BrewForge.Infrastructure.Persistence;

/// <summary>
/// Turns a constraint or trigger rejection from PostgreSQL into the same
/// refusal the domain would have raised. The domain guards come first; this
/// exists so that a rule the database enforces (a race between two requests,
/// for instance) still reaches the client as a clean 409 naming the rule, and
/// never as a 500.
/// </summary>
public static class PostgresErrorTranslator
{
    private static readonly Dictionary<string, (string Rule, string Message, string? Code)> Constraints = new()
    {
        ["ux_recipe_one_released"] =
            ("BR-02", "The recipe already has a released version.", null),
        ["recipe_version_recipe_id_version_no_key"] =
            ("BR-03", "That version number has already been used for this recipe.", null),
        ["recipe_version_check"] =
            ("BR-12", "The approver may not be the author of the draft.", ErrorCodes.ApproverIsAuthor),
        ["step_dependency_check"] =
            ("BR-09", "A step may not depend on itself.", ErrorCodes.CircularDependency),
        ["ux_course_one_per_version"] =
            ("BR-18", "A course already exists for this recipe version.", null),
        ["course_module_course_id_module_type_key"] =
            ("BR-29", "A course holds each of the seven module types exactly once.", null),
        ["course_module_course_id_module_order_key"] =
            ("BR-29", "A course holds each of the seven module positions exactly once.", null),
        ["course_module_module_order_check"] =
            ("BR-29", "A course has exactly seven modules.", null),
        ["sales_record_branch_id_recipe_id_trading_date_key"] =
            ("BR-25", "A sales record already exists for this drink, branch and day.", ErrorCodes.ImportDuplicateDay),
        ["ux_pilot_active_version"] =
            ("BR-28", "This recipe version already has a pilot in DRAFT or RUNNING state.", null),
    };

    public static DomainException? Translate(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        if (postgres is null) return null;

        if (postgres.ConstraintName is { } name && Constraints.TryGetValue(name, out var known))
        {
            return DomainException.RuleViolation(known.Rule, known.Message, known.Code);
        }

        return postgres.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation =>
                DomainException.RuleViolation("UNIQUE", "A record with the same key already exists.",
                    details: new ErrorDetail(postgres.ConstraintName ?? "key", "already exists")),
            PostgresErrorCodes.ForeignKeyViolation =>
                DomainException.RuleViolation("REFERENCE",
                    "The record refers to, or is referred to by, another record that prevents this change.",
                    details: new ErrorDetail(postgres.ConstraintName ?? "reference", "foreign key violation")),
            PostgresErrorCodes.CheckViolation =>
                DomainException.Validation("A value is outside the range the schema allows.",
                    new ErrorDetail(postgres.ConstraintName ?? "check", "check constraint violation")),
            // RAISE EXCEPTION from the triggers of 001_initial_schema.sql.
            PostgresErrorCodes.RaiseException when postgres.MessageText.Contains("BR-01", StringComparison.Ordinal) =>
                DomainException.RuleViolation("BR-01",
                    "A released recipe version cannot be modified. Create a new version instead.",
                    ErrorCodes.ReleasedVersionImmutable),
            PostgresErrorCodes.RaiseException when postgres.MessageText.Contains("append-only", StringComparison.Ordinal) =>
                DomainException.RuleViolation("AUDIT", "The audit log is append-only."),
            _ => null,
        };
    }
}
