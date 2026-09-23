using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Web.Services;

namespace SmartAgent.Web.Controllers;

/// <summary>
/// CI/CD integration surface. A pipeline (GitHub Actions, Jenkins, cron...)
/// uses these endpoints as a continuous prompt loop:
///   validate → apply prompt → POST feedback → validate again → gate on regressions.
/// Optional shared-secret guard: when "SmartAgent:ApiKey" is configured, all
/// mutating endpoints require the same value in the X-Api-Key header.
/// </summary>
[ApiController]
[Route("api")]
public sealed class ApiController(
    SmartAgentEngine engine,
    RunStore runStore,
    ContinuityRegistry continuity,
    PromptEvaluator promptEvaluator,
    SaaelOrchestrator saael,
    IConfiguration config) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- CI/CD loop ----------

    /// <summary>Runs a full validation for a target and returns the chained prompt plus continuity diff.</summary>
    private static ParallelThreadSummary? ParallelDto(EvaluationReport? r) => r is null ? null : new ParallelThreadSummary
    {
        Units = r.Plan.Units.Count,
        Threads = r.Plan.Width,
        UnitsFailed = r.UnitsFailed,
        TotalEffort = r.TotalEffort,
        ScopeSatisfied = r.ScopeSatisfied,
        DominantRisk = r.DominantRisk,
        ElapsedMs = r.TotalElapsedMs,
        Summary = r.Summary
    };

    [HttpPost("validate")]
    [RequestSizeLimit(60_000_000)]
    public async Task<ActionResult<ValidateResponse>> Validate([FromBody] ValidateRequest body, CancellationToken ct)
    {
        if (!Authorized()) return Unauthorized();
        if (body is null || string.IsNullOrWhiteSpace(body.SourceRef))
            return BadRequest(new { error = "sourceRef is required (GitHub URL, website URL, or base64 ZIP for zipArchive source)." });

        var options = new RunOptions
        {
            ScopeLimit = body.ScopeLimit is > 0 and <= 8 ? body.ScopeLimit.Value : 3,
            Focus = Enum.TryParse<FocusArea>(body.Focus, ignoreCase: true, out var focus) ? focus : FocusArea.Balanced,
            Notes = body.Notes,
            MaxThreads = body.MaxThreads is >= 1 and <= 64 ? body.MaxThreads.Value : null
        };

        SourceRequest request;
        try
        {
            request = BuildRequest(body);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        PromptRunResult result;
        try
        {
            result = await engine.RunAsync(request, options, ct);
        }
        catch (Exception ex)
        {
            return Problem(title: "Validation failed", detail: ex.Message, statusCode: 502);
        }

        runStore.Save(result);

        return Ok(new ValidateResponse
        {
            RunId = result.RunId,
            TargetKey = result.TargetKey,
            RunNumber = result.RunNumber,
            Continuity = ContinuityDto.From(result.Continuity),
            Findings = result.AllFindings.Select(FindingDto.From).ToList(),
            PromptTitle = result.Prompts.FirstOrDefault()?.Title,
            Prompt = result.Prompts.FirstOrDefault()?.Prompt,
            Parallel = ParallelDto(result.ParallelReport),
            Warnings = result.Warnings.ToList()
        });
    }

    /// <summary>Fetches a stored run (prompt, findings, continuity).</summary>
    [HttpGet("runs/{runId:guid}")]
    public ActionResult<ValidateResponse> GetRun(Guid runId)
    {
        if (!Authorized()) return Unauthorized();
        var r = runStore.Get(runId);
        if (r is null) return NotFound(new { error = $"Run {runId} not found (results are kept for the last 50 runs)." });

        return Ok(new ValidateResponse
        {
            RunId = r.RunId,
            TargetKey = r.TargetKey,
            RunNumber = r.RunNumber,
            Continuity = ContinuityDto.From(r.Continuity),
            Findings = r.AllFindings.Select(FindingDto.From).ToList(),
            PromptTitle = r.Prompts.FirstOrDefault()?.Title,
            Prompt = r.Prompts.FirstOrDefault()?.Prompt,
            Parallel = ParallelDto(r.ParallelReport),
            Warnings = r.Warnings.ToList()
        });
    }

    /// <summary>
    /// Reports the outcome of applying a run's prompt. "fixed" findings are verified by the
    /// next validation run; "wont_fix" suppresses the finding; "failed_verification" escalates.
    /// </summary>
    [HttpPost("runs/{runId:guid}/feedback")]
    public ActionResult<FeedbackResponse> Feedback(Guid runId, [FromBody] FeedbackRequest body)
    {
        if (!Authorized()) return Unauthorized();
        var run = runStore.Get(runId);
        if (run is null) return NotFound(new { error = $"Run {runId} not found." });
        if (body?.Feedback is null || body.Feedback.Count == 0)
            return BadRequest(new { error = "feedback[] with at least one {fingerprint, status} is required." });

        var applied = 0;
        var skipped = new List<string>();
        foreach (var item in body.Feedback)
        {
            if (!Enum.TryParse<FeedbackStatus>(Normalize(item.Status), ignoreCase: true, out var status))
            {
                skipped.Add(item.Fingerprint);
                continue;
            }
            applied += continuity.ApplyFeedback(run.TargetKey, [(item.Fingerprint, status, item.Note)]);
        }

        return Ok(new FeedbackResponse
        {
            RunId = runId,
            TargetKey = run.TargetKey,
            Applied = applied,
            Skipped = skipped
        });
    }

    /// <summary>Continuity timeline for a target: run count and every tracked finding with feedback state.</summary>
    [HttpGet("targets/{targetKey}/history")]
    public ActionResult<TargetHistoryDto> History(string targetKey)
    {
        if (!Authorized()) return Unauthorized();
        var h = continuity.GetHistory(targetKey);
        if (h is null) return NotFound(new { error = $"Unknown target '{targetKey}'." });

        return Ok(new TargetHistoryDto
        {
            TargetKey = h.TargetKey,
            TargetName = h.TargetName,
            RunCount = h.RunCount,
            LastRunUtc = h.LastRunUtc,
            LastRunId = h.LastRunId,
            LastPromptTitle = h.LastPromptTitle,
            Findings = h.Findings.Select(t => new TrackedFindingDto
            {
                Fingerprint = t.Fingerprint,
                Rule = t.Rule,
                Title = t.Title,
                FilePath = t.FilePath,
                LastLine = t.LastLine,
                FeedbackStatus = t.FeedbackStatus.ToString(),
                Occurrences = t.Occurrences,
                FirstSeenRunNumber = t.FirstSeenRunNumber,
                LastSeenRunNumber = t.LastSeenRunNumber
            }).ToList()
        });
    }

    /// <summary>All known targets and their run counts (pipeline dashboard).</summary>
    [HttpGet("targets")]
    public ActionResult<List<TargetSummaryDto>> Targets()
    {
        if (!Authorized()) return Unauthorized();
        return Ok(continuity.GetAllTargets().Select(h => new TargetSummaryDto
        {
            TargetKey = h.TargetKey,
            TargetName = h.TargetName,
            RunCount = h.RunCount,
            LastRunUtc = h.LastRunUtc,
            OpenFindings = h.Findings.Count(t => t.LastSeenRunNumber == h.RunCount),
            PendingFixVerification = h.Findings.Count(t => t.FeedbackStatus == FeedbackStatus.Fixed)
        }).ToList());
    }


    /// <summary>
    /// Parallel prompt evaluation: analyzes ANY prompt, derives its scope (work units),
    /// depth and width automatically, runs the units on multiple threads in balanced
    /// waves, and consolidates the results into one report (scope-satisfaction check).
    /// </summary>
    [HttpPost("evaluate-prompt")]
    public async Task<IActionResult> EvaluatePrompt([FromBody] EvaluatePromptRequest body, CancellationToken ct)
    {
        if (!Authorized()) return Unauthorized();
        if (body is null || string.IsNullOrWhiteSpace(body.Prompt) || body.Prompt.Trim().Length < 20)
            return BadRequest(new { error = "prompt is required (at least 20 characters)." });

        var options = new EvaluationOptions
        {
            MaxThreads = body.MaxThreads is >= 1 and <= 32 ? body.MaxThreads.Value : EvaluationOptions.Default.MaxThreads,
            RetriesPerUnit = body.RetriesPerUnit is >= 0 and <= 3 ? body.RetriesPerUnit.Value : 1
        };

        try
        {
            var report = await promptEvaluator.EvaluateAsync(body.Prompt, options, null, ct);
            return Ok(new EvaluatePromptResponse
            {
                Plan = new PlanDto
                {
                    PromptPreview = report.Plan.PromptPreview,
                    Units = report.Plan.Units.Count,
                    Width = report.Plan.Width,
                    Waves = report.Plan.Waves.Count,
                    TotalCost = report.Plan.TotalCost,
                    AverageDepth = Math.Round(report.Plan.AverageDepth, 2),
                    UnitDetails = report.Plan.Units.Select(u => new PlanUnitDto
                    {
                        Id = u.Id, Order = u.Order, Source = u.Source, Depth = u.Depth, Cost = u.Cost,
                        FileTargets = u.FileTargets.ToList(), Wave = Enumerable.Range(0, report.Plan.Waves.Count).First(i => report.Plan.Waves[i].Contains(u.Id)) + 1,
                        Text = u.Text.Length <= 200 ? u.Text : u.Text[..197] + "…"
                    }).ToList()
                },
                UnitsEvaluated = report.UnitsEvaluated,
                UnitsFailed = report.UnitsFailed,
                ScopeSatisfied = report.ScopeSatisfied,
                DominantRisk = report.DominantRisk,
                TotalEffort = report.TotalEffort,
                ElapsedMs = report.TotalElapsedMs,
                Summary = report.Summary,
                Results = report.Results.Select(r => new UnitResultDto
                {
                    UnitId = r.UnitId, Failed = r.Failed, Error = r.Error,
                    Complexity = r.Complexity, Effort = r.Effort, Risk = r.Risk,
                    Findings = r.Findings.ToList(), SuggestedActions = r.SuggestedActions.ToList(),
                    Attempts = r.Attempts, ElapsedMs = r.ElapsedMs
                }).ToList()
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }


    // ---------- SAAEL: AI-orchestrated agile lifecycle (role agents + human gates) ----------

    /// <summary>Stage 0-6: business idea → stories, acceptance criteria, NFRs,
    /// ambiguities (BA gate input) and a sprint PROPOSAL pending PM+PO approval.</summary>
    [HttpPost("saael/idea")]
    public IActionResult SubmitIdea([FromBody] SaaelIdeaRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.Idea) || body.Idea.Trim().Length < 20)
            return BadRequest(new { error = "idea is required (at least 20 characters)." });
        var view = saael.SubmitIdea(body.Idea);
        return Ok(view);
    }

    /// <summary>Attempts the next lifecycle transition for a story. Human gates
    /// return status=awaiting-approval with the open approval request.</summary>
    [HttpPost("saael/advance")]
    public IActionResult Advance([FromBody] SaaelAdvanceRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.StoryId)) return BadRequest(new { error = "storyId is required." });
        try { return Ok(saael.Advance(body.StoryId, body.Actor)); }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Human BA resolves an ambiguity raised during decomposition (BA gate precondition).</summary>
    [HttpPost("saael/resolve-ambiguity")]
    public IActionResult ResolveAmbiguity([FromBody] SaaelResolveRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.AmbiguityId) || string.IsNullOrWhiteSpace(body.Resolution) || string.IsNullOrWhiteSpace(body.Actor))
            return BadRequest(new { error = "ambiguityId, resolution and actor are required." });
        try
        {
            saael.ResolveAmbiguity(body.AmbiguityId, body.Resolution, body.Actor);
            return Ok(new { status = "resolved" });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Human PM+PO decision on a sprint proposal (recommendation is never self-approved).</summary>
    [HttpPost("saael/sprint-decision")]
    public IActionResult SprintDecision([FromBody] SaaelSprintDecisionRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.ProposalId) || string.IsNullOrWhiteSpace(body.Actor))
            return BadRequest(new { error = "proposalId and actor are required." });
        try
        {
            saael.DecideSprint(body.ProposalId, body.Actor, body.Approve);
            return Ok(new { status = body.Approve ? "approved" : "rejected" });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Human Development Manager decision on a Developer Agent change proposal.</summary>
    [HttpPost("saael/change-decision/{storyId}")]
    public IActionResult ChangeDecision(string storyId, [FromBody] SaaelChangeDecisionRequest body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Actor) || string.IsNullOrWhiteSpace(body.Decision))
            return BadRequest(new { error = "actor and decision (Accepted|Modified|Rejected) are required." });
        try
        {
            saael.DecideChange(storyId, body.Actor, body.Decision);
            return Ok(new { status = body.Decision });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Records a human decision on an approval request (the human-in-the-loop gate).</summary>
    [HttpPost("saael/approve")]
    public IActionResult Approve([FromBody] SaaelApproveRequest body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ApprovalId) || string.IsNullOrWhiteSpace(body.Actor))
            return BadRequest(new { error = "approvalId and actor are required." });
        try
        {
            saael.DecideApproval(body.ApprovalId, body.Role, body.Actor, body.Approved, body.Notes ?? "");
            return Ok(new { status = "recorded" });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Developer Agent change proposal for a story (proposal only — never auto-code).</summary>
    [HttpPost("saael/proposal/{storyId}")]
    public IActionResult ProposeChange(string storyId)
    {
        try { return Ok(saael.ProposeChange(storyId)); }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>QA Agent scenario generation (normal/boundary/negative/security/performance/regression).</summary>
    [HttpPost("saael/qa-scenarios/{storyId}")]
    public IActionResult QaScenarios(string storyId)
    {
        try { return Ok(saael.GenerateQaScenarios(storyId)); }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Release-readiness engine: checks + outstanding human approvals (§15).</summary>
    [HttpGet("saael/readiness/{storyId}")]
    public IActionResult Readiness(string storyId)
    {
        try { return Ok(saael.Readiness(storyId)); }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>CI root-cause analysis: "Build failed" → cause, likely commit, affected tests, fix, confidence.</summary>
    [HttpPost("saael/ci-analyze")]
    public IActionResult CiAnalyze([FromBody] SaaelBuildLogRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.BuildLog)) return BadRequest(new { error = "buildLog is required." });
        return Ok(saelAnalyze(body.BuildLog));
    }

    private object saelAnalyze(string buildLog) => saael.AnalyzeBuildFailure(buildLog);

    /// <summary>Data-driven retrospective insights from the Learning Agent (§19).</summary>
    [HttpGet("saael/retrospective")]
    public IActionResult Retrospective() => Ok(saael.Retrospective());

    /// <summary>Progressive-delivery rollback: with actor = human-authorized;
    /// without actor = policy-authorized auto-rollback, only at level L4.</summary>
    [HttpPost("saael/rollback")]
    public IActionResult Rollback([FromBody] SaaelRollbackRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.StoryId)) return BadRequest(new { error = "storyId is required." });
        return Ok(new { result = saael.Rollback(body.StoryId, body.Reason ?? "manual", body.Actor, body.ErrorRate) });
    }

    /// <summary>BROWSER-Agent: fetch a live page (localhost allowed), analyze it against the
    /// prompt's focus areas, and get suggested stories as chained prompts (added to the backlog).</summary>
    [HttpPost("saael/analyze-app")]
    public async Task<IActionResult> AnalyzeApp([FromBody] SaaelAnalyzeAppRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.Url) || string.IsNullOrWhiteSpace(body.Prompt))
            return BadRequest(new { error = "url and prompt are required." });
        try { return Ok(await saael.AnalyzeAppAsync(body.Url, body.Prompt)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Full story state: item, open approvals, traceability chain, governance records.</summary>
    [HttpGet("saael/story/{storyId}")]
    public IActionResult Story(string storyId)
    {
        try
        {
            return Ok(new
            {
                story = saael.Backlog().FirstOrDefault(s => s.Id == storyId),
                approvals = saael.OpenApprovals(storyId),
                trace = saael.Trace(storyId),
                governance = saael.Governance().Where(g => g.InputSummary.Contains(storyId) || g.OutputSummary.Contains(storyId)).TakeLast(20)
            });
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpGet("saael/backlog")]
    public IActionResult Backlog() => Ok(saelBacklog());

    private object saelBacklog() => new { stories = saael.Backlog(), tokenLedger = saael.Tokens(), openApprovals = saael.OpenApprovals() };

    [HttpGet("saael/token-usage")]
    public IActionResult TokenUsage() => Ok(saelTokens());

    private object saelTokens() => saael.Tokens();

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "SmartAgent CI/CD prompt pipeline" });

    // ---------- helpers ----------

    private static SourceRequest BuildRequest(ValidateRequest body) => body.SourceType?.ToLowerInvariant() switch
    {
        "website" or "url" => new SourceRequest { SourceType = SourceType.Website, Url = body.SourceRef },
        "github" or "repo" => new SourceRequest { SourceType = SourceType.GitHub, Url = body.SourceRef, GitHubToken = body.GitHubToken },
        "ziparchive" or "zip" => new SourceRequest
        {
            SourceType = SourceType.ZipArchive,
            ZipStream = DecodeZip(body.SourceRef)
        },
        _ => throw new ArgumentException("sourceType must be website, github or zipArchive.")
    };

    private static MemoryStream DecodeZip(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var ms = new MemoryStream(bytes, writable: false);
        // validate it is a zip before handing it to the engine
        _ = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        ms.Position = 0;
        return ms;
    }

    private bool Authorized()
    {
        var key = config["SmartAgent:ApiKey"];
        return string.IsNullOrWhiteSpace(key) || Request.Headers.TryGetValue("X-Api-Key", out var sent) && sent == key;
    }

    private static string? Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        // accept wont_fix / wont-fix / WONTFIX / failed-verification etc.
        return status.Trim().Replace("_", "").Replace("-", "").Replace(" ", "");
    }
}

// ---------- DTOs ----------

public sealed class ValidateRequest
{
    /// <summary>website | github | zipArchive</summary>
    public string? SourceType { get; set; }
    /// <summary>URL for website/github; base64 ZIP content for zipArchive.</summary>
    public string SourceRef { get; set; } = string.Empty;
    public string? GitHubToken { get; set; }
    public int? ScopeLimit { get; set; }
    public string? Focus { get; set; }
    public string? Notes { get; set; }
    /// <summary>Upper bound on parallel threads for the automatic prompt split (default: CPU count, min 2).</summary>
    public int? MaxThreads { get; set; }
}

public sealed class ParallelThreadSummary
{
    /// <summary>True: every prompt is automatically reviewed, split, and parallel-processed.</summary>
    public bool AutoParallel { get; init; } = true;
    public int Units { get; init; }
    public int Threads { get; init; }
    public int UnitsFailed { get; init; }
    public int TotalEffort { get; init; }
    public bool ScopeSatisfied { get; init; }
    public string DominantRisk { get; init; } = "Low";
    public long ElapsedMs { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class ValidateResponse
{
    public Guid RunId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public int RunNumber { get; set; }
    public ContinuityDto? Continuity { get; set; }
    public List<FindingDto> Findings { get; set; } = [];
    public string? PromptTitle { get; set; }
    public string? Prompt { get; set; }
    public ParallelThreadSummary? Parallel { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class FindingDto
{
    public string Id { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Continuity { get; set; } = string.Empty;
    public int Occurrences { get; set; }
    public string SuggestedAction { get; set; } = string.Empty;

    public static FindingDto From(Finding f) => new()
    {
        Id = f.Id, Fingerprint = f.Fingerprint, Rule = f.Rule, Title = f.Title,
        FilePath = f.FilePath, Line = f.Line, Category = f.Category.ToString(), Severity = f.Severity.ToString(),
        Score = f.Score, Continuity = f.Continuity.ToString(), Occurrences = f.Occurrences,
        SuggestedAction = f.SuggestedAction
    };
}

public sealed class ContinuityDto
{
    public int RunNumber { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public DateTimeOffset? PreviousRunUtc { get; set; }
    public string? PreviousPromptTitle { get; set; }
    public int New { get; set; }
    public int Persisting { get; set; }
    public int Regressions { get; set; }
    public int Resolved { get; set; }
    public int Suppressed { get; set; }

    public static ContinuityDto? From(ContinuityReport? c) => c is null ? null : new ContinuityDto
    {
        RunNumber = c.RunNumber,
        TargetKey = c.TargetKey,
        PreviousRunUtc = c.PreviousRunUtc,
        PreviousPromptTitle = c.PreviousPromptTitle,
        New = c.NewFindings.Count,
        Persisting = c.PersistingFindings.Count,
        Regressions = c.RegressionFindings.Count,
        Resolved = c.ResolvedSinceLastRun.Count,
        Suppressed = c.SuppressedFindings.Count
    };
}

public sealed class FeedbackRequest
{
    public List<FeedbackItem> Feedback { get; set; } = [];
    public string? Actor { get; set; }
}

public sealed class FeedbackItem
{
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>fixed | wont_fix | failed_verification</summary>
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class FeedbackResponse
{
    public Guid RunId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public int Applied { get; set; }
    public List<string> Skipped { get; set; } = [];
}

public sealed class TargetHistoryDto
{
    public string TargetKey { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public DateTimeOffset LastRunUtc { get; set; }
    public Guid? LastRunId { get; set; }
    public string? LastPromptTitle { get; set; }
    public List<TrackedFindingDto> Findings { get; set; } = [];
}

public sealed class TrackedFindingDto
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int LastLine { get; set; }
    public string FeedbackStatus { get; set; } = string.Empty;
    public int Occurrences { get; set; }
    public int FirstSeenRunNumber { get; set; }
    public int LastSeenRunNumber { get; set; }
}

public sealed class EvaluatePromptRequest
{
    /// <summary>Any prompt text: numbered requirements, bullets, sections or free prose.</summary>
    public string? Prompt { get; set; }
    /// <summary>Max concurrent evaluation threads; default = CPU count.</summary>
    public int? MaxThreads { get; set; }
    public int? RetriesPerUnit { get; set; }
}

public sealed class EvaluatePromptResponse
{
    public PlanDto Plan { get; set; } = new();
    public int UnitsEvaluated { get; set; }
    public int UnitsFailed { get; set; }
    public bool ScopeSatisfied { get; set; }
    public string DominantRisk { get; set; } = string.Empty;
    public int TotalEffort { get; set; }
    public long ElapsedMs { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<UnitResultDto> Results { get; set; } = [];
}

public sealed class PlanDto
{
    public string PromptPreview { get; set; } = string.Empty;
    public int Units { get; set; }
    public int Width { get; set; }
    public int Waves { get; set; }
    public int TotalCost { get; set; }
    public double AverageDepth { get; set; }
    public List<PlanUnitDto> UnitDetails { get; set; } = [];
}

public sealed class PlanUnitDto
{
    public string Id { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Source { get; set; } = string.Empty;
    public int Depth { get; set; }
    public int Cost { get; set; }
    public int Wave { get; set; }
    public List<string> FileTargets { get; set; } = [];
    public string Text { get; set; } = string.Empty;
}

public sealed class UnitResultDto
{
    public string UnitId { get; set; } = string.Empty;
    public bool Failed { get; set; }
    public string? Error { get; set; }
    public int Complexity { get; set; }
    public int Effort { get; set; }
    public string Risk { get; set; } = string.Empty;
    public List<string> Findings { get; set; } = [];
    public List<string> SuggestedActions { get; set; } = [];
    public int Attempts { get; set; }
    public long ElapsedMs { get; set; }
}

public sealed class SaaelIdeaRequest { public string? Idea { get; set; } }
public sealed class SaaelAdvanceRequest { public string? StoryId { get; set; } public string? Actor { get; set; } }
public sealed class SaaelApproveRequest
{
    public string? ApprovalId { get; set; }
    public HumanRole Role { get; set; }
    public string? Actor { get; set; }
    public bool Approved { get; set; } = true;
    public string? Notes { get; set; }
}
public sealed class SaaelBuildLogRequest { public string? BuildLog { get; set; } }
public sealed class SaaelRollbackRequest
{
    public string? StoryId { get; set; }
    public string? Reason { get; set; }
    /// <summary>Human authorizer; when null the rollback is policy-authorized (L4 only).</summary>
    public string? Actor { get; set; }
    /// <summary>When set and no actor: policy-authorized auto-rollback (L4 only, threshold 5%).</summary>
    public double? ErrorRate { get; set; }
}

public sealed class SaaelResolveRequest { public string? AmbiguityId { get; set; } public string? Resolution { get; set; } public string? Actor { get; set; } }
public sealed class SaaelSprintDecisionRequest { public string? ProposalId { get; set; } public string? Actor { get; set; } public bool Approve { get; set; } = true; }
public sealed class SaaelChangeDecisionRequest { public string? Actor { get; set; } public string? Decision { get; set; } }

public sealed class SaaelAnalyzeAppRequest { public string? Url { get; set; } public string? Prompt { get; set; } }

public sealed class TargetSummaryDto
{
    public string TargetKey { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public DateTimeOffset LastRunUtc { get; set; }
    public int OpenFindings { get; set; }
    public int PendingFixVerification { get; set; }
}
