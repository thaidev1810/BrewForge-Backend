namespace BrewForge.Domain.Recipes;

/// <summary>The <c>recipe_category</c> enumeration.</summary>
public enum RecipeCategory
{
    Tea,
    Coffee,
    Other,
}

/// <summary>The <c>recipe_origin</c> enumeration. EXISTING skips the pilot (BR-36).</summary>
public enum RecipeOrigin
{
    New,
    Existing,
}

public enum RecipeStatus
{
    Active,
    Discontinued,
}

/// <summary>The <c>version_state</c> enumeration.</summary>
public enum VersionState
{
    Draft,
    Validated,
    Rejected,
    Released,
    Superseded,
}

/// <summary>The <c>dependency_type</c> enumeration.</summary>
public enum DependencyType
{
    /// <summary>The prerequisite step must be finished before this one starts.</summary>
    FinishToStart,

    /// <summary>This step consumes what the prerequisite step produced.</summary>
    RequiresOutput,
}

/// <summary>The <c>check_type</c> enumeration: the three independent validator checks (BR-08).</summary>
public enum CheckType
{
    Equipment,
    Ordering,
    Ingredient,
}
