using System.Diagnostics;
using System.Text;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Facade of the parallel prompt-evaluation algorithm:
///   evaluate prompt → WorkPlanner derives scope (units), depth (per unit)
///   and width (threads) → ParallelWorkExecutor splits the work into balanced
///   waves and runs them on multiple threads → PromptConsolidator merges the
///   per-unit results into one report and verifies the prompt scope was
///   fully satisfied.
/// </summary>
public sealed class PromptEvaluator(
    WorkPlanner planner,
    ParallelWorkExecutor executor,
    IWorkUnitEvaluator unitEvaluator,
    PromptConsolidator consolidator)
{
    /// <summary>Plan only (no execution): how would this prompt be split?</summary>
    public ExecutionPlan Plan(string prompt, EvaluationOptions? options = null) =>
        planner.Plan(prompt, options ?? EvaluationOptions.Default);

    /// <summary>Plan + parallel execution + consolidation. Never throws on unit failures; they are reported.</summary>
    public async Task<EvaluationReport> EvaluateAsync(string prompt, EvaluationOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var opts = options ?? EvaluationOptions.Default;
        var plan = planner.Plan(prompt, opts);

        var sw = Stopwatch.StartNew();
        var results = await executor.ExecuteAsync(plan, opts, progress, ct);
        sw.Stop();

        return consolidator.Consolidate(plan, results, sw.ElapsedMilliseconds);
    }
}
