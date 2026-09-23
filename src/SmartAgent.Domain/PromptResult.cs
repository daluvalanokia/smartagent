namespace SmartAgent.Domain;

/// <summary>One consolidated, scope-limited prompt produced by a run.</summary>
public sealed class GeneratedPrompt
{
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<Finding> TargetFindings { get; init; }
    /// <summary>Which integration produced the final wording.</summary>
    public required string Composer { get; init; }
}

/// <summary>The full result of one SmartAgent run.</summary>
public sealed class PromptRunResult
{
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required SourceSnapshot Snapshot { get; init; }
    public required IReadOnlyList<Finding> AllFindings { get; init; }
    public required IReadOnlyList<GeneratedPrompt> Prompts { get; init; }
    /// <summary>Stable cross-run identity of the analyzed target.</summary>
    public string TargetKey { get; init; } = string.Empty;
    /// <summary>Cross-run continuity diff for this target (CI/CD coordination).</summary>
    public ContinuityReport? Continuity { get; init; }
    /// <summary>1-based run number for this target (from continuity history).</summary>
    public int RunNumber => Continuity?.RunNumber ?? 1;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Automatic parallel-thread report: EVERY built prompt is reviewed, split into
    /// work units, processed on parallel lanes, and consolidated here — no opt-in needed.</summary>
    public EvaluationReport? ParallelReport { get; init; }
}
