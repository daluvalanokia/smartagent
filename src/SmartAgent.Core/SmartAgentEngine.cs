using SmartAgent.Domain;
using SmartAgent.Integrations.Reasoning;
using SmartAgent.SourceProviders;

namespace SmartAgent.Core;

/// <summary>
/// Orchestrates one run: fetch source → analyze → match continuity history →
/// enrich via reasoning sites → prioritize (with escalation) → scope →
/// build the consolidated chained prompt → AUTOMATICALLY review the prompt and
/// split it into parallel work-unit threads, process them concurrently, and
/// consolidate → commit run history. Every prompt resolves through the parallel
/// pipeline — there is no opt-in.
/// </summary>
public sealed class SmartAgentEngine(
    IEnumerable<ISourceProvider> sourceProviders,
    HeuristicAnalyzer analyzer,
    IReasoningService reasoningService,
    Prioritizer prioritizer,
    PromptBuilder promptBuilder,
    ContinuityRegistry continuityRegistry,
    PromptEvaluator promptEvaluator)
{
    public async Task<PromptRunResult> RunAsync(SourceRequest request, RunOptions options, CancellationToken ct = default)
    {
        var provider = sourceProviders.FirstOrDefault(p => p.SourceType == request.SourceType)
            ?? throw new NotSupportedException($"No source provider registered for {request.SourceType}.");

        var started = DateTimeOffset.UtcNow;
        var warnings = new List<string>();

        var snapshot = await provider.FetchAsync(request with { CancellationToken = ct });

        var runId = Guid.NewGuid();
        var targetKey = ContinuityRegistry.DeriveTargetKey(snapshot);
        var findings = analyzer.Analyze(snapshot);
        EvaluationReport? parallelReport = null;

        // CI/CD continuity: stamp findings with cross-run state and build the diff
        var continuity = continuityRegistry.MatchAndBeginRun(targetKey, snapshot.SourceName, findings);

        var prompts = new List<GeneratedPrompt>();
        if (findings.Count > 0)
        {
            warnings.AddRange(await reasoningService.EnrichAsync(findings, snapshot, ct));

            var scoped = prioritizer.Scope(findings, options);
            var prompt = await promptBuilder.BuildAsync(snapshot, scoped, options, continuity, runId, ct);
            prompts.Add(prompt);

            // AUTOMATIC PARALLEL RESOLUTION: review the prompt, split it into work-unit
            // threads, process them on parallel lanes, consolidate. Applies to every prompt.
            parallelReport = await promptEvaluator.EvaluateAsync(prompt.Prompt,
                new EvaluationOptions
                {
                    MaxThreads = options.MaxThreads ?? EvaluationOptions.Default.MaxThreads,
                    UnitTimeout = EvaluationOptions.Default.UnitTimeout,
                    RetriesPerUnit = EvaluationOptions.Default.RetriesPerUnit
                }, null, ct);
            if (parallelReport.UnitsFailed > 0)
                warnings.Add($"parallel evaluation: {parallelReport.UnitsFailed} thread unit(s) failed and were reported, not swallowed.");

            continuityRegistry.CommitRun(targetKey, runId, prompt.Title);
        }
        else
        {
            continuityRegistry.CommitRun(targetKey, runId, null);
        }

        return new PromptRunResult
        {
            RunId = runId,
            StartedUtc = started,
            Snapshot = snapshot,
            AllFindings = findings,
            Prompts = prompts,
            TargetKey = targetKey,
            Continuity = continuity,
            Warnings = warnings,
            ParallelReport = parallelReport
        };
    }
}
