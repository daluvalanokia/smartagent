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
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
