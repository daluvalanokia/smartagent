namespace SmartAgent.Domain;

/// <summary>
/// One concrete observation about the analyzed app. Findings are scored,
/// prioritized and finally consolidated into the run's prompt(s).
/// </summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required string Rule { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required FindingCategory Category { get; init; }
    public required Severity Severity { get; set; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    /// <summary>The offending code excerpt / evidence.</summary>
    public string Evidence { get; init; } = string.Empty;
    /// <summary>What to change, concretely.</summary>
    public string SuggestedAction { get; init; } = string.Empty;
    /// <summary>Priority score computed by the prioritizer.</summary>
    public double Score { get; set; }
    /// <summary>Extra context contributed by reasoning-site integrations.</summary>
    public string? ReasoningNote { get; set; }
}
