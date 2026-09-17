using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Scores findings and picks the limited, prioritized scope for one run.
/// The output is the heart of the "consolidated and precise, limited scope"
/// contract: only the top <see cref="RunOptions.ScopeLimit"/> findings
/// (after de-duplication) survive into the prompt.
/// </summary>
public sealed class Prioritizer
{
    private static readonly Dictionary<Severity, double> SeverityWeight = new()
    {
        [Severity.Critical] = 10, [Severity.High] = 7, [Severity.Medium] = 4, [Severity.Low] = 1.5
    };

    private static readonly Dictionary<FindingCategory, double> BaseCategoryWeight = new()
    {
        [FindingCategory.ErraticBehavior] = 1.3,      // erratic behavior first by default
        [FindingCategory.FunctionalityImprovement] = 1.15,
        [FindingCategory.Security] = 1.25,
        [FindingCategory.Performance] = 1.0,
        [FindingCategory.Maintainability] = 0.7
    };

    private static readonly Dictionary<FocusArea, IReadOnlyDictionary<FindingCategory, double>> FocusBoost = new()
    {
        [FocusArea.Balanced] = new Dictionary<FindingCategory, double>(),
        [FocusArea.Functionality] = new Dictionary<FindingCategory, double>
        {
            [FindingCategory.FunctionalityImprovement] = 1.5, [FindingCategory.Performance] = 1.2
        },
        [FocusArea.Stability] = new Dictionary<FindingCategory, double>
        {
            [FindingCategory.ErraticBehavior] = 1.6, [FindingCategory.Security] = 1.3
        }
    };

    /// <summary>Scores all findings (sets <see cref="Finding.Score"/>) and returns the scoped top slice.</summary>
    public IReadOnlyList<Finding> Scope(IReadOnlyList<Finding> findings, RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);

        var focus = FocusBoost[options.Focus];

        double Score(Finding f) =>
            SeverityWeight[f.Severity] * BaseCategoryWeight[f.Category]
            * (focus.TryGetValue(f.Category, out var b) ? b : 1.0)
            * (f.ReasoningNote is null ? 1.0 : 1.05);   // reasoning-enriched findings get a nudge

        // de-duplicate identical (rule, file, line) signals from overlapping scans
        var deduped = findings
            .GroupBy(f => (f.Rule, f.FilePath, f.Line))
            .Select(g => g.First())
            .ToList();

        foreach (var f in deduped) f.Score = Score(f);

        return deduped
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .Take(Math.Max(1, options.ScopeLimit))
            .ToList();
    }
}
