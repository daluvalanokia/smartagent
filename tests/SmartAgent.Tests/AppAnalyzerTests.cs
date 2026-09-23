using System.Net;
using SmartAgent.Core;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class AppAnalyzerTests
{
    private const string BrokenHtml = """
        <html>
        <head><title>This title is way too long and will be truncated in search results everywhere</title></head>
        <body>
            <h1>First heading</h1>
            <h1>Second heading</h1>
            <nav><a href="#">Home</a><a href="#" onclick="go('x')">Docs</a>
            <a href="http://cdn.example.com/lib.js">lib</a>
            <a href="javascript:submitForm()">submit</a>
            <a href="http://other.example.com/page" target="_blank">other</a></nav>
            <form action="/save" method="post">
                <input type="text" name="q" />
                <input type="email" name="e" />
                <input type="password" name="p" />
                <button type="submit">go</button>
            </form>
            <img src="hero.png">
            <img src="logo.png">
            <script>var x = 1;</script>
            <script>var y = 2;</script>
            <script>var z = 3;</script>
            <script>var a = 4;</script>
            <script>var b = 5;</script>
            <script>var c = 6;</script>
            <script>var d = 7;</script>
        </body>
        </html>
        """;

    // ---------- URL guard (SSRF-safe) ----------

    [Theory]
    [InlineData("javascript:alert(1)")]          // XSS-style scheme
    [InlineData("data:text/html,hi")]             // data URI
    [InlineData("file:///etc/passwd")]            // local file exfil
    [InlineData("ftp://example.com/file")]        // non-http scheme
    [InlineData("not a url at all")]
    public void Guard_rejects_non_http_targets(string url) =>
        Assert.Throws<ArgumentException>(() => AppAnalyzerAgent.ValidateUrl(url));

    [Theory]
    [InlineData("https://example.com/app")]
    [InlineData("http://localhost:5199/")]         // localhost is a first-class target
    [InlineData("http://127.0.0.1:5199/Home")]
    [InlineData("https://app.example.com:8443/x")]
    public void Guard_allows_http_https_and_localhost(string url) =>
        Assert.Equal(url, AppAnalyzerAgent.ValidateUrl(url).ToString());

    [Fact]
    public void Guard_rejects_embedded_credentials() =>
        Assert.Throws<ArgumentException>(() => AppAnalyzerAgent.ValidateUrl("http://user:pass@example.com/"));

    // ---------- static analysis ----------

    [Fact]
    public void Analyze_finds_the_expected_issue_classes()
    {
        var agent = new AppAnalyzerAgent(new HttpClient());
        var findings = agent.Analyze(BrokenHtml, "focus on accessibility, security, navigation and forms");

        Assert.Contains(findings, f => f.Area == "SEO" && f.Severity == "Low" && f.Observation.Contains("truncated"));
        Assert.Contains(findings, f => f.Area == "Accessibility" && f.Observation.Contains("lang"));
        Assert.Contains(findings, f => f.Observation.Contains("2 <h1>"));
        Assert.Contains(findings, f => f.Area == "Security" && f.Observation.Contains("inline event handler"));
        Assert.Contains(findings, f => f.Area == "Security" && f.Observation.Contains("mixed content"));
        Assert.Contains(findings, f => f.Area == "Security" && f.Observation.Contains("javascript:"));
        Assert.Contains(findings, f => f.Area == "Security" && f.Observation.Contains("noopener"));
        Assert.Contains(findings, f => f.Area == "Navigation" && f.Observation.Contains("placeholder link"));
        Assert.Contains(findings, f => f.Area == "Forms" && f.Observation.Contains("without an associated"));
        Assert.Contains(findings, f => f.Area == "Accessibility" && f.Observation.Contains("alt text"));
        Assert.Contains(findings, f => f.Area == "Performance" && f.Observation.Contains("7 <script>"));
        // prompt focus flags
        Assert.All(findings.Where(f => f.Area is "Navigation" or "Forms"), f => Assert.True(f.PromptFocused));
        Assert.DoesNotContain(findings.Where(f => f.PromptFocused), f => f.Area is "SEO" or "Content");
    }

    [Fact]
    public void Analyze_clean_page_produces_no_false_positives()
    {
        var agent = new AppAnalyzerAgent(new HttpClient());
        var clean = """
            <!DOCTYPE html>
            <html lang="en">
            <head><title>SmartAgent</title><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body><h1>SmartAgent</h1><nav><a href="/runs">Runs</a></nav>
            <form><label for="q">Search</label><input id="q" required></form>
            <img src="logo.png" alt="SmartAgent logo">
            <p>SmartAgent runs validated prompt pipelines for engineering teams: every idea becomes a decomposed backlog, every story walks a gated lifecycle with human approvals, and every agent action lands in a governance audit trail. Teams use it to evaluate prompts in parallel, chain fixes through CI, and keep releases safe with rollback policies and token budgets.</p>
            </body></html>
            """;
        var findings = agent.Analyze(clean, "general quality");
        Assert.Empty(findings);
    }

    // ---------- findings → story suggestions ----------

    [Fact]
    public void Security_findings_become_high_risk_stories_with_chained_prompts()
    {
        var agent = new AppAnalyzerAgent(new HttpClient());
        var findings = agent.Analyze(BrokenHtml, "security");
        var suggestions = agent.SuggestStories(findings, "https://example.com/app");

        Assert.Equal(findings.Count, suggestions.Count);
        var js = suggestions.First(s => s.SuggestedTitle.Contains("javascript"));
        Assert.Equal("High", js.Risk);
        Assert.Equal(1, js.Priority);                              // severity-ranked first
        Assert.StartsWith("Target: https://example.com/app.", js.ChainedPrompt);
        Assert.All(suggestions, s => Assert.True(s.ChainedPrompt.Contains("Acceptance:")));
        Assert.All(suggestions, s => Assert.True(s.AcceptanceCriteria.Any(c => c.Contains("human"))));
        // severity ordering: high-priority (1) suggestions come first, never later
        var priorities = suggestions.Select(s => s.Priority).ToList();
        Assert.Equal(priorities.OrderBy(p => p), priorities);
    }

    // ---------- fetch + orchestrator integration (live local server) ----------

    [Fact]
    public async Task Fetch_retrieves_a_live_page()
    {
        var handler = new StubHandler("<html><head><title>Stub App</title></head><body><h1>Stub</h1></body></html>");
        var agent = new AppAnalyzerAgent(new HttpClient(handler));
        var (status, html) = await agent.FetchAsync(new Uri("http://stub.local/page"));
        Assert.Equal(200, status);
        Assert.Contains("Stub App", html);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _content;
        public StubHandler(string content) => _content = content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_content) });
    }
}
