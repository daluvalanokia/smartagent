using System.Text.Json.Serialization;

namespace SmartAgent.Domain;

/// <summary>
/// Cross-run lifecycle of a finding for a given target. A finding that shows
/// up again in a later run is <see cref="Persisting"/>; one that was marked
/// fixed via feedback but reappears is a <see cref="Regression"/>.
/// </summary>
public enum ContinuityStatus
{
    /// <summary>First time this finding is observed for the target.</summary>
    New = 0,

    /// <summary>Seen in an earlier run and still present.</summary>
    Persisting = 1,

    /// <summary>Was reported fixed via feedback but is present again.</summary>
    Regression = 2
}

/// <summary>Operator/CI feedback recorded against a tracked finding.</summary>
public enum FeedbackStatus
{
    None = 0,

    /// <summary>The fix was applied; the next run should verify it stays fixed.</summary>
    Fixed = 1,

    /// <summary>Accepted as-is; suppress from future prompts for this target.</summary>
    WontFix = 2,

    /// <summary>A previous "fixed" claim failed verification; keep escalating.</summary>
    FailedVerification = 3
}

/// <summary>A finding as remembered across runs for one target (persisted).</summary>
public sealed class TrackedFinding
{
    public required string Fingerprint { get; set; }
    public required string Rule { get; set; }
    public required string Title { get; set; }
    public required string FilePath { get; set; }
    public int LastLine { get; set; }
    public FeedbackStatus FeedbackStatus { get; set; } = FeedbackStatus.None;
    public string? FeedbackNote { get; set; }
    /// <summary>How many runs in a row (including the first sighting) this finding appeared.</summary>
    public int Occurrences { get; set; } = 1;
    public int FirstSeenRunNumber { get; set; } = 1;
    public int LastSeenRunNumber { get; set; } = 1;
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-target run history held by the continuity registry (persisted).</summary>
public sealed class TargetHistory
{
    public required string TargetKey { get; set; }
    public string TargetName { get; set; } = "";
    public int RunCount { get; set; }
    public DateTimeOffset LastRunUtc { get; set; }
    public Guid? LastRunId { get; set; }
    public string? LastPromptTitle { get; set; }
    public List<TrackedFinding> Findings { get; set; } = [];
}

/// <summary>
/// The continuity diff produced when a new run is matched against a target's
/// history. Drives the chained prompt sections and the CI/CD gate values.
/// </summary>
public sealed class ContinuityReport
{
    public required string TargetKey { get; init; }
    public string TargetName { get; init; } = "";
    /// <summary>1-based sequence number of this run for the target.</summary>
    public int RunNumber { get; init; }
    public DateTimeOffset RunUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PreviousRunUtc { get; init; }
    public Guid? PreviousRunId { get; init; }
    public string? PreviousPromptTitle { get; init; }

    /// <summary>Findings present now that were never seen before.</summary>
    public IReadOnlyList<TrackedFinding> NewFindings { get; init; } = [];

    /// <summary>Findings seen in earlier runs and still present now.</summary>
    public IReadOnlyList<TrackedFinding> PersistingFindings { get; init; } = [];

    /// <summary>Findings marked Fixed via feedback that reappeared. CI gate value.</summary>
    public IReadOnlyList<TrackedFinding> RegressionFindings { get; init; } = [];

    /// <summary>Findings that vanished this run (candidate resolved; verified when feedback said Fixed).</summary>
    public IReadOnlyList<TrackedFinding> ResolvedSinceLastRun { get; init; } = [];

    /// <summary>Findings suppressed by WontFix feedback (kept out of prompts).</summary>
    public IReadOnlyList<TrackedFinding> SuppressedFindings { get; init; } = [];
}
