namespace BrewForge.Domain.Courses;

/// <summary>The <c>course_type</c> enumeration.</summary>
public enum CourseType
{
    Induction,
    Product,
    Equipment,
    Recertification,
}

/// <summary>The <c>course_state</c> enumeration.</summary>
public enum CourseState
{
    Draft,
    PendingApproval,
    Published,
    OutOfDate,
    Archived,
}

/// <summary>The <c>module_type</c> enumeration: the seven modules of a course, in their fixed order (BR-29).</summary>
public enum ModuleType
{
    ProductOverview,
    Ingredients,
    Equipment,
    Sop,
    Technique,
    CommonMistakes,
    ExceptionHandling,
}

/// <summary>The <c>module_source</c> enumeration.</summary>
public enum ModuleSource
{
    /// <summary>Built entirely from the bound recipe version. The trainer cannot edit it (BR-30).</summary>
    Generated,

    /// <summary>Written entirely by the trainer. Regeneration never touches it (BR-30).</summary>
    Authored,

    /// <summary>A generated list (the technique gates) that the trainer writes prose for.</summary>
    Mixed,
}

/// <summary>The <c>module_state</c> enumeration.</summary>
public enum ModuleState
{
    Empty,
    Draft,
    Complete,
    NeedsReview,
}
