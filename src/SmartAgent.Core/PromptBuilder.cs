using System.Text;
using SmartAgent.Domain;
using SmartAgent.Integrations.AI;

namespace SmartAgent.Core;

/// <summary>
/// Composes the final consolidated prompt for the scoped findings. Uses the
/// AI processing provider when available; otherwise falls back to a
/// deterministic template so a run never fails just because AI is offline.
/// </summary>
public sealed class PromptBuilder(IChatCompletionClient chatClient)
{
    public async Task<GeneratedPrompt> BuildAsync(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped, RunOptions options, CancellationToken ct)
    {
        var evidence = RenderEvidence(snapshot, scoped, options);

        if (chatClient.IsConfigured)
        {
            try
            {
                var prompt = await chatClient.CompleteAsync(SystemPrompt, evidence, ct);
                return new GeneratedPrompt
                {
                    Title = BuildTitle(scoped, options),
                    Prompt = prompt.Trim(),
                    TargetFindings = scoped,
                    Composer = "AI processing provider"
                };
            }
            catch (Exception)
            {
                // fall through to deterministic composition
            }
        }

        return new GeneratedPrompt
        {
            Title = BuildTitle(scoped, options),
            Prompt = DeterministicPrompt(snapshot, scoped, options, evidence).ToString(),
            TargetFindings = scoped,
            Composer = "built-in template (AI provider not configured/unavailable)"
        };
    }

    private const string SystemPrompt =
        """
        You are SmartAgent's prompt composer. You receive a scoped set of prioritized findings
        about a target application. Write ONE consolidated engineering prompt that a developer
        (or an AI coding assistant) can execute in a single run.

        Rules:
        - Address ONLY the supplied findings. Never invent new issues or widen the scope.
        - Be concise and precise; no filler, no restating the obvious.
        - Structure: objective (1-2 sentences), scope (bullet list of exact files/lines to touch),
        required changes (numbered, one per finding), acceptance criteria (verifiable checklist).
        - Keep it within roughly 350 words.
        """;

    private static string BuildTitle(IReadOnlyList<Finding> scoped, RunOptions options)
    {
        if (scoped.Count == 0) return "No actionable findings in this scope";
        var dominant = scoped.GroupBy(f => f.Category).OrderByDescending(g => g.Count()).First().Key;
        var area = options.Focus switch
        {
            FocusArea.Functionality => "functionality",
            FocusArea.Stability => "stability",
            _ => "app quality"
        };
        return $"Improve {area}: {dominant switch
        {
            FindingCategory.ErraticBehavior => "erratic behavior",
            FindingCategory.FunctionalityImprovement => "functionality",
            FindingCategory.Performance => "performance",
            FindingCategory.Security => "security",
            _ => "maintainability"
        }} ({scoped.Count} scoped finding(s))";
    }

    private static string RenderEvidence(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped, RunOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Application: {snapshot.SourceName} ({snapshot.SourceType})");
        sb.AppendLine($"Source detail: {snapshot.SourceDetail}");
        sb.AppendLine($"Run focus: {options.Focus}; scope limit: {options.ScopeLimit}");
        if (!string.IsNullOrWhiteSpace(options.Notes)) sb.AppendLine($"Operator notes: {options.Notes}");
        sb.AppendLine();
        sb.AppendLine("Prioritized findings (highest priority first):");
        foreach (var f in scoped)
        {
            sb.AppendLine($"- [{f.Id}] ({f.Category}, {f.Severity}, score {f.Score:F1}) {f.Title} — {f.FilePath}:{f.Line}");
            sb.AppendLine($"  Evidence: {f.Evidence}");
            sb.AppendLine($"  Suggested action: {f.SuggestedAction}");
            if (!string.IsNullOrWhiteSpace(f.ReasoningNote)) sb.AppendLine($"  Reasoning-site note: {f.ReasoningNote}");
        }
        return sb.ToString();
    }

    private static StringBuilder DeterministicPrompt(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped, RunOptions options, string evidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Objective");
        sb.AppendLine($"Improve the app '{snapshot.SourceName}' in one focused run by addressing exactly the {scoped.Count} scoped, prioritized finding(s) below. Do not widen the scope.");
        sb.AppendLine();
        sb.AppendLine("# Scope");
        foreach (var f in scoped) sb.AppendLine($"- `{f.FilePath}` (line {f.Line})");
        sb.AppendLine();
        sb.AppendLine("# Required changes");
        for (var i = 0; i < scoped.Count; i++)
        {
            var f = scoped[i];
            sb.AppendLine($"{i + 1}. [{f.Title}] {f.Description}");
            sb.AppendLine($"   Change: {f.SuggestedAction}");
            if (!string.IsNullOrWhiteSpace(f.ReasoningNote)) sb.AppendLine($"   External reasoning: {f.ReasoningNote}");
        }
        if (!string.IsNullOrWhiteSpace(options.Notes)) { sb.AppendLine(); sb.AppendLine($"Operator context: {options.Notes}"); }
        sb.AppendLine();
        sb.AppendLine("# Acceptance criteria");
        sb.AppendLine("- Every listed finding is resolved and verified in the touched files only.");
        sb.AppendLine("- No new behavior is introduced outside the scoped files.");
        sb.AppendLine("- Existing tests still pass; add regression coverage for the erratic-behavior fixes.");
        return sb;
    }
}
