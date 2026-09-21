using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Derives an execution plan from ANY prompt: segments the prompt into
/// independently evaluable work units (numbered items, bullets, headers,
/// bracketed finding refs, sentence groups as fallback), estimates each
/// unit's depth/cost, then decides the parallel width and balances units
/// into waves so no thread idles behind a heavy unit.
/// </summary>
public sealed partial class WorkPlanner
{
    [GeneratedRegex(@"^\s*\d+[\.\)]\s+", RegexOptions.Multiline)]
    private static partial Regex NumberedItemRegex();

    [GeneratedRegex(@"^\s*[-*•]\s+", RegexOptions.Multiline)]
    private static partial Regex BulletItemRegex();

    [GeneratedRegex(@"^#{1,3}\s+", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\[F\d{3,}\]|\[[0-9A-F]{16}\]")]
    private static partial Regex FindingRefRegex();

    [GeneratedRegex(@"`([^`\n]+)`|([\w./\\-]+\.(?:cs|html|js|ts|py|json|css|sql|razor|md)\b)")]
    private static partial Regex FileRefRegex();

    private static readonly string[] DepthSignals =
    [
        "refactor", "migration", "database", "algorithm", "concurrent", "parallel",
        "security", "schema", "pipeline", "architecture", "optimize", "validation",
        "regression", "audit", "integrity", "workflow", "integration"
    ];

    public ExecutionPlan Plan(string prompt, EvaluationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var units = ExtractUnits(prompt);
        if (units.Count == 0)
            throw new ArgumentException("The prompt does not contain any evaluable work.");

        var width = Math.Clamp(units.Count, 1, Math.Max(1, options.MaxThreads));
        var waves = BalanceWaves(units, width);

        return new ExecutionPlan
        {
            PromptPreview = prompt.Length <= 120 ? prompt : prompt[..117] + "…",
            Units = units,
            Width = width,
            Waves = waves,
            TotalCost = units.Sum(u => u.Cost),
            AverageDepth = units.Average(u => u.Depth)
        };
    }

    // ---------- segmentation ----------

    private static List<WorkUnit> ExtractUnits(string prompt)
    {
        var units = new List<(string Text, string Source)>();

        // 1) strongest signal: numbered items ("1. ...", "2. ...") — the canonical requirement list
        var numbered = SplitBy(prompt, NumberedItemRegex(), "numbered item");
        if (numbered.Count >= 2) units.AddRange(numbered);

        // 2) explicit finding references ([F001], [AB12CD34...]) even when prose-wrapped
        var findingRefs = FindingRefRegex().Matches(prompt)
            .Select(m => m.Value).Distinct().ToList();
        if (units.Count == 0 && findingRefs.Count >= 2)
            units.AddRange(findingRefs.Select(fp =>
                (SplitAround(prompt, fp), $"finding reference {fp}")));

        // 3) bullets
        if (units.Count == 0)
        {
            var bullets = SplitBy(prompt, BulletItemRegex(), "bullet");
            if (bullets.Count >= 2) units.AddRange(bullets);
        }

        // 4) markdown headers → section units
        if (units.Count == 0)
        {
            var sections = SplitBy(prompt, HeaderRegex(), "section");
            if (sections.Count >= 2) units.AddRange(sections);
        }

        // 5) fallback: sentence-group chunks (~2 sentences or 60 words) so ANY text is evaluable
        if (units.Count == 0)
            units.AddRange(ChunkSentences(prompt).Select(t => (t, "sentence group")));

        // build units with depth estimation
        var result = new List<WorkUnit>();
        for (var i = 0; i < units.Count; i++)
        {
            var (text, source) = units[i];
            var clean = text.Trim();
            if (clean.Length == 0) continue;

            var files = FileRefRegex().Matches(clean)
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var depth = EstimateDepth(clean, files.Count);
            result.Add(new WorkUnit
            {
                Id = $"W{i + 1:D3}",
                Order = i + 1,
                Text = clean,
                FileTargets = files,
                Depth = depth,
                Cost = 10 + depth * 10 + files.Count * 5 + Math.Min(clean.Length / 100, 5) * 4,
                Source = source
            });
        }
        return result;
    }

    /// <summary>Depth 1 (trivial) … 5 (deep): length, file spread and hard-domain signals.</summary>
    private static int EstimateDepth(string text, int fileCount)
    {
        var depth = 1;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (words > 40) depth++;
        if (words > 120) depth++;
        if (fileCount > 1) depth++;
        var lower = text.ToLowerInvariant();
        if (DepthSignals.Count(s => lower.Contains(s)) >= 2) depth++;
        return Math.Clamp(depth, 1, 5);
    }

    private static List<(string Text, string Source)> SplitBy(string text, Regex marker, string sourceLabel)
    {
        var matches = marker.Matches(text);
        if (matches.Count == 0) return [];
        var parts = new List<(string, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            // include the marker line itself minus the marker prefix
            var chunk = text[start..end];
            var trimmed = marker.Replace(chunk, string.Empty, 1);
            parts.Add((trimmed, sourceLabel));
        }
        // prose before the first marker becomes the zeroth unit (context), when substantial
        var head = text[..matches[0].Index].Trim();
        if (head.Length > 40) parts.Insert(0, (head, "preamble"));
        return parts;
    }

    private static string SplitAround(string prompt, string token)
    {
        var idx = prompt.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return token;
        var start = Math.Max(0, idx - 120);
        var len = Math.Min(prompt.Length - start, 360);
        return prompt.Substring(start, len).Trim();
    }

    private static IEnumerable<string> ChunkSentences(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?:;])\s+")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var chunk = new List<string>();
        var words = 0;
        foreach (var s in sentences)
        {
            chunk.Add(s);
            words += s.Split(' ').Length;
            if (words >= 60) { yield return string.Join(' ', chunk); chunk.Clear(); words = 0; }
        }
        if (chunk.Count > 0) yield return string.Join(' ', chunk);
    }

    // ---------- wave balancing ----------

    /// <summary>
    /// Longest-processing-time-first bin packing: sort by cost desc, assign each unit
    /// to the currently-cheapest wave of <paramref name="width"/> waves → balanced waves.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> BalanceWaves(IReadOnlyList<WorkUnit> units, int width)
    {
        if (width <= 1) return [units.Select(u => u.Id).ToList()];

        var order = units.OrderByDescending(u => u.Cost).ToList();
        var bins = Enumerable.Range(0, width).Select(_ => new Bin()).ToArray();

        foreach (var u in order)
        {
            var lightest = bins.MinBy(b => b.Load)!;
            lightest.Items.Add(u.Id);
            lightest.Load += u.Cost;    // reference type: the load update persists for the next MinBy
        }

        return bins
            .Where(b => b.Items.Count > 0)
            .OrderByDescending(b => b.Load)
            .Select(b => (IReadOnlyList<string>)b.Items)
            .ToList();
    }

    private sealed class Bin
    {
        public int Load;
        public List<string> Items = [];
    }
}
