using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// BROWSER-Agent: opens a browser-style fetch of a web page (localhost included),
/// runs static analysis against the focus areas named in the prompt, and converts
/// findings into backlog-ready story suggestions with chained prompts.
/// AI performs the analysis; humans own every decision the stories lead to.
/// </summary>
public sealed partial class AppAnalyzerAgent
{
    private readonly HttpClient _http;

    public AppAnalyzerAgent(HttpClient http) => _http = http;

    // ---------- fetch with a URL guard (SSRF-safe: http/https only, no userinfo) ----------

    /// <summary>Validates a target URL for browser-style fetch. http/https only,
    /// no embedded credentials, no javascript:/data:/file: schemes. Localhost is allowed
    /// (dev targets are a first-class use case).</summary>
    public static Uri ValidateUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{url}' is not a valid absolute URL.");
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Scheme '{uri.Scheme}' is not allowed — only http and https targets can be analyzed.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("URLs with embedded credentials are rejected.");
        return uri;
    }

    /// <summary>Fetches the page (10s timeout, 2 MB cap) and returns status + HTML.</summary>
    public async Task<(int StatusCode, string Html)> FetchAsync(Uri uri)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Add("User-Agent", "SmartAgent-Browser-Agent/1.0 (+analysis)");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        var html = await resp.Content.ReadAsStringAsync(cts.Token);
        if (html.Length > 2_000_000) html = html[..2_000_000];   // analysis cap
        return ((int)resp.StatusCode, html);
    }

    // ---------- static analysis ----------

    /// <summary>Pure function: analyzes HTML against the focus areas extracted from the prompt.</summary>
    public IReadOnlyList<AppFinding> Analyze(string html, string focusPrompt)
    {
        var focus = FocusAreas(focusPrompt);
        var findings = new List<AppFinding>();
        void Add(string area, string severity, string obs, string sug)
        {
            findings.Add(new AppFinding
            {
                Area = area, Severity = severity, Observation = obs, Suggestion = sug,
                PromptFocused = focus.Contains(area, StringComparer.OrdinalIgnoreCase)
            });
        }

        // document basics
        var title = TitleRegex().Match(html).Groups[1].Value.Trim();
        if (title.Length == 0) Add("SEO", "High", "The page has no <title> — browsers and search engines cannot label it.", "Add a descriptive <title> that names the app and the page's purpose.");
        else if (title.Length > 65) Add("SEO", "Low", $"The <title> is {title.Length} characters — likely truncated in search results.", "Shorten the title to under 65 characters while keeping it descriptive.");

        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase) || !LangRegex().IsMatch(html))
            Add("Accessibility", "Medium", "The <html> element has no lang attribute — screen readers cannot pick a pronunciation language.", "Add the correct lang attribute (e.g. lang=\"en\") to <html>.");

        if (!html.Contains("viewport", StringComparison.OrdinalIgnoreCase))
            Add("Performance", "Medium", "No viewport meta tag — the page cannot scale correctly on mobile devices.", "Add <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">.");

        // headings
        var h1s = H1Regex().Matches(html).Count;
        if (h1s == 0) Add("Accessibility", "Medium", "No <h1> heading — assistive users cannot orient on the page.", "Add exactly one <h1> describing the page's main purpose.");
        else if (h1s > 1) Add("Accessibility", "Low", $"{h1s} <h1> headings — heading hierarchy is ambiguous.", "Keep a single <h1> and demote the others to <h2>+.");

        // security
        var inlineHandlers = InlineHandlerRegex().Matches(html).Count;
        if (inlineHandlers > 0)
            Add("Security", "Medium", $"{inlineHandlers} inline event handler(s) (onclick=, onchange=, …) — behavior is not auditable and encourages DOM-XSS patterns.", "Move handlers into a CSP-compatible external script using addEventListener.");
        if (MixedContentRegex().IsMatch(html))
            Add("Security", "High", "The page references http:// resources from an https page (mixed content) — browsers may block or warn.", "Serve all subresources over https.");
        var unsafeBlanks = UnsafeBlankRegex().Matches(html).Count;
        if (unsafeBlanks > 0)
            Add("Security", "Medium", $"{unsafeBlanks} target=\"_blank\" link(s) without rel=\"noopener\" — the linked page can control this window (tabnabbing).", "Add rel=\"noopener noreferrer\" to every target=\"_blank\" link.");
        if (JsUrlLinkRegex().IsMatch(html))
            Add("Security", "High", "Links with javascript: URLs detected — classic XSS injection surface and break screen readers.", "Replace javascript: hrefs with real routes plus event listeners.");

        // links / navigation
        var deadLinks = DeadLinkRegex().Matches(html).Count;
        if (deadLinks > 0)
            Add("Navigation", "Medium", $"{deadLinks} placeholder link(s) with href=\"#\" — dead ends in the navigation.", "Point navigation links at real routes or remove them.");

        // forms / accessibility
        var labelless = LabellessInputRegex().Matches(html).Count;
        if (labelless > 0)
            Add("Forms", "Medium", $"{labelless} form input(s) without an associated <label> or title — screen readers announce them nameless.", "Associate every input with a <label for> or aria-label.");
        var altless = AltlessImgRegex().Matches(html).Count;
        if (altless > 0)
            Add("Accessibility", "Medium", $"{altless} <img> without alt text — screen readers skip or garble them.", "Add meaningful alt attributes (alt=\"\" for decorative images).");

        // performance signals
        var scripts = ScriptRegex().Matches(html).Count;
        if (scripts > 6)
            Add("Performance", "Low", $"{scripts} <script> tags — each is a separate request and parse cost.", "Bundle or defer scripts; consider a module loader.");
        var text = TagStripRegex().Replace(ScriptBlockRegex().Replace(html, " "), " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 60 && scripts < 3)
            Add("Content", "Low", $"Only ~{words} words of content — the page may not communicate its purpose.", "Add explanatory copy for the page's primary goal.");

        // prompt-focused observations that need no deep crawl
        if (focus.Contains("navigation", StringComparer.OrdinalIgnoreCase))
        {
            var navCount = NavRegex().Matches(html).Count;
            if (navCount == 0)
                Add("Navigation", "Medium", "No <nav> landmark element — the primary navigation is not machine-identifiable.", "Wrap the primary menu in a <nav> element.");
        }
        if (focus.Contains("forms", StringComparer.OrdinalIgnoreCase) && html.Contains("<form", StringComparison.OrdinalIgnoreCase) && !html.Contains("required", StringComparison.OrdinalIgnoreCase))
            Add("Forms", "Low", "A <form> exists but no required-field hints — users can submit incomplete data.", "Mark essential inputs required and validate server-side too.");

        return findings;
    }

    private static HashSet<string> FocusAreas(string prompt) =>
        new(prompt.Split(new[] { ' ', ',', '.', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.TrimEnd(':', '-').ToLowerInvariant())
            .Where(w => FocusKeywords.Contains(w)), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FocusKeywords =
    [
        "accessibility", "security", "performance", "seo", "content", "navigation",
        "forms", "mobile", "ux", "links", "layout"
    ];

    // ---------- findings → story suggestions (chained prompts) ----------

    /// <summary>Converts findings into backlog-ready story suggestions.
    /// Security findings become High-risk stories so human gates stay mandatory.</summary>
    public IReadOnlyList<StorySuggestion> SuggestStories(IReadOnlyList<AppFinding> findings, string url)
    {
        var suggestions = new List<StorySuggestion>();
        foreach (var f in findings.OrderBy(f => SeverityRank(f.Severity)))
        {
            var risk = f.Severity == "High" || f.Area == "Security" ? "High" : f.Severity switch
            {
                "Medium" => "Medium", _ => "Low"
            };
            var priority = f.Severity == "High" ? 1 : f.Severity == "Medium" ? 2 : 3;
            var points = f.Severity == "High" ? 5 : f.Severity == "Medium" ? 3 : 2;

            suggestions.Add(new StorySuggestion
            {
                SuggestedTitle = $"[{f.Area}] {Short(f.Observation, 60)}",
                Description = $"App analysis of {url} (focus: {f.Area}, severity {f.Severity}) found: {f.Observation}",
                AcceptanceCriteria =
                [
                    $"On {url}, the issue is resolved: {Short(f.Observation, 120)}",
                    $"Fix verified: {f.Suggestion}",
                    "No regression in the surrounding page functionality",
                    "Change reviewed by a human developer before build"
                ],
                Risk = risk,
                StoryPoints = points,
                Priority = priority,
                ChainedPrompt =
                    $"Target: {url}. Fix this {f.Area.ToLowerInvariant()} finding ({f.Severity} severity). " +
                    $"Problem: {f.Observation} " +
                    $"Required fix: {f.Suggestion} " +
                    $"Acceptance: {f.Suggestion} verified on {url}, no functional regressions, human code review before build."
            });
        }
        return suggestions;
    }

    public static int SeverityRank(string severity) => severity switch { "High" => 0, "Medium" => 1, _ => 2 };
    private static string Short(string s, int len) => s.Length <= len ? s : s[..(len - 1)] + "…";

    // ---------- regexes ----------

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"<html[^>]*\blang\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex LangRegex();
    [GeneratedRegex(@"<h1\b", RegexOptions.IgnoreCase)]
    private static partial Regex H1Regex();
    [GeneratedRegex(@"\bon[a-z]+\s*=\s*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();
    [GeneratedRegex(@"(?:src|href)\s*=\s*[""']http://", RegexOptions.IgnoreCase)]
    private static partial Regex MixedContentRegex();
    [GeneratedRegex(@"target\s*=\s*[""']_blank[""'](?![^>]*rel\s*=\s*[""'][^""']*noopener)", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeBlankRegex();
    [GeneratedRegex(@"href\s*=\s*[""']javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex JsUrlLinkRegex();
    [GeneratedRegex(@"href\s*=\s*[""']#[""']", RegexOptions.IgnoreCase)]
    private static partial Regex DeadLinkRegex();
    [GeneratedRegex(@"<input\b(?![^>]*\btype\s*=\s*[""']hidden)(?![^>]*\b(?:aria-label|id=))(?![^>]*>[\s\S]*?</label>)", RegexOptions.IgnoreCase)]
    private static partial Regex LabellessInputRegex();
    [GeneratedRegex(@"<img\b(?![^>]*\balt\s*=)", RegexOptions.IgnoreCase)]
    private static partial Regex AltlessImgRegex();
    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();
    [GeneratedRegex(@"<nav\b", RegexOptions.IgnoreCase)]
    private static partial Regex NavRegex();
    [GeneratedRegex(@"<(script|style)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagStripRegex();
}
