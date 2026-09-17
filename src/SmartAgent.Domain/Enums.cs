namespace SmartAgent.Domain;

/// <summary>Where the analyzed app source comes from.</summary>
public enum SourceType
{
    /// <summary>a. Source uploaded as a ZIP archive.</summary>
    ZipArchive = 0,

    /// <summary>b. Source pulled from a GitHub repository.</summary>
    GitHub = 1,

    /// <summary>c. App inspected from a live website URL.</summary>
    Website = 2
}

/// <summary>What a finding is about.</summary>
public enum FindingCategory
{
    /// <summary>Improves app functionality (features, UX, robustness).</summary>
    FunctionalityImprovement = 0,

    /// <summary>Erratic behavior: crashes, races, swallowed errors, unstable flows.</summary>
    ErraticBehavior = 1,

    /// <summary>Performance related behavior.</summary>
    Performance = 2,

    /// <summary>Security related behavior.</summary>
    Security = 3,

    /// <summary>General code quality / maintainability.</summary>
    Maintainability = 4
}

/// <summary>Run focus set by the operator; boosts matching findings.</summary>
public enum FocusArea
{
    /// <summary>Weight functionality and erratic behavior equally.</summary>
    Balanced = 0,

    /// <summary>Prioritize new/improved functionality.</summary>
    Functionality = 1,

    /// <summary>Prioritize fixing erratic/unstable behavior.</summary>
    Stability = 2
}

public enum Severity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
