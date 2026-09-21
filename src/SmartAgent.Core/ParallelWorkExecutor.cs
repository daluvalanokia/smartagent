using System.Collections.Concurrent;
using System.Diagnostics;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> with bounded parallelism. The plan's
/// waves are parallel LANES: all lanes start together as Task-thread workers
/// (lane count = the plan's width), and each lane processes its units
/// sequentially. LPT-balanced lanes equalize thread finish times.
/// Per-unit timeout, bounded retries and failure tolerance — a failing unit
/// is recorded, never kills the run. Thread-safe by construction: results
/// land in a concurrent dictionary keyed by unit id.
/// </summary>
public sealed class ParallelWorkExecutor(IWorkUnitEvaluator evaluator)
{
    public async Task<Dictionary<string, UnitEvaluation>> ExecuteAsync(
        ExecutionPlan plan,
        EvaluationOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var results = new ConcurrentDictionary<string, UnitEvaluation>();

        progress?.Report($"starting {plan.Waves.Count} parallel lane(s) on up to {plan.Width} thread(s) for {plan.Units.Count} unit(s)");

        var lanes = plan.Waves.Select(lane => RunLaneAsync(plan, lane, results, options, ct));
        await Task.WhenAll(lanes);   // all lanes run concurrently; each lane drains its queue

        return results.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunLaneAsync(ExecutionPlan plan, IReadOnlyList<string> lane,
        ConcurrentDictionary<string, UnitEvaluation> results, EvaluationOptions options, CancellationToken ct)
    {
        foreach (var unitId in lane)
        {
            ct.ThrowIfCancellationRequested();
            await RunUnitAsync(plan, unitId, results, options, ct);
        }
    }

    private async Task RunUnitAsync(ExecutionPlan plan, string unitId,
        ConcurrentDictionary<string, UnitEvaluation> results, EvaluationOptions options, CancellationToken ct)
    {
        var unit = plan.Units.First(u => u.Id.Equals(unitId, StringComparison.OrdinalIgnoreCase));
        var sw = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= options.RetriesPerUnit + 1; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.UnitTimeout);

                var result = await evaluator.EvaluateAsync(unit, timeout.Token);
                results[unit.Id] = new UnitEvaluation
                {
                    UnitId = result.UnitId, Failed = result.Failed, Error = result.Error,
                    Complexity = result.Complexity, Effort = result.Effort, Risk = result.Risk,
                    Findings = result.Findings, SuggestedActions = result.SuggestedActions,
                    Attempts = attempt, ElapsedMs = sw.ElapsedMilliseconds
                };
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // unit timed out — fall through to the next attempt
            }
            catch (Exception ex)
            {
                if (attempt > options.RetriesPerUnit)
                {
                    results[unit.Id] = new UnitEvaluation
                    {
                        UnitId = unit.Id, Failed = true,
                        Error = ex.Message, Attempts = attempt, ElapsedMs = sw.ElapsedMilliseconds
                    };
                    return;
                }
            }
        }

        // retries exhausted on timeouts
        results[unit.Id] = new UnitEvaluation
        {
            UnitId = unit.Id, Failed = true,
            Error = $"Timed out after {options.RetriesPerUnit + 1} attempt(s) (limit {options.UnitTimeout.TotalSeconds:F0}s)",
            Attempts = options.RetriesPerUnit + 1, ElapsedMs = sw.ElapsedMilliseconds
        };
    }
}
