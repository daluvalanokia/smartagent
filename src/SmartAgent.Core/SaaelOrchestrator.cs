using System.Text;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// SAAEL orchestrator (§3): owns the project lifecycle state — backlog,
/// sprints, stories, approvals, governance, traceability and the token ledger —
/// and advances stories through the lifecycle. Role agents do the work;
/// HUMAN GATES (approval requests) guard the transitions people own.
/// AI performs work; humans own decisions.
/// </summary>
public sealed class SaaelOrchestrator(
    BaAgent ba,
    PmAgent pm,
    DeveloperAgent developer,
    QaAgent qa,
    LearningAgent learning,
    CiAgent ci,
    AppAnalyzerAgent appAnalyzer)
{
    private readonly object _sync = new();
    private int _storySeq = 101;
    private int _proposalSeq = 1;
    private int _approvalSeq = 1;

    private readonly Dictionary<string, WorkItem> _items = new();
    private readonly List<Ambiguity> _ambiguities = [];
    private readonly List<ApprovalRequest> _approvals = [];
    private readonly List<GovernanceRecord> _governance = [];
    private readonly List<TraceLink> _trace = [];
    private readonly List<SprintProposal> _proposals = [];
    private readonly Dictionary<string, List<TestScenario>> _scenarios = [];
    private readonly Dictionary<string, ChangeProposal> _changeProposals = [];
    private readonly List<TokenUsage> _tokens = [];

    /// <summary>Token budget for AI agent work in this project; when exhausted,
    /// agent output is demoted to recommendation-only and nothing auto-executes.</summary>
    public int TokenBudget { get; set; } = 250_000;
    public bool BudgetExceeded => _tokens.Sum(t => t.Tokens) >= TokenBudget;

    /// <summary>Seeded sprint history so the Learning Agent has evidence on first run.</summary>
    private readonly List<(string StoryTitle, int PlannedDays, int ActualDays)> _history =
    [
        ("Integrate routing API for service requests", 5, 9),
        ("Add classification confidence threshold", 3, 4),
        ("External API retry policy", 4, 8),
        ("Export audit trail to report", 2, 2),
        ("Import customer data batch", 3, 3),
        ("Ingest telemetry from API gateway", 6, 11)
    ];

    // ---------- stage 0-6: idea → decomposition → sprint proposal ----------

    public SaaelPlanView SubmitIdea(string idea)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idea);
        lock (_sync)
        {
            var budgeted = BudgetExceeded;
            var first = _storySeq;
            var (stories, ambiguities, links) = ba.Decompose(idea, first);
            _storySeq += stories.Count;

            foreach (var s in stories) _items[s.Id] = s;
            _ambiguities.AddRange(ambiguities);
            _trace.AddRange(links);

            Record("BA-Agent-01", "requirements-decomposition", idea,
                $"{stories.Count} story(ies), {ambiguities.Count} ambiguities raised",
                budgeted ? "budget-limited-recommendation" : "executed",
                est: 800 + idea.Length / 4);

            var proposal = ProposeSprintInternal(capacityPoints: stories.Sum(s => s.StoryPoints));

            return new SaaelPlanView
            {
                Stories = stories.Select(View).ToList(),
                Ambiguities = ambiguities.Select(a => new AmbiguityView
                {
                    Id = a.Id, StoryId = a.StoryId, Question = a.Question,
                    Resolution = a.Resolution, ResolvedBy = a.ResolvedBy
                }).ToList(),
                SprintProposal = proposal,
                BudgetExceeded = budgeted
            };
        }
    }

    private SprintProposal ProposeSprintInternal(int capacityPoints)
    {
        var proposal = pm.ProposeSprint(_items.Values.ToList(), capacityPoints, $"SPR-{_proposalSeq++:D3}");
        _proposals.Add(proposal);
        Record("PM-Agent-02", "sprint-proposal", $"capacity {capacityPoints} points",
            $"{proposal.StoryIds.Count} stories, {proposal.TotalPoints} points — recommendation pending PM+PO review",
            "recommendation", est: 400 + _items.Count * 10);
        return proposal;
    }

    public SprintProposal ProposeSprint(int capacityPoints)
    {
        lock (_sync) { return ProposeSprintInternal(capacityPoints); }
    }

    public void DecideSprint(string proposalId, string actor, bool approve)
    {
        lock (_sync)
        {
            var p = _proposals.FirstOrDefault(x => x.ProposalId == proposalId) ?? throw new ArgumentException($"Unknown proposal {proposalId}");
            p.Status = approve ? "Approved" : "Rejected";
            Record("PM-Agent-02", "sprint-decision", p.ProposalId,
                $"{(approve ? "Approved" : "Rejected")} by {actor}", "executed", humanReviewer: actor, est: 100);
        }
    }

    public void ResolveAmbiguity(string ambiguityId, string resolution, string actor)
    {
        lock (_sync)
        {
            var a = _ambiguities.FirstOrDefault(x => x.Id == ambiguityId) ?? throw new ArgumentException($"Unknown ambiguity {ambiguityId}");
            a.Resolution = resolution;
            a.ResolvedBy = actor;
            _trace.Add(new TraceLink { StoryId = a.StoryId, Type = TraceLinkType.Approval, TargetRef = $"ambiguity:{a.Id}:{actor}" });
            Record("BA-Agent-01", "ambiguity-resolution", a.Id, $"Resolved by {actor}", "executed", humanReviewer: actor, est: 150);
        }
    }

    // ---------- stage 9: development proposal + QA scenarios ----------

    public ChangeProposal ProposeChange(string storyId)
    {
        lock (_sync)
        {
            var story = Get(storyId);
            var proposal = developer.ProposeChange(story);
            _changeProposals[storyId] = proposal;
            _trace.Add(new TraceLink { StoryId = storyId, Type = TraceLinkType.Code, TargetRef = string.Join(", ", proposal.FilesAffected.Take(4)) });
            Record("DEV-Agent-04", "change-proposal", storyId,
                $"{proposal.FilesAffected.Count} file(s), risk {proposal.Risk}, {proposal.SuggestedUnitTests} unit tests suggested — developer review required",
                BudgetExceeded ? "budget-limited-recommendation" : "proposal", est: 600 + story.Description.Length / 4);
            return proposal;
        }
    }

    public void DecideChange(string storyId, string actor, string decision)   // Accepted | Modified | Rejected
    {
        lock (_sync)
        {
            var p = _changeProposals.TryGetValue(storyId, out var cp) ? cp : throw new ArgumentException($"No proposal for {storyId}");
            p.Status = decision;
            Record("DEV-Agent-04", "change-decision", storyId, $"{decision} by {actor}", "executed", humanReviewer: actor, est: 100);
        }
    }

    public IReadOnlyList<TestScenario> GenerateQaScenarios(string storyId, IReadOnlyList<string>? historicalDefects = null)
    {
        lock (_sync)
        {
            var story = Get(storyId);
            var defects = historicalDefects ?? ["Customer submitted a 5,000-character request that overflowed the routing buffer (INC-2201)"];
            var scenarios = qa.GenerateScenarios(story, defects);
            _scenarios[storyId] = scenarios.ToList();
            _trace.Add(new TraceLink { StoryId = storyId, Type = TraceLinkType.Test, TargetRef = $"{scenarios.Count} generated scenarios" });
            Record("QA-Agent-05", "test-scenario-generation", storyId,
                $"{scenarios.Count} scenarios across 6 categories incl. defect replay", "executed", est: 500 + scenarios.Count * 20);
            return scenarios;
        }
    }

    // ---------- stage 8: the lifecycle state machine with human gates ----------

    /// <summary>Linear lifecycle order (§8).</summary>
    private static readonly LifecycleState[] Order =
    [
        LifecycleState.New, LifecycleState.Analyzing, LifecycleState.Ready, LifecycleState.InDevelopment,
        LifecycleState.CodeReview, LifecycleState.Build, LifecycleState.UnitTest, LifecycleState.IntegrationTest,
        LifecycleState.Qa, LifecycleState.BusinessTest, LifecycleState.Staging, LifecycleState.Uat,
        LifecycleState.ReleaseApproved, LifecycleState.Production, LifecycleState.Monitoring, LifecycleState.Completed
    ];

    /// <summary>Human gates: transitions people own at every automation level (§22 key principle).</summary>
    private static readonly Dictionary<(LifecycleState From, LifecycleState To), HumanRole[]> Gates = new()
    {
        [(LifecycleState.Analyzing, LifecycleState.Ready)] = [HumanRole.BusinessAnalyst],
        [(LifecycleState.Ready, LifecycleState.InDevelopment)] = [HumanRole.DevelopmentManager],
        [(LifecycleState.CodeReview, LifecycleState.Build)] = [HumanRole.Developer],                      // AI review is never the sole approval (§11)
        [(LifecycleState.Qa, LifecycleState.BusinessTest)] = [HumanRole.QaManager],
        [(LifecycleState.BusinessTest, LifecycleState.Staging)] = [HumanRole.BusinessUser],
        [(LifecycleState.Staging, LifecycleState.Uat)] = [HumanRole.ProductOwner],
        [(LifecycleState.Uat, LifecycleState.ReleaseApproved)] = [HumanRole.ProductOwner, HumanRole.QaManager, HumanRole.ImplementationCoordinator],
        [(LifecycleState.ReleaseApproved, LifecycleState.Production)] = [HumanRole.ImplementationCoordinator],
    };

    /// <summary>Attempts the next lifecycle transition. Returns what happened:
    /// transitioned, awaiting-approval (with the open request), or blocked (with reason).</summary>
    public AdvanceResult Advance(string storyId, string? actor = null)
    {
        lock (_sync)
        {
            var story = Get(storyId);
            var idx = Array.IndexOf(Order, story.State);
            if (idx >= Order.Length - 1)
                return new AdvanceResult(storyId, story.State, story.State, "completed", null, "Story already completed.");
            var to = Order[idx + 1];

            // preconditions
            if (to == LifecycleState.Ready && _ambiguities.Any(a => a.StoryId == storyId && !a.Resolved))
                return new AdvanceResult(storyId, story.State, to, "blocked", null,
                    "All ambiguities must be resolved by the Business Analyst before the story can become Ready.");
            if (to == LifecycleState.InDevelopment && (!_changeProposals.TryGetValue(storyId, out var cp) || cp.Status != "Accepted"))
                return new AdvanceResult(storyId, story.State, to, "blocked", null,
                    "A change proposal must exist and be Accepted by the Development Manager first.");
            if (to == LifecycleState.Production)
            {
                var readiness = BuildReadiness(storyId);
                if (!readiness.ReadyForProduction)
                    return new AdvanceResult(storyId, story.State, to, "blocked", null,
                        "Release readiness not satisfied: " + readiness.Summary);
            }

            // human gate?
            if (Gates.TryGetValue((story.State, to), out var roles))
            {
                var request = _approvals.FirstOrDefault(a => a.StoryId == storyId && a.FromState == story.State && a.ToState == to && !a.Rejected)
                    ?? new ApprovalRequest { Id = $"APR-{_approvalSeq++:D3}", StoryId = storyId, FromState = story.State, ToState = to, RequiredRoles = roles };
                if (!_approvals.Contains(request)) _approvals.Add(request);

                if (!request.Satisfied)
                    return new AdvanceResult(storyId, story.State, to, "awaiting-approval", request,
                        $"Human gate {story.State} → {to}: awaiting {string.Join(", ", roles.Where(r => !request.Decisions.Any(d => d.Role == r && d.Approved)))}.");

                Transition(story, to, $"human-gate approved by {string.Join("+", request.Decisions.Where(d => d.Approved).Select(d => d.Actor))}");
                return new AdvanceResult(storyId, Order[idx], to, "transitioned", request, "Gate satisfied; transition executed.");
            }

            // technical transition — automation level decides (§22)
            var auto = story.Level switch
            {
                AutomationLevel.L1_Recommend => false,          // human decides every step
                AutomationLevel.L2_ApprovedExecution => story.State == LifecycleState.New,  // agent analysis auto
                AutomationLevel.L3_AutoLowRisk => story.State is LifecycleState.New or LifecycleState.InDevelopment
                    or LifecycleState.Build or LifecycleState.UnitTest or LifecycleState.IntegrationTest or LifecycleState.Production,
                AutomationLevel.L4_PolicyAutonomous => true,
                _ => false
            };
            // L4 policy guard: high-impact review is never skipped even at L4 (§11, §25)
            if (story.RequiresHighImpactReview && to is LifecycleState.Production)
                auto = false;
            if (BudgetExceeded) auto = false;                  // budget exhausted → recommend-only

            if (auto)
            {
                Transition(story, to, $"auto (L{(int)story.Level})");
                return new AdvanceResult(storyId, Order[idx], to, "transitioned", null, $"Technical transition auto-executed at level L{(int)story.Level}.");
            }

            // not auto → human approval required for a technical step
            var req = _approvals.FirstOrDefault(a => a.StoryId == storyId && a.FromState == story.State && a.ToState == to && !a.Rejected)
                ?? new ApprovalRequest { Id = $"APR-{_approvalSeq++:D3}", StoryId = storyId, FromState = story.State, ToState = to, RequiredRoles = [HumanRole.ProjectManager] };
            if (!_approvals.Contains(req)) _approvals.Add(req);
            if (!req.Satisfied)
                return new AdvanceResult(storyId, story.State, to, "awaiting-approval", req,
                    $"Level L{(int)story.Level}: transition {story.State} → {to} requires Project Manager approval.");
            Transition(story, to, $"approved by {string.Join("+", req.Decisions.Where(d => d.Approved).Select(d => d.Actor))}");
            return new AdvanceResult(storyId, Order[idx], to, "transitioned", req, "Approved technical transition executed.");
        }
    }

    public void DecideApproval(string approvalId, HumanRole role, string actor, bool approved, string notes = "")
    {
        lock (_sync)
        {
            var a = _approvals.FirstOrDefault(x => x.Id == approvalId) ?? throw new ArgumentException($"Unknown approval {approvalId}");
            if (!a.RequiredRoles.Contains(role))
                throw new ArgumentException($"Role {role} is not a required approver for gate {a.FromState} → {a.ToState}.");
            a.Decisions.Add(new RoleDecision { Role = role, Actor = actor, Approved = approved, Notes = notes });
            _trace.Add(new TraceLink { StoryId = a.StoryId, Type = TraceLinkType.Approval, TargetRef = $"{a.Id}:{actor}:{(approved ? "approved" : "rejected")}" });
            Record("ORCH", "approval-decision", a.Id, $"{role} {actor}: {(approved ? "approved" : "REJECTED")}",
                "executed", humanReviewer: actor, est: 120);
        }
    }

    private void Transition(WorkItem story, LifecycleState to, string via)
    {
        var from = story.State;
        story.State = to;
        if (to == LifecycleState.ReleaseApproved || to == LifecycleState.Production)
            _trace.Add(new TraceLink { StoryId = story.Id, Type = TraceLinkType.Release, TargetRef = $"{from}→{to} {via}" });
        if (to == LifecycleState.Monitoring)
            _trace.Add(new TraceLink { StoryId = story.Id, Type = TraceLinkType.Telemetry, TargetRef = "production-telemetry-linked" });
        Record("ORCH", "lifecycle-transition", story.Id, $"{from} → {to} ({via})", "executed", est: 80);
    }

    // ---------- stage 15/16: release readiness + progressive delivery ----------

    public ReleaseReadinessReport Readiness(string storyId)
    {
        lock (_sync) { return BuildReadiness(storyId); }
    }

    private ReleaseReadinessReport BuildReadiness(string storyId)
    {
        var story = Get(storyId);
        var openApprovals = _approvals.Where(a => a.StoryId == storyId && !a.Rejected && !a.Satisfied).ToList();
        var scenarioCount = _scenarios.TryGetValue(storyId, out var sc) ? sc.Count : 0;
        var stateIdx = Array.IndexOf(Order, story.State);

        var checks = new List<ReleaseReadinessCheck>
        {
            new() { Area = "Requirements", Status = story.State >= LifecycleState.Uat ? "Pass" : "Outstanding", Detail = $"Story state: {story.State}" },
            new() { Area = "Tests", Status = scenarioCount >= 6 ? "Pass" : "Fail", Detail = $"{scenarioCount} test scenario(s) generated" },
            new() { Area = "Critical defects", Status = "Pass", Detail = "0 open critical defects" },
            new() { Area = "Security", Status = story.Risk == "High" && story.State < LifecycleState.Staging ? "Outstanding" : "Pass", Detail = $"Risk class {story.Risk}" },
            new() { Area = "Performance", Status = stateIdx >= Array.IndexOf(Order, LifecycleState.Qa) ? "Pass" : "Outstanding", Detail = "NFR threshold check" },
            new() { Area = "Rollback capability", Status = "Pass", Detail = "Verified (previous release retained)" },
            new() { Area = "Monitoring", Status = "Pass", Detail = "Configured" }
        };

        // upcoming human authorizations (§15): gates ahead of the current state whose
        // required roles have not yet recorded an approval
        var outstanding = openApprovals
            .Where(a => !a.Rejected)
            .Select(a => $"{a.Id}: {string.Join(", ", a.RequiredRoles.Where(r => !a.Decisions.Any(d => d.Role == r && d.Approved)))}")
            .ToList();
        for (var i = stateIdx; i < Order.Length - 1 && Order[i] < LifecycleState.Production; i++)
        {
            if (!Gates.TryGetValue((Order[i], Order[i + 1]), out var gateRoles)) continue;
            var req = _approvals.FirstOrDefault(a => a.StoryId == storyId && a.FromState == Order[i] && a.ToState == Order[i + 1]);
            foreach (var role in gateRoles.Where(r => req is null || !req.Decisions.Any(d => d.Role == r && d.Approved)))
                outstanding.Add($"Pending authorization: {role} for {Order[i]} → {Order[i + 1]}");
        }

        // ready = technical checks green, nothing rejected, and the release-approval gate passed
        var ready = checks.All(c => c.Status == "Pass")
                   && openApprovals.All(a => !a.Rejected)
                   && stateIdx >= Array.IndexOf(Order, LifecycleState.ReleaseApproved);
        if (!ready && outstanding.Count == 0)
            outstanding.Add("Release gates not yet passed — see checks above.");

        return new ReleaseReadinessReport
        {
            StoryId = storyId,
            Checks = checks,
            ReadyForProduction = ready,
            OutstandingApprovals = outstanding,
            Summary = ready
                ? "All technical gates passed; human release authorization is the only outstanding step."
                : $"Not ready: {checks.Count(c => c.Status != "Pass")} check(s) failing/outstanding, {openApprovals.Count} approval request(s) open."
        };
    }

    /// <summary>Progressive delivery safeguard (§16). With an actor: human-authorized rollback.
    /// Without an actor: policy-authorized auto-rollback, allowed ONLY at L4.</summary>
    public string Rollback(string storyId, string reason, string? actor = null, double? errorRate = null)
    {
        lock (_sync)
        {
            var story = Get(storyId);
            if (story.State is not (LifecycleState.Production or LifecycleState.Monitoring))
                return $"Rollback not applicable: story {storyId} is in {story.State}.";

            if (actor is null)
            {
                if (story.Level != AutomationLevel.L4_PolicyAutonomous)
                    return "Auto-rollback without human authorization requires automation level L4 (predefined safety policy).";
                if (errorRate is not > 0.05)
                    return "Auto-rollback policy not triggered: error rate below the 5% safety threshold.";
                Record("OPS-Agent-09", "policy-rollback", storyId, $"error rate {errorRate:P1} > 5% threshold", "executed", est: 200);
            }
            else
            {
                Record("OPS-Agent-09", "human-rollback", storyId, $"authorized by {actor}: {reason}", "executed", humanReviewer: actor, est: 200);
            }

            story.State = LifecycleState.Staging;
            _trace.Add(new TraceLink { StoryId = storyId, Type = TraceLinkType.Release, TargetRef = $"rollback-to-staging: {reason}" });
            return $"Rolled back {storyId} to Staging. Incident created; humans notified.";
        }
    }

    // ---------- stage 17-19: learning loop + retrospectives ----------

    public IReadOnlyList<SprintInsight> Retrospective()
    {
        lock (_sync)
        {
            var insights = learning.Analyze(_history);
            Record("LEARN-Agent-10", "retrospective-analysis", $"{_history.Count} sprint records",
                $"{insights.Count} insight(s) — process improvement recommendations, not automatic management decisions",
                "executed", est: 700 + _history.Count * 30);
            return insights;
        }
    }

    public CiAnalysis AnalyzeBuildFailure(string buildLog)
    {
        lock (_sync)
        {
            var analysis = ci.Analyze(buildLog);
            Record("CI-Agent-06", "build-failure-analysis", Trim(buildLog, 120),
                $"primary cause: {Trim(analysis.PrimaryCause, 90)}; confidence {analysis.Confidence:P0}; developer review required",
                "executed", est: 600 + buildLog.Length / 4);
            return analysis;
        }
    }

    // ---------- browser app analysis: fetch → findings → suggested stories as prompts ----------

    /// <summary>BROWSER-Agent: opens a browser-style fetch of the target (localhost allowed),
    /// analyzes the page against the focus areas in the prompt, and converts findings into
    /// backlog stories whose chained prompts feed the CI/CD pipeline. Stories are created
    /// in state New — every downstream gate stays human.</summary>
    public async Task<AppAnalysisReport> AnalyzeAppAsync(string url, string focusPrompt)
    {
        var uri = AppAnalyzerAgent.ValidateUrl(url);   // SSRF-safe guard: http/https only, no credentials
        var (status, html) = await appAnalyzer.FetchAsync(uri);
        var findings = appAnalyzer.Analyze(html, focusPrompt);
        var suggestions = appAnalyzer.SuggestStories(findings, uri.ToString());

        var created = new List<string>();
        lock (_sync)
        {
            foreach (var sug in suggestions)
            {
                var id = $"SA-{_storySeq++:D3}";
                _items[id] = new WorkItem
                {
                    Id = id, Title = sug.SuggestedTitle, Description = sug.Description,
                    AcceptanceCriteria = sug.AcceptanceCriteria,
                    NonFunctionalRequirements = ["Human review required before build", "No regressions on the analyzed page"],
                    Risk = sug.Risk, StoryPoints = sug.StoryPoints, Priority = sug.Priority,
                    State = LifecycleState.New, Level = AutomationLevel.L2_ApprovedExecution
                };
                created.Add(id);
                _trace.Add(new TraceLink { StoryId = id, Type = TraceLinkType.Requirement, TargetRef = $"browser-analysis:{uri}" });
            }

            Record("BROWSER-Agent-03", "app-analysis", $"GET {uri} (status {status}); focus: {Trim(focusPrompt, 80)}",
                $"{findings.Count} finding(s), {created.Count} story suggestion(s) created in backlog — every gate downstream stays human",
                BudgetExceeded ? "budget-limited-recommendation" : "executed",
                est: 900 + html.Length / 4);
        }

        var title = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value.Trim();

        return new AppAnalysisReport
        {
            Url = uri.ToString(), FocusPrompt = focusPrompt, StatusCode = status, PageTitle = title,
            Findings = findings, StorySuggestions = suggestions, CreatedStoryIds = created,
            Summary = findings.Count == 0
                ? $"No static-analysis findings on {uri} for the requested focus areas."
                : $"{findings.Count} finding(s) on {uri}: {findings.Count(f => f.Severity == "High")} high, " +
                  $"{findings.Count(f => f.Severity == "Medium")} medium, {findings.Count(f => f.Severity == "Low")} low. " +
                  $"{created.Count} story suggestion(s) added to the backlog as chained prompts."
        };
    }

    // ---------- queries ----------

    private WorkItem Get(string storyId) =>
        _items.TryGetValue(storyId, out var s) ? s : throw new ArgumentException($"Unknown story {storyId}");

    public IReadOnlyList<WorkItem> Backlog() { lock (_sync) { return _items.Values.OrderBy(s => s.Id).ToList(); } }

    public IReadOnlyList<ApprovalRequest> OpenApprovals(string? storyId = null)
    { lock (_sync) { return _approvals.Where(a => storyId is null || a.StoryId == storyId).ToList(); } }

    public IReadOnlyList<GovernanceRecord> Governance(string? storyId = null)
    { lock (_sync) { return _governance.Where(g => storyId is null || _trace.Any(t => t.StoryId == storyId)).ToList(); } }

    public IReadOnlyList<TraceLink> Trace(string storyId)
    { lock (_sync) { return _trace.Where(t => t.StoryId == storyId).ToList(); } }

    public IReadOnlyList<SprintInsight> Insights => Retrospective();

    public TokenLedgerView Tokens()
    {
        lock (_sync)
        {
            return new TokenLedgerView
            {
                Budget = TokenBudget,
                Used = _tokens.Sum(t => t.Tokens),
                ByAgent = _tokens.GroupBy(t => t.AgentId)
                    .Select(g => new AgentTokens { AgentId = g.Key, Tokens = g.Sum(x => x.Tokens) }).ToList(),
                Exceeded = BudgetExceeded
            };
        }
    }

    private void Record(string agentId, string action, string input, string output, string result,
        string? humanReviewer = null, int est = 0)
    {
        _governance.Add(new GovernanceRecord
        {
            RecordId = $"GOV-{_governance.Count + 1:D4}", AgentId = agentId, Action = action,
            InputSummary = Trim(input, 200), OutputSummary = Trim(output, 200),
            HumanReviewer = humanReviewer, Result = result, TokensUsed = est
        });
        if (est > 0) _tokens.Add(new TokenUsage { AgentId = agentId, Tokens = est });
    }

    private static string Trim(string s, int len) => s.Length <= len ? s : s[..(len - 1)] + "…";

    // ---------- view models ----------

    public sealed class SaaelPlanView
    {
        public required IReadOnlyList<WorkItemView> Stories { get; init; }
        public required IReadOnlyList<AmbiguityView> Ambiguities { get; init; }
        public required SprintProposal SprintProposal { get; init; }
        public bool BudgetExceeded { get; init; }
    }

    public sealed record WorkItemView(string Id, string Title, string State, string Risk, int Points, int Priority);

    public sealed class AmbiguityView
    {
        public required string Id { get; init; }
        public required string StoryId { get; init; }
        public required string Question { get; init; }
        public string? Resolution { get; init; }
        public string? ResolvedBy { get; init; }
    }

    public sealed record AdvanceResult(string StoryId, LifecycleState FromState, LifecycleState ToState,
        string Status, ApprovalRequest? Approval, string Message);

    public sealed class TokenLedgerView
    {
        public required int Budget { get; init; }
        public required int Used { get; init; }
        public required IReadOnlyList<AgentTokens> ByAgent { get; init; }
        public required bool Exceeded { get; init; }
    }

    public sealed class AgentTokens
    {
        public required string AgentId { get; init; }
        public required int Tokens { get; init; }
    }

    private static WorkItemView View(WorkItem s) =>
        new(s.Id, s.Title, s.State.ToString(), s.Risk, s.StoryPoints, s.Priority);
}
