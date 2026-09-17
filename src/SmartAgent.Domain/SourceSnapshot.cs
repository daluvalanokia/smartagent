namespace SmartAgent.Domain;

/// <summary>A single file captured from a source (zip, GitHub or website).</summary>
public sealed class SourceFile
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public long SizeBytes => Content.Length;
}

/// <summary>
/// A normalized, size-capped snapshot of the target app gathered from one of
/// the supported sources. All downstream analysis works only against this.
/// </summary>
public sealed class SourceSnapshot
{
    public required SourceType SourceType { get; init; }
    public required string SourceName { get; init; }
    public required string SourceDetail { get; init; }
    public required IReadOnlyList<SourceFile> Files { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int TotalFiles => Files.Count;
    public long TotalSizeBytes => Files.Sum(f => f.SizeBytes);
}

/// <summary>Input handed to a source provider.</summary>
public sealed record SourceRequest
{
    public SourceType SourceType { get; init; }
    /// <summary>ZIP stream (zip source) or raw URL (GitHub/website).</summary>
    public Stream? ZipStream { get; init; }
    public string? Url { get; init; }
    /// <summary>Optional GitHub PAT for private repos.</summary>
    public string? GitHubToken { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
