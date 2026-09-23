using System.Text;
using SmartAgent.Domain;
using SmartAgent.Integrations.AI;

namespace SmartAgent.Core;

/// <summary>
/// Composes the final consolidated prompt for the scoped findings. Uses the
/// AI processing provider when available; otherwise falls back to a
/// deterministic template so a run never fails just because AI is offline.
///
/// CI/CD continuity: prompts chain across runs — each one references the
/// previous run, verifies previously-fixed findings, tags recurring items
/// (persisting / REGRESSION) and ends with the feedback + re-validation loop
/// so a pipeline can gate on regressions.
/// </summary>
public sealed class PromptBuilder(IChatCompletionClient chatClient)
{
    public async Task<GeneratedPrompt> BuildAsync(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped,
        RunOptions options, ContinuityReport? continuity, Guid runId, CancellationToken ct)
    {
        var evidence = RenderEvidence(snapshot, scoped, options, continuity, runId);

        if (chatClient.IsConfigured)
        {
            try
            {
                var prompt = await chatClient.CompleteAsync(SystemPrompt, evidence, ct);
                return new GeneratedPrompt
                {
                    Title = BuildTitle(scoped, options, continuity),
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
            Title = BuildTitle(scoped, options, continuity),
            Prompt = DeterministicPrompt(snapshot, scoped, options, continuity, runId, evidence).ToString(),
            TargetFindings = scoped,
            Composer = "built-in template (AI provider not configured/unavailable)"
        };
    }

    private const string SystemPrompt =
        """
        You are SmartAgent's prompt composer for a CONTINUOUS integration loop. You receive a scoped
        set of prioritized findings about a target application, plus the target's continuity history
        (previous runs, what was fixed, what persists, what regressed). Write ONE consolidated
        engineering prompt that a developer (or an AI coding assistant) can execute in a single run.

        Rules:
        - Address ONLY the supplied findings. Never invent new issues or widen the scope.
        - Chain with history: acknowledge the previous run, verify previously-fixed findings stay
        fixed, and prioritize REGRESSIONS first.
        - Be concise and precise; no filler.
        - Structure: continuity (1-2 lines), objective, scope, required changes (numbered, tagged
        new/persisting/REGRESSION), verification of previous fixes, acceptance criteria, next steps
        (feedback API + re-validation).
        - Keep it within roughly 400 words.
        """;

    private static string BuildTitle(IReadOnlyList<Finding> scoped, RunOptions options, ContinuityReport? c)
    {
        var runPart = c is { RunNumber: > 1 } ? $" — run #{c.RunNumber}" : "";
        if (scoped.Count == 0) return $"No actionable findings in this scope{runPart}";
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
        }} ({scoped.Count} scoped finding(s)){runPart}";
    }

    private static string RenderEvidence(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped,
        RunOptions options, ContinuityReport? continuity, Guid runId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Application: {snapshot.SourceName} ({snapshot.SourceType})");
        sb.AppendLine($"Source detail: {snapshot.SourceDetail}");
        sb.AppendLine($"Run focus: {options.Focus}; scope limit: {options.ScopeLimit}; run id: {runId}");
        if (!string.IsNullOrWhiteSpace(options.Notes)) sb.AppendLine($"Operator notes: {options.Notes}");

        if (continuity is { RunNumber: > 1 } c)
        {
            sb.AppendLine();
            sb.AppendLine($"Continuity: run #{c.RunNumber} for this target; previous run {c.PreviousRunUtc:yyyy-MM-dd} ({c.PreviousPromptTitle ?? "n/a"}).");
            sb.AppendLine($"Fixed and gone since last run: {c.ResolvedSinceLastRun.Count}. Persisting: {c.PersistingFindings.Count}. Regressions: {c.RegressionFindings.Count}.");
            foreach (var r in c.RegressionFindings) sb.AppendLine($"  REGRESSION: [{r.Fingerprint}] {r.Title} ({r.FilePath}:{r.LastLine}) — claimed fixed, back again");
            foreach (var r in c.ResolvedSinceLastRun) sb.AppendLine($"  RESOLVED (verify stays fixed): [{r.Fingerprint}] {r.Title} ({r.FilePath})");
        }

        sb.AppendLine();
        sb.AppendLine("Prioritized findings (highest priority first):");
        foreach (var f in scoped)
        {
            var tag = f.Continuity switch
            {
                ContinuityStatus.Regression => "REGRESSION",
                ContinuityStatus.Persisting => $"persisting, seen {f.Occurrences} runs",
                _ => "new"
            };
            sb.AppendLine($"- [{f.Id}/{f.Fingerprint}] ({f.Category}, {f.Severity}, score {f.Score:F1}, {tag}) {f.Title} — {f.FilePath}:{f.Line}");
            sb.AppendLine($"  Evidence: {f.Evidence}");
            sb.AppendLine($"  Suggested action: {f.SuggestedAction}");
            if (!string.IsNullOrWhiteSpace(f.ReasoningNote)) sb.AppendLine($"  Reasoning-site note: {f.ReasoningNote}");
        }
        return sb.ToString();
    }

    private static StringBuilder DeterministicPrompt(SourceSnapshot snapshot, IReadOnlyList<Finding> scoped,
        RunOptions options, ContinuityReport? continuity, Guid runId, string evidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Objective");
        sb.AppendLine($"Improve the app '{snapshot.SourceName}' in one focused run by addressing exactly the {scoped.Count} scoped, prioritized finding(s) below. Do not widen the scope.");

        if (continuity is { RunNumber: > 1 } c)
        {
            sb.AppendLine();
            sb.AppendLine("# Continuity");
            sb.AppendLine($"This is run #{c.RunNumber} for this target (previous run: {c.PreviousRunUtc:yyyy-MM-dd HH:mm} UTC, \"{c.PreviousPromptTitle ?? "n/a"}\").");
            if (c.ResolvedSinceLastRun.Count > 0)
            {
                sb.AppendLine($"Verified resolved since the previous run ({c.ResolvedSinceLastRun.Count}):");
                foreach (var r in c.ResolvedSinceLastRun) sb.AppendLine($"- {r.Title} — {r.FilePath}:{r.LastLine}");
            }
            if (c.PersistingFindings.Count > 0)
                sb.AppendLine($"Persisting from earlier runs: {c.PersistingFindings.Count} finding(s) — these escalated in priority.");
            if (c.RegressionFindings.Count > 0)
            {
                sb.AppendLine($"REGRESSIONS — previously reported fixed but detected again ({c.RegressionFindings.Count}); fix these FIRST:");
                foreach (var r in c.RegressionFindings) sb.AppendLine($"- {r.Title} — {r.FilePath}:{r.LastLine} (last reported fixed: {r.FeedbackNote ?? "no note"})");
            }
            if (c.ResolvedSinceLastRun.Count == 0 && c.PersistingFindings.Count == 0 && c.RegressionFindings.Count == 0)
                sb.AppendLine("No carry-over from previous runs; all findings in scope are new.");
        }

        sb.AppendLine();
        sb.AppendLine("# Scope");
        foreach (var f in scoped) sb.AppendLine($"- `{f.FilePath}` (line {f.Line})");

        sb.AppendLine();
        sb.AppendLine("# Required changes");
        for (var i = 0; i < scoped.Count; i++)
        {
            var f = scoped[i];
            var tag = f.Continuity switch
            {
                ContinuityStatus.Regression => " [REGRESSION]",
                ContinuityStatus.Persisting => $" [persisting, seen {f.Occurrences} run(s)]",
                _ => " [new]"
            };
            sb.AppendLine($"{i + 1}.{tag} [{f.Title}] {f.Description}");
            sb.AppendLine($"   Change: {f.SuggestedAction}");
            if (!string.IsNullOrWhiteSpace(f.ReasoningNote)) sb.AppendLine($"   External reasoning: {f.ReasoningNote}");
        }
        if (!string.IsNullOrWhiteSpace(options.Notes)) { sb.AppendLine(); sb.AppendLine($"Operator context: {options.Notes}"); }

        sb.AppendLine();
        sb.AppendLine("# Execution");
        sb.AppendLine("- This task list was reviewed and resolved by the agent as a single task item — no parallel split was needed.");

        sb.AppendLine();
        sb.AppendLine("# Verification of previous fixes");
        sb.AppendLine("- Every finding previously reported as fixed must remain fixed in the touched files; a reappearing finding is a regression and fails this run.");

        sb.AppendLine();
        sb.AppendLine("# Acceptance criteria");
        sb.AppendLine("- Every listed finding is resolved and verified in the touched files only.");
        sb.AppendLine("- No new behavior is introduced outside the scoped files.");
        sb.AppendLine("- Existing tests still pass; add regression coverage for the erratic-behavior fixes.");

        sb.AppendLine();
        sb.AppendLine("# Next steps (CI/CD loop)");
        sb.AppendLine($"1. Apply this prompt, then report the outcome for each finding fingerprint to POST /api/runs/{runId}/feedback (status: fixed | wont_fix | failed_verification).");
        sb.AppendLine("2. Re-run validation for the same target (POST /api/validate) and compare continuity: regressions must be 0 before promoting the build.");
        sb.AppendLine("3. The next generated prompt will automatically verify these fixes and escalate anything unresolved.");
        return sb;
    }

    /// <summary>
    /// Amends a built prompt with the execution statement that reflects what the agent
    /// actually did: resolved into multiple work items processed in parallel, or a
    /// single task item. Replaces the default single-task note rendered at build time.
    /// </summary>
    public static GeneratedPrompt WithExecutionStatement(GeneratedPrompt prompt, EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(report);

        var parallel = report.Plan.Units.Count > 1;
        string note = parallel
            ? $"- This task was resolved by the agent into {report.Plan.Units.Count} work items and processed in parallel on {report.Plan.Width} thread(s) across {report.WavesExecuted} wave(s); all items are consolidated here."
              + (report.UnitsFailed > 0 ? $" {report.UnitsFailed} item(s) failed and are reported in the run warnings." : "")
            : "- This task was reviewed and resolved by the agent as a single task item — no parallel split was needed.";
        var section = "# Execution" + Environment.NewLine + note;

        var text = prompt.Prompt;
        var start = text.IndexOf("# Execution", StringComparison.Ordinal);
        if (start >= 0)
        {
            var next = text.IndexOf(Environment.NewLine + "# ", start + 1, StringComparison.Ordinal);
            var end = next >= 0 ? next : text.Length;
            text = text[..start] + section + text[end..];
        }
        else
        {
            text = text.TrimEnd() + Environment.NewLine + Environment.NewLine + section;
        }

        return new GeneratedPrompt
        {
            Title = prompt.Title,
            Prompt = text,
            TargetFindings = prompt.TargetFindings,
            Composer = prompt.Composer
        };
    }
}