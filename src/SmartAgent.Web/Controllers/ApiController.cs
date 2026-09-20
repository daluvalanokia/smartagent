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
    IConfiguration config) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- CI/CD loop ----------

    /// <summary>Runs a full validation for a target and returns the chained prompt plus continuity diff.</summary>
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
            Notes = body.Notes
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

public sealed class TargetSummaryDto
{
    public string TargetKey { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public DateTimeOffset LastRunUtc { get; set; }
    public int OpenFindings { get; set; }
    public int PendingFixVerification { get; set; }
}
