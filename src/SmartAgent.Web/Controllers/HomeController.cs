using Microsoft.AspNetCore.Mvc;
using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Web.Models;
using SmartAgent.Web.Services;

namespace SmartAgent.Web.Controllers;

public class HomeController(SmartAgentEngine engine, RunStore runStore, PromptEvaluator promptEvaluator, SaaelOrchestrator saael, ILogger<HomeController> logger) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new RunInput());

    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(RunInput input, CancellationToken ct)
    {
        if (input.ScopeLimit is < 1 or > 8)
            ModelState.AddModelError(nameof(input.ScopeLimit), "Scope limit must be between 1 and 8.");

        Stream? zipStream = null;
        try
        {
            SourceRequest request;
            switch (input.SourceType)
            {
                case SourceType.ZipArchive:
                    if (input.ZipFile is null || input.ZipFile.Length == 0)
                    {
                        ModelState.AddModelError(nameof(input.ZipFile), "Upload a ZIP archive of the app source.");
                        return View("Index", input);
                    }
                    zipStream = input.ZipFile.OpenReadStream();
                    request = new SourceRequest { SourceType = SourceType.ZipArchive, ZipStream = zipStream };
                    break;

                case SourceType.GitHub:
                    if (string.IsNullOrWhiteSpace(input.GitHubUrl))
                    {
                        ModelState.AddModelError(nameof(input.GitHubUrl), "Enter the GitHub repository URL.");
                        return View("Index", input);
                    }
                    request = new SourceRequest { SourceType = SourceType.GitHub, Url = input.GitHubUrl };
                    break;

                case SourceType.Website:
                    if (string.IsNullOrWhiteSpace(input.WebsiteUrl))
                    {
                        ModelState.AddModelError(nameof(input.WebsiteUrl), "Enter the website URL.");
                        return View("Index", input);
                    }
                    request = new SourceRequest { SourceType = SourceType.Website, Url = input.WebsiteUrl };
                    break;

                default:
                    return BadRequest("Unsupported source type.");
            }

            var options = new RunOptions
            {
                ScopeLimit = input.ScopeLimit,
                Focus = input.Focus,
                Notes = input.Notes
            };

            var result = await engine.RunAsync(request, options, ct);
            runStore.Save(result);
            return RedirectToAction(nameof(Result), new { id = result.RunId });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Index", input);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Index", input);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Source fetch failed");
            ModelState.AddModelError(string.Empty, $"Could not fetch the source: {ex.Message}");
            return View("Index", input);
        }
        catch (TaskCanceledException)
        {
            ModelState.AddModelError(string.Empty, "The fetch timed out. Try a smaller source or check the URL.");
            return View("Index", input);
        }
        finally
        {
            zipStream?.Dispose();
        }
    }

    [HttpGet]
    public IActionResult Result(Guid id)
    {
        var result = runStore.Get(id);
        return result is null ? NotFound("Run not found (results are kept in memory for the last 50 runs).") : View(result);
    }

    [HttpGet]
    public IActionResult Download(Guid id)
    {
        var result = runStore.Get(id);
        if (result is null) return NotFound("Run not found.");

        var sb = new System.Text.StringBuilder();
        foreach (var p in result.Prompts)
        {
            sb.AppendLine($"# {p.Title}");
            sb.AppendLine($"(composer: {p.Composer})");
            sb.AppendLine();
            sb.AppendLine(p.Prompt);
            sb.AppendLine(new string('-', 60));
            sb.AppendLine();
        }
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/plain; charset=utf-8",
            $"smartagent-run-{id}.txt");
    }

    // ---------- parallel prompt evaluation (scope/depth/width algorithm) ----------

    [HttpGet]
    public IActionResult Evaluate() => View(new PromptEvaluationInput());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(PromptEvaluationInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Trim().Length < 20)
        {
            ModelState.AddModelError(nameof(input.Prompt), "Paste a prompt of at least 20 characters to evaluate.");
            return View(input);
        }

        try
        {
            var options = new EvaluationOptions
            {
                MaxThreads = input.MaxThreads is >= 1 and <= 32 ? input.MaxThreads.Value : EvaluationOptions.Default.MaxThreads
            };
            var report = await promptEvaluator.EvaluateAsync(input.Prompt, options, null, ct);
            return View("EvaluateResult", report);
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(input.Prompt), ex.Message);
            return View(input);
        }
    }

    // ---------- SAAEL: AI-orchestrated agile lifecycle demo ----------

    [HttpGet]
    public IActionResult Saael() => View(new SaaelInput());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Saael(SaaelInput input, string actor = "owner")
    {
        if (string.IsNullOrWhiteSpace(input.Idea) || input.Idea.Trim().Length < 20)
        {
            ModelState.AddModelError(nameof(input.Idea), "Describe the business idea in at least 20 characters.");
            return View(input);
        }

        var steps = new List<SaaelStep>();
        void Step(string stage, string who, string what, string result) =>
            steps.Add(new SaaelStep(stage, who, what, result));

        // Stage 0-6: BA decomposition + sprint proposal (recommendation only)
        var plan = saael.SubmitIdea(input.Idea);
        Step("Requirements", "BA-Agent", "idea → stories + acceptance criteria + ambiguities",
            $"{plan.Stories.Count} stories, {plan.Ambiguities.Count} ambiguities raised for the human BA");
        Step("Sprint planning", "PM-Agent", "backlog → sprint proposal",
            $"{plan.SprintProposal.StoryIds.Count} stories / {plan.SprintProposal.TotalPoints} points — PROPOSAL, PM+PO review required");
        saael.DecideSprint(plan.SprintProposal.ProposalId, actor, approve: true);
        Step("Sprint planning", $"human ({actor} as PM+PO)", "sprint proposal decision", "Approved");

        // Walk the FIRST story through the lifecycle. AI executes technical steps;
        // every human gate is recorded as an explicit human decision.
        var story = plan.Stories[0];
        var walk = new List<string>();
        var scenarioCount = 0;
        for (var i = 0; i < 40; i++)
        {
            var before = saael.Backlog().First(x => x.Id == story.Id).State;
            if (before is LifecycleState.Monitoring or LifecycleState.Completed) break;
            var r = saael.Advance(story.Id, actor);
            walk.Add($"[{before}] {r.Status}: {r.Message}");

            if (r.Status == "blocked")
            {
                if (r.Message.Contains("ambiguities", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var amb in plan.Ambiguities.Where(a => a.StoryId == story.Id).ToList())
                        saael.ResolveAmbiguity(amb.Id, "Measured criterion agreed with the business owner (demo resolution).", actor);
                    walk.Add($"[human] {actor} as Business Analyst resolved the story's ambiguities");
                }
                else if (r.Message.Contains("change proposal", StringComparison.OrdinalIgnoreCase))
                {
                    var cp = saael.ProposeChange(story.Id);
                    saael.DecideChange(story.Id, actor, "Accepted");
                    var scenarios = saael.GenerateQaScenarios(story.Id);
                    scenarioCount = scenarios.Count;
                    walk.Add($"[DEV-Agent] change proposal: {cp.FilesAffected.Count} file(s), risk {cp.Risk}, {cp.SuggestedUnitTests} unit tests suggested");
                    walk.Add($"[QA-Agent] {scenarioCount} test scenarios generated (6 categories + defect replay)");
                    walk.Add($"[human] {actor} as Development Manager accepted the proposal");
                }
                continue;
            }

            if (r.Status == "awaiting-approval" && r.Approval is { } apr)
            {
                foreach (var role in apr.RequiredRoles)
                    saael.DecideApproval(apr.Id, role, actor, approved: true, "demo sign-off");
                walk.Add($"[human] {actor} approved gate {apr.FromState} → {apr.ToState} as {string.Join(" + ", apr.RequiredRoles)}");
                var after = saael.Advance(story.Id, actor);
                walk.Add($"[{apr.FromState}] {after.Status}: {after.Message}");
                if (after.Status != "transitioned") break;
            }
            else if (r.Status != "transitioned")
            {
                break;
            }
        }

        Step("QA", "QA-Agent", "test scenario generation",
            $"{scenarioCount} scenarios across 6 categories (incl. defect replay)");
        var readiness = saael.Readiness(story.Id);
        Step("Release readiness", "Release-Engine", "readiness analysis", readiness.Summary);
        var retro = saael.Retrospective();
        Step("Retrospective", "Learning-Agent", "data-driven retro", retro[0].Observation);

        var vm = new SaaelDemoViewModel
        {
            Idea = input.Idea,
            Plan = plan,
            Walk = walk,
            Readiness = readiness,
            Insights = retro,
            Tokens = saael.Tokens(),
            Governance = saael.Governance().TakeLast(40).Reverse().ToList()
        };
        return View("SaaelResult", vm);
    }
}
