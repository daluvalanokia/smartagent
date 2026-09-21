using Microsoft.AspNetCore.Mvc;
using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Web.Models;
using SmartAgent.Web.Services;

namespace SmartAgent.Web.Controllers;

public class HomeController(SmartAgentEngine engine, RunStore runStore, PromptEvaluator promptEvaluator, ILogger<HomeController> logger) : Controller
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
}
