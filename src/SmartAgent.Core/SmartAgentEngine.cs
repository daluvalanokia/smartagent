using SmartAgent.Domain;
using SmartAgent.Integrations.Reasoning;
using SmartAgent.SourceProviders;

namespace SmartAgent.Core;

/// <summary>
/// Orchestrates one run: fetch source → analyze → enrich via reasoning sites
/// → prioritize → scope → build the consolidated prompt(s).
/// </summary>
public sealed class SmartAgentEngine(
    IEnumerable<ISourceProvider> sourceProviders,
    HeuristicAnalyzer analyzer,
    IReasoningService reasoningService,
    Prioritizer prioritizer,
    PromptBuilder promptBuilder)
{
    public async Task<PromptRunResult> RunAsync(SourceRequest request, RunOptions options, CancellationToken ct = default)
    {
        var provider = sourceProviders.FirstOrDefault(p => p.SourceType == request.SourceType)
            ?? throw new NotSupportedException($"No source provider registered for {request.SourceType}.");

        var started = DateTimeOffset.UtcNow;
        var warnings = new List<string>();

        var snapshot = await provider.FetchAsync(request with { CancellationToken = ct });

        var findings = analyzer.Analyze(snapshot);
        if (findings.Count == 0)
        {
            return new PromptRunResult
            {
                RunId = Guid.NewGuid(),
                StartedUtc = started,
                Snapshot = snapshot,
                AllFindings = findings,
                Prompts = [],
                Warnings = warnings
            };
        }

        warnings.AddRange(await reasoningService.EnrichAsync(findings, snapshot, ct));

        var scoped = prioritizer.Scope(findings, options);
        var prompt = await promptBuilder.BuildAsync(snapshot, scoped, options, ct);

        return new PromptRunResult
        {
            RunId = Guid.NewGuid(),
            StartedUtc = started,
            Snapshot = snapshot,
            AllFindings = findings,
            Prompts = [prompt],
            Warnings = warnings
        };
    }
}
