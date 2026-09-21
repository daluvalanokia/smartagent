using System.Text;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Merges parallel per-unit results into one consolidated report and checks
/// that the prompt's scope was satisfied: every planned unit must have a
/// result (a recorded failure is accounted for; a MISSING result is not).
/// </summary>
public sealed class PromptConsolidator
{
    public EvaluationReport Consolidate(ExecutionPlan plan,
        IReadOnlyDictionary<string, UnitEvaluation> results, long totalElapsedMs)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var ordered = plan.Units
            .Select(u => results.TryGetValue(u.Id, out var r) ? r : null)
            .ToList();

        var missing = plan.Units.Count(u => !results.ContainsKey(u.Id));
        var failed = ordered.Count(r =>r is { Failed: true });
        var evaluated = ordered.Count(r => r is { Failed: false });

        var ok = ordered.Where(r => r is { Failed: false }).ToList();
        var totalEffort = ok.Sum(r => r.Effort);

        var riskRank = new Dictionary<string, int> { ["High"] = 3, ["Medium"] = 2, ["Low"] = 1 };
        var dominantRisk = ok.Count == 0 ? "Low"
            : ok.GroupBy(r => r.Risk).OrderByDescending(g => g.Count())
                .ThenByDescending(g => riskRank[g.Key!]).First().Key!;

        var scopeSatisfied = missing == 0;   // failures are accounted, gaps are not

        var sb = new StringBuilder();
        sb.AppendLine($"Evaluated {plan.Units.Count} work unit(s) across {plan.Waves.Count} wave(s) with {plan.Width} thread(s).");
        sb.AppendLine($"Succeeded: {evaluated}; failed: {failed}; missing: {missing}. Total effort: {totalEffort}. Dominant risk: {dominantRisk}.");
        if (failed > 0)
            sb.AppendLine($"Failed unit(s) require re-evaluation: {string.Join(", ", ordered.Where(r => r is { Failed: true }).Select(r => r!.UnitId))}.");
        if (!scopeSatisfied)
            sb.AppendLine($"Scope NOT satisfied: {missing} unit(s) produced no result.");
        else
            sb.AppendLine("Scope satisfied: every unit of the prompt was evaluated and consolidated.");

        return new EvaluationReport
        {
            Plan = plan,
            Results = ordered.Where(r => r is not null).Select(r => r!).ToList(),
            UnitsEvaluated = evaluated,
            UnitsFailed = failed,
            WavesExecuted = plan.Waves.Count,
            ScopeSatisfied = scopeSatisfied,
            TotalEffort = totalEffort,
            DominantRisk = dominantRisk,
            Summary = sb.ToString().Trim(),
            TotalElapsedMs = totalElapsedMs
        };
    }
}
