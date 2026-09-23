namespace SmartAgent.Domain;

/// <summary>
/// Options that constrain a single run. They enforce the "limited scope"
/// contract: one run never tries to fix everything, only its top-priority slice.
/// </summary>
public sealed class RunOptions
{
    /// <summary>Max number of findings consolidated into this run's prompt(s).</summary>
    public int ScopeLimit { get; init; } = 3;
    public FocusArea Focus { get; init; } = FocusArea.Balanced;
    /// <summary>Free-text context the operator wants considered.</summary>
    public string? Notes { get; init; }
    /// <summary>Upper bound on parallel threads for the automatic prompt split. Default: processor count (min 2).</summary>
    public int? MaxThreads { get; init; }

    public static RunOptions Default => new();
}
