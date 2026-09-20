using SmartAgent.Domain;
using SmartAgent.Integrations.Reasoning;
using SmartAgent.SourceProviders;

namespace SmartAgent.Core;

/// <summary>
/// Orchestrates one run: fetch source → analyze → match continuity history →
/// enrich via reasoning sites → prioritize (with escalation) → scope →
/// build the consolidated chained prompt → commit run history.
/// </summary>
public sealed class SmartAgentEngine(
    IEnumerable<ISourceProvider> sourceProviders,
    HeuristicAnalyzer analyzer,
    IReasoningService reasoningService,
    Prioritizer prioritizer,
    PromptBuilder promptBuilder,
    ContinuityRegistry continuityRegistry)
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

        // CI/CD continuity: stamp findings with cross-run state and build the diff
        var continuity = continuityRegistry.MatchAndBeginRun(targetKey, snapshot.SourceName, findings);

        var prompts = new List<GeneratedPrompt>();
        if (findings.Count > 0)
        {
            warnings.AddRange(await reasoningService.EnrichAsync(findings, snapshot, ct));

            var scoped = prioritizer.Scope(findings, options);
            var prompt = await promptBuilder.BuildAsync(snapshot, scoped, options, continuity, runId, ct);
            prompts.Add(prompt);
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
            Warnings = warnings
        };
    }
}
