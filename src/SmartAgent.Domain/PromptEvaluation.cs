namespace SmartAgent.Domain;

/// <summary>Options that bound a parallel prompt evaluation.</summary>
public sealed class EvaluationOptions
{
    /// <summary>Upper bound on concurrent evaluation threads (width).</summary>
    public int MaxThreads { get; init; } = Math.Max(2, Environment.ProcessorCount);
    /// <summary>Per-unit timeout.</summary>
    public TimeSpan UnitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Retries per failed unit before recording the failure.</summary>
    public int RetriesPerUnit { get; init; } = 1;
    public static EvaluationOptions Default => new();
}

/// <summary>One independently evaluable piece of a prompt, produced by the planner.</summary>
public sealed class WorkUnit
{
    public required string Id { get; init; }
    /// <summary>Ordinal position in the original prompt.</summary>
    public int Order { get; init; }
    public required string Text { get; init; }
    /// <summary>File paths referenced by this unit, if any.</summary>
    public IReadOnlyList<string> FileTargets { get; init; } = [];
    /// <summary>Depth 1 (trivial) … 5 (deep/complex) — drives cost and wave balancing.</summary>
    public int Depth { get; init; }
    /// <summary>Relative execution cost estimate (depth-weighted).</summary>
    public int Cost { get; init; }
    /// <summary>How this unit was extracted from the prompt.</summary>
    public required string Source { get; init; }
}

/// <summary>The automatically derived execution plan for a prompt.</summary>
public sealed class ExecutionPlan
{
    public required string PromptPreview { get; init; }
    public IReadOnlyList<WorkUnit> Units { get; init; } = [];
    /// <summary>Threads that will actually be used: min(units, MaxThreads).</summary>
    public int Width { get; init; }
    /// <summary>LPT-balanced parallel lanes: all lanes run concurrently (lane count = width); units within a lane run sequentially.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Waves { get; init; } = [];
    public int TotalCost { get; init; }
    public double AverageDepth { get; init; }
    public DateTimeOffset PlannedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Result of evaluating one work unit.</summary>
public sealed class UnitEvaluation
{
    public required string UnitId { get; init; }
    public bool Failed { get; init; }
    public string? Error { get; init; }
    /// <summary>Complexity 1 (trivial) … 10 (heavy).</summary>
    public int Complexity { get; init; }
    /// <summary>Estimated effort in arbitrary units.</summary>
    public int Effort { get; init; }
    public string Risk { get; init; } = "Low";
    public IReadOnlyList<string> Findings { get; init; } = [];
    public IReadOnlyList<string> SuggestedActions { get; init; } = [];
    public int Attempts { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>Consolidated outcome of a parallel prompt evaluation.</summary>
public sealed class EvaluationReport
{
    public required ExecutionPlan Plan { get; init; }
    public IReadOnlyList<UnitEvaluation> Results { get; init; } = [];
    public int UnitsEvaluated { get; init; }
    public int UnitsFailed { get; init; }
    public int WavesExecuted { get; init; }
    /// <summary>True when every unit produced (or failed-and-accounted) a result — scope satisfied.</summary>
    public bool ScopeSatisfied { get; init; }
    public int TotalEffort { get; init; }
    public string DominantRisk { get; init; } = "Low";
    public string Summary { get; init; } = string.Empty;
    public long TotalElapsedMs { get; init; }
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;
}
