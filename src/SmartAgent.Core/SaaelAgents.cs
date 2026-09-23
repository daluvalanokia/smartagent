using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core;

// ============================================================================
// SAAEL role agents (§4): each agent performs its role's work deterministically
// and produces PROPOSALS/EVIDENCE — decisions stay with humans at the gates.
// ============================================================================

/// <summary>BA Agent (§0/§6): idea → capabilities → stories, acceptance criteria,
/// NFRs, and ambiguity questions the human BA must resolve.</summary>
public sealed partial class BaAgent
{
    private static readonly string[] HedgeWords =
        ["automatically", "appropriate", "properly", "as needed", "etc", "various", "several", "suitable", "correct"];

    public (IReadOnlyList<WorkItem> Stories, IReadOnlyList<Ambiguity> Ambiguities, IReadOnlyList<TraceLink> Links) Decompose(
        string idea, int firstStoryNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idea);

        // capabilities ≈ sentences; one story per substantive sentence
        var sentences = Regex.Split(idea, @"(?<=[.!?:;])\s+")
            .Select(s => s.Trim()).Where(s => s.Length > 10).ToList();
        var stories = new List<WorkItem>();
        var ambiguities = new List<Ambiguity>();
        var links = new List<TraceLink>();
        var lower = idea.ToLowerInvariant();

        for (var i = 0; i < sentences.Count; i++)
        {
            var id = $"SA-{firstStoryNumber + i:D3}";
            var sentLower = sentences[i].ToLowerInvariant();
            var hedges = HedgeWords.Where(h => sentLower.Contains(h)).ToList();

            var ac = new List<string>
            {
                $"Given a valid input related to \"{Trim(sentences[i], 60)}\", when the system processes it, then the expected business outcome is produced and recorded",
                "Every automated decision exposes a confidence score",
                "Every automated action is written to the audit trail"
            };
            var nfr = new List<string>
            {
                "Response time within the defined threshold",
                "Availability above the defined target",
                "Auditability required",
                "Security controls required",
                "Human fallback required"
            };

            stories.Add(new WorkItem
            {
                Id = id,
                Title = Trim(sentences[i], 80),
                Description = sentences[i],
                AcceptanceCriteria = ac,
                NonFunctionalRequirements = nfr,
                Priority = hedges.Count > 0 ? 1 : 2,                       // ambiguous items surface first
                Risk = sentLower.Contains("route") || sentLower.Contains("classif") || sentLower.Contains("secur") || sentLower.Contains("data") ? "High" : "Medium",
                StoryPoints = 2 + Math.Min(sentences[i].Length / 60, 3)
            });
            links.Add(new TraceLink { StoryId = id, Type = TraceLinkType.Requirement, TargetRef = $"business-idea:{firstStoryNumber:D3}" });
            links.Add(new TraceLink { StoryId = id, Type = TraceLinkType.Story, TargetRef = id });

            // ambiguity detection (§0): hedges, missing thresholds, undefined data scope
            if (hedges.Count > 0)
                ambiguities.Add(new Ambiguity
                {
                    Id = $"AMB-{firstStoryNumber + i:D3}a", StoryId = id,
                    Question = $"The requirement says \"{hedges[0]}\" — what exactly defines an {hedges[0]} outcome for this story? Provide the measurable criterion."
                });
            if (!Regex.IsMatch(sentences[i], @"\d"))
                ambiguities.Add(new Ambiguity
                {
                    Id = $"AMB-{firstStoryNumber + i:D3}b", StoryId = id,
                    Question = "No numeric threshold appears in this requirement. What confidence level / volume / timeout is acceptable?"
                });
        }

        if (ambiguities.Count == 0)
            ambiguities.Add(new Ambiguity
            {
                Id = $"AMB-{firstStoryNumber:D3}x", StoryId = stories[0].Id,
                Question = "Which data may be processed, and is human review required before automated actions take effect?"
            });

        return (stories, ambiguities, links);
    }

    private static string Trim(string s, int len) => s.Length <= len ? s : s[..(len - 1)] + "…";
}

/// <summary>PM Agent (§7): proposes a sprint backlog from priority, risk and
/// capacity. Recommendation only — the PM and PO review it.</summary>
public sealed class PmAgent
{
    public SprintProposal ProposeSprint(IReadOnlyList<WorkItem> backlog, int capacityPoints, string proposalId)
    {
        var ordered = backlog.OrderBy(s => s.Priority).ThenBy(s => s.Risk == "High" ? 0 : 1).ThenBy(s => s.Id).ToList();
        var chosen = new List<WorkItem>();
        var points = 0;
        foreach (var s in ordered)
        {
            if (points + s.StoryPoints > capacityPoints) continue;
            chosen.Add(s);
            points += s.StoryPoints;
        }

        var highRisk = chosen.Count(s => s.Risk == "High");
        return new SprintProposal
        {
            ProposalId = proposalId,
            StoryIds = chosen.Select(s => s.Id).ToList(),
            TotalPoints = points,
            Rationale = $"Selected {chosen.Count} story(ies), {points} points (capacity {capacityPoints}). " +
                        $"Ordered by priority, then risk ({highRisk} high-risk item(s) deliberately scheduled early). " +
                        "PROPOSAL: Project Manager and Product Owner must approve; the AI does not silently change priorities."
        };
    }
}

/// <summary>Developer Agent (§9): change proposal — files affected, impact, risk,
/// suggested tests. Never directly modifies code.</summary>
public sealed partial class DeveloperAgent
{
    [GeneratedRegex(@"[\w./\\-]+\.(?:cs|html|js|ts|py|json|css|sql|razor|md)\b")]
    private static partial Regex FileRefRegex();

    [GeneratedRegex(@"[A-Z][a-zA-Z]{3,}(?:Service|Controller|Repository|Provider|Engine|Store|Agent)\b")]
    private static partial Regex ComponentRegex();

    public ChangeProposal ProposeChange(WorkItem story)
    {
        var files = FileRefRegex().Matches(story.Description)
            .Select(m => m.Value).Distinct().ToList();
        var components = ComponentRegex().Matches(story.Description)
            .Select(m => m.Value).Distinct().ToList();

        var impact = new List<string>();
        if (components.Count > 0) impact.Add($"Components: {string.Join(", ", components.Take(4))}");
        impact.Add(story.Risk == "High" ? "Potential impact: API, database and downstream consumers" : "Potential impact: single component, contained blast radius");

        var n = Math.Max(Math.Max(files.Count, components.Count), 1);
        return new ChangeProposal
        {
            StoryId = story.Id,
            FilesAffected = files.Count > 0 ? files : components.Select(c => $"{c}.cs").ToList(),
            PotentialImpact = impact,
            Risk = story.Risk,
            SuggestedUnitTests = 7 * n + 7,
            SuggestedIntegrationTests = 3 * n + 3,
            SuggestedNegativeScenarios = n + 2,
        };
    }
}

/// <summary>QA Agent (§12): generates normal, boundary, negative, security,
/// performance and regression scenarios from acceptance criteria and history.</summary>
public sealed class QaAgent
{
    private static readonly string[] Categories = ["Normal", "Boundary", "Negative", "Security", "Performance", "Regression"];

    public IReadOnlyList<TestScenario> GenerateScenarios(WorkItem story, IReadOnlyList<string> historicalDefects)
    {
        var scenarios = new List<TestScenario>();
        foreach (var cat in Categories)
        {
            var perCat = cat switch
            {
                "Normal" => story.AcceptanceCriteria.Count,
                "Boundary" => 2,
                "Negative" => 2,
                _ => 1
            };
            for (var i = 0; i < perCat; i++)
                scenarios.Add(new TestScenario
                {
                    StoryId = story.Id,
                    Category = cat,
                    Title = cat switch
                    {
                        "Normal" => $"Verify acceptance criterion {i + 1}: {Trim(story.AcceptanceCriteria[i], 70)}",
                        "Boundary" => i == 0 ? "Verify behavior at minimum input size / threshold" : "Verify behavior at maximum input size / threshold",
                        "Negative" => i == 0 ? "Verify graceful handling of invalid / malformed input" : "Verify system degrades safely when a dependency is unavailable",
                        "Security" => "Verify injection / privilege-escalation attempt is rejected and audited",
                        "Performance" => "Verify response time stays within the NFR threshold under expected load",
                        _ => "Replay the most recent production defect scenario as a regression guard"
                    }
                });
        }

        // defect → knowledge → regression protection (§12)
        foreach (var defect in historicalDefects)
            scenarios.Add(new TestScenario
            {
                StoryId = story.Id, Category = "Regression",
                Title = $"Historical defect replay: {Trim(defect, 90)}"
            });
        return scenarios;
    }

    private static string Trim(string s, int len) => s.Length <= len ? s : s[..(len - 1)] + "…";
}

/// <summary>CI Agent (§10): converts "Build failed" into a root-cause analysis.</summary>
public sealed partial class CiAgent
{
    [GeneratedRegex(@"error CS\d+: ([^\n]+)")]
    private static partial Regex CsErrorRegex();

    [GeneratedRegex(@"commit\s+([0-9a-f]{7,40})|([0-9a-f]{7})\s+[A-Z]")]
    private static partial Regex CommitRegex();

    [GeneratedRegex(@"[A-Za-z0-9_.\-]*Test[A-Za-z0-9_.]*")]
    private static partial Regex TestNameRegex();

    public CiAnalysis Analyze(string buildLog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildLog);

        var err = CsErrorRegex().Match(buildLog);
        var primaryCause = err.Success
            ? err.Groups[1].Value.Trim()
            : buildLog.Split('\n').FirstOrDefault(l => l.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
              ?? "Build failed";
        var commit = CommitRegex().Match(buildLog);
        var tests = buildLog.Split('\n')
            .Where(l => l.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => TestNameRegex().Matches(l).Select(m => m.Value))
            .Distinct().ToList();

        return new CiAnalysis
        {
            PrimaryCause = primaryCause,
            LikelyCommit = commit.Success ? (commit.Groups[1].Success ? commit.Groups[1].Value : commit.Groups[2].Value) : "unknown",
            AffectedTests = tests,
            SuggestedCorrection = primaryCause.Contains("null", StringComparison.OrdinalIgnoreCase)
                ? "Initialize the result object before fallback processing and add a null-guard regression test."
                : "Fix the failing construct, then re-run the affected tests before re-attempting the build.",
            Confidence = 0.85 + Math.Min(tests.Count * 0.02, 0.1),
            DeveloperReviewRequired = true
        };
    }
}

/// <summary>Learning Agent (§19): data-driven retrospective insights.</summary>
public sealed class LearningAgent
{
    public IReadOnlyList<SprintInsight> Analyze(IReadOnlyList<(string StoryTitle, int PlannedDays, int ActualDays)> sprintHistory)
    {
        var insights = new List<SprintInsight>();
        if (sprintHistory.Count == 0) return insights;

        var api = sprintHistory.Where(s => s.StoryTitle.Contains("API", StringComparison.OrdinalIgnoreCase)
                                        || s.StoryTitle.Contains("integration", StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = sprintHistory.Where(s => !s.StoryTitle.Contains("API", StringComparison.OrdinalIgnoreCase)
                                        && !s.StoryTitle.Contains("integration", StringComparison.OrdinalIgnoreCase)).ToList();

        if (api.Count >= 2 && rest.Count >= 2)
        {
            var apiOver = api.Average(s => (double)s.ActualDays / s.PlannedDays);
            var restOver = rest.Average(s => (double)s.ActualDays / s.PlannedDays);
            if (apiOver - restOver >= 0.2)
                insights.Add(new SprintInsight
                {
                    Observation = $"Stories involving external APIs took {(int)Math.Round((apiOver / restOver - 1) * 100)}% longer than estimated (planned vs actual overrun {apiOver:F2}× vs {restOver:F2}×).",
                    SuggestedAction = "Add API dependency analysis during sprint planning and pad external-API stories by one buffer day.",
                    Evidence = $"{api.Count} API/integration stories vs {rest.Count} other stories across sprint history."
                });
        }

        var overrun = sprintHistory.Count(s => s.ActualDays > s.PlannedDays);
        if (overrun > sprintHistory.Count / 2)
            insights.Add(new SprintInsight
            {
                Observation = $"{overrun}/{sprintHistory.Count} stories overran their estimate.",
                SuggestedAction = "Raise the planning buffer or split stories above 5 points before committing the next sprint.",
                Evidence = "Sprint actual-vs-planned history."
            });

        if (insights.Count == 0)
            insights.Add(new SprintInsight
            {
                Observation = "Estimates have been accurate; no systemic overrun detected.",
                SuggestedAction = "Maintain the current planning process; keep monitoring per-category cycle time.",
                Evidence = $"{sprintHistory.Count} stories analyzed."
            });
        return insights;
    }
}
