using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>Evaluates a single work unit. Implementations may be heuristic, AI-backed, or remote.</summary>
public interface IWorkUnitEvaluator
{
    Task<UnitEvaluation> EvaluateAsync(WorkUnit unit, CancellationToken ct);
}

/// <summary>
/// Deterministic unit evaluator: scores complexity from depth/cost, derives risk from
/// domain signals and file spread, and extracts concrete actions. Deterministic so
/// evaluation never depends on an external provider being available.
/// </summary>
public sealed class HeuristicWorkUnitEvaluator : IWorkUnitEvaluator
{
    private static readonly string[] HighRiskSignals = ["security", "database", "migration", "schema", "regression", "concurrent", "parallel", "audit"];
    private static readonly string[] MediumRiskSignals = ["refactor", "validation", "workflow", "optimize", "integration", "pipeline", "architecture"];

    public Task<UnitEvaluation> EvaluateAsync(WorkUnit unit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var lower = unit.Text.ToLowerInvariant();
        var high = HighRiskSignals.Count(s => lower.Contains(s));
        var medium = MediumRiskSignals.Count(s => lower.Contains(s));

        var risk = high > 0 ? "High" : medium > 0 || unit.FileTargets.Count > 1 ? "Medium" : "Low";
        var complexity = Math.Clamp((int)Math.Round(unit.Cost / 10.0), 1, 10);

        var findings = new List<string>();
        var actions = new List<string>();

        if (unit.FileTargets.Count > 0)
            findings.Add($"Touches {unit.FileTargets.Count} file(s): {string.Join(", ", unit.FileTargets.Take(3))}");
        if (high > 0)
            findings.Add($"Contains {high} high-risk signal(s) ({string.Join(", ", HighRiskSignals.Where(s => lower.Contains(s)).Take(3))})");
        if (unit.Depth >= 4)
            findings.Add($"Deep unit (depth {unit.Depth}) — expect multi-step changes");

        if (unit.FileTargets.Count > 0) actions.Add($"Apply changes in {unit.FileTargets[0]} first; keep other files untouched");
        if (risk == "High") actions.Add("Add regression coverage before merging");
        if (unit.Depth >= 4) actions.Add("Break into sub-steps and verify incrementally");
        if (actions.Count == 0) actions.Add("Straightforward single-file change");

        var result = new UnitEvaluation
        {
            UnitId = unit.Id,
            Complexity = complexity,
            Effort = unit.Cost,
            Risk = risk,
            Findings = findings.Count > 0 ? findings : ["Self-contained unit with no cross-file impact"],
            SuggestedActions = actions,
            Attempts = 1
        };
        return Task.FromResult(result);
    }
}
