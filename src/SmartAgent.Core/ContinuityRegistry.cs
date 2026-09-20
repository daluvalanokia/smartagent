using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// CI/CD continuity core: remembers, per target, every finding ever reported
/// and the operator/CI feedback against it. Each new run is matched against
/// that history so prompts chain: new findings are marked NEW, recurring ones
/// escalate, previously-fixed ones that reappear become REGRESSIONS, and
/// vanished ones are reported as resolved. Persisted to disk (JSON) so
/// continuity survives restarts; falls back to in-memory when the store path
/// is unavailable.
/// </summary>
public sealed class ContinuityRegistry
{
    private readonly object _sync = new();
    private readonly string? _storePath;
    private Dictionary<string, TargetHistory> _targets = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public ContinuityRegistry(string? storePath = null)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath) ? null : storePath;
        Load();
    }

    /// <summary>Stable identity for a target: website host, github owner/repo, or archive name.</summary>
    public static string DeriveTargetKey(SourceSnapshot snapshot) => snapshot.SourceType switch
    {
        SourceType.Website => $"site:{snapshot.SourceName.ToLowerInvariant()}",
        SourceType.GitHub => $"github:{snapshot.SourceName.ToLowerInvariant()}",
        _ => $"zip:{snapshot.SourceName.ToLowerInvariant()}"
    };

    public static string FingerprintOf(string targetKey, Finding f) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{targetKey}|{f.FilePath.Trim().ToLowerInvariant()}|{f.Rule}|{f.Title}")))[..16];

    /// <summary>
    /// Matches the run's findings against the target's history, stamps each
    /// finding with its continuity state, and advances the run counter.
    /// Returns the diff that drives the chained prompt and CI gate values.
    /// </summary>
    public ContinuityReport MatchAndBeginRun(string targetKey, string targetName, IReadOnlyList<Finding> findings)
    {
        lock (_sync)
        {
            if (!_targets.TryGetValue(targetKey, out var hist))
            {
                hist = new TargetHistory { TargetKey = targetKey, TargetName = targetName };
                _targets[targetKey] = hist;
            }
            hist.TargetName = targetName;

            var runNumber = hist.RunCount + 1;
            var now = DateTimeOffset.UtcNow;
            var previousRunUtc = hist.RunCount == 0 ? (DateTimeOffset?)null : hist.LastRunUtc;
            var previousRunId = hist.LastRunId;
            var previousPromptTitle = hist.LastPromptTitle;

            var @new = new List<TrackedFinding>();
            var persisting = new List<TrackedFinding>();
            var regressions = new List<TrackedFinding>();
            var suppressed = new List<TrackedFinding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var firstSightingThisRun = new Dictionary<string, ContinuityStatus>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in findings)
            {
                var fp = FingerprintOf(targetKey, f);
                f.Fingerprint = fp;

                var entry = hist.Findings.FirstOrDefault(t => t.Fingerprint.Equals(fp, StringComparison.OrdinalIgnoreCase))
                             ?? hist.Findings.FirstOrDefault(t =>
                                 t.Rule == f.Rule && t.Title == f.Title &&
                                 t.FilePath.Trim().Equals(f.FilePath.Trim(), StringComparison.OrdinalIgnoreCase));

                // within-run duplicate (same rule+file, different line): inherit the first sighting's
                // state; do NOT increment occurrences — persistence is a cross-run concept only
                if (entry is not null && entry.LastSeenRunNumber == runNumber)
                {
                    f.Continuity = firstSightingThisRun[entry.Fingerprint];
                    f.Occurrences = entry.Occurrences;
                    if (entry.FeedbackStatus == FeedbackStatus.WontFix) { f.SuppressFromScope = true; suppressed.Add(entry); }
                    continue;
                }

                if (entry is null)
                {
                    entry = new TrackedFinding
                    {
                        Fingerprint = fp,
                        Rule = f.Rule,
                        Title = f.Title,
                        FilePath = f.FilePath,
                        LastLine = f.Line,
                        FirstSeenRunNumber = runNumber,
                        FirstSeenUtc = now,
                        LastSeenRunNumber = runNumber,
                        LastSeenUtc = now
                    };
                    hist.Findings.Add(entry);
                    f.Continuity = ContinuityStatus.New;
                    f.Occurrences = 1;
                    @new.Add(entry);
                    firstSightingThisRun[entry.Fingerprint] = f.Continuity;
                }
                else
                {
                    entry.Occurrences++;
                    entry.LastLine = f.Line;
                    entry.LastSeenRunNumber = runNumber;
                    entry.LastSeenUtc = now;

                    f.Occurrences = entry.Occurrences;
                    if (entry.FeedbackStatus == FeedbackStatus.Fixed)
                    {
                        // claimed fixed but it is back — highest-severity continuity state
                        f.Continuity = ContinuityStatus.Regression;
                        regressions.Add(entry);
                        firstSightingThisRun[entry.Fingerprint] = f.Continuity;
                    }
                    else
                    {
                        f.Continuity = ContinuityStatus.Persisting;
                        persisting.Add(entry);
                        firstSightingThisRun[entry.Fingerprint] = f.Continuity;
                    }
                }

                if (entry.FeedbackStatus == FeedbackStatus.WontFix)
                {
                    f.SuppressFromScope = true;
                    suppressed.Add(entry);
                }
                seen.Add(entry.Fingerprint);
            }

            // entries that were visible last run but are gone now → resolved (verified when feedback said fixed)
            var resolved = hist.Findings
                .Where(t => !seen.Contains(t.Fingerprint) && t.LastSeenRunNumber == runNumber - 1)
                .ToList();
            foreach (var r in resolved)
            {
                if (r.FeedbackStatus == FeedbackStatus.Fixed) r.FeedbackStatus = FeedbackStatus.None; // verified resolved; reset for any future reappearance
            }

            hist.RunCount = runNumber;
            hist.LastRunUtc = now;

            var report = new ContinuityReport
            {
                TargetKey = targetKey,
                TargetName = targetName,
                RunNumber = runNumber,
                RunUtc = now,
                PreviousRunUtc = previousRunUtc,
                PreviousRunId = previousRunId,
                PreviousPromptTitle = previousPromptTitle,
                NewFindings = @new,
                PersistingFindings = persisting,
                RegressionFindings = regressions,
                ResolvedSinceLastRun = resolved,
                SuppressedFindings = suppressed
            };

            Save();
            return report;
        }
    }

    /// <summary>Records the completed run's identity (id + prompt title) for the next run's chain.</summary>
    public void CommitRun(string targetKey, Guid runId, string? promptTitle)
    {
        lock (_sync)
        {
            if (_targets.TryGetValue(targetKey, out var hist))
            {
                hist.LastRunId = runId;
                hist.LastPromptTitle = promptTitle;
                Save();
            }
        }
    }

    /// <summary>Applies CI/operator feedback for specific finding fingerprints of a target.</summary>
    public int ApplyFeedback(string targetKey, IEnumerable<(string Fingerprint, FeedbackStatus Status, string? Note)> feedback)
    {
        lock (_sync)
        {
            if (!_targets.TryGetValue(targetKey, out var hist)) return 0;
            var applied = 0;
            foreach (var (fp, status, note) in feedback)
            {
                var entry = hist.Findings.FirstOrDefault(t =>
                    t.Fingerprint.Equals(fp, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                entry.FeedbackStatus = status;
                entry.FeedbackNote = note;
                applied++;
            }
            if (applied > 0) Save();
            return applied;
        }
    }

    public TargetHistory? GetHistory(string targetKey)
    {
        lock (_sync) return _targets.TryGetValue(targetKey, out var h) ? Clone(h) : null;
    }

    public IReadOnlyList<TargetHistory> GetAllTargets()
    {
        lock (_sync) return _targets.Values.Select(Clone).ToList();
    }

    private static TargetHistory Clone(TargetHistory h) =>
        JsonSerializer.Deserialize<TargetHistory>(JsonSerializer.Serialize(h, JsonOpts), JsonOpts) ?? h;

    private void Load()
    {
        if (_storePath is null) return;
        try
        {
            if (!File.Exists(_storePath)) return;
            using var stream = File.OpenRead(_storePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, TargetHistory>>(stream, JsonOpts);
            if (loaded is not null) _targets = new Dictionary<string, TargetHistory>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // corrupt/unreadable store → start fresh in memory rather than fail runs
            _targets = new Dictionary<string, TargetHistory>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        if (_storePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_storePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_targets, JsonOpts));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch
        {
            // persistence is best-effort; continuity still works for this process
        }
    }
}
