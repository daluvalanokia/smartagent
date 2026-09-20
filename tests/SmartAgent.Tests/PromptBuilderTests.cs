using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Integrations.AI;
using Xunit;

namespace SmartAgent.Tests;

public class PromptBuilderTests
{
    private sealed class OfflineClient : IChatCompletionClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(string s, string u, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static SourceSnapshot Snap() => new()
    {
        SourceType = SourceType.ZipArchive, SourceName = "demo", SourceDetail = "2 files",
        Files = [new SourceFile { Path = "a.cs", Content = "x" }]
    };

    private static Finding F() => new()
    {
        Id = "F001", Rule = "empty-catch", Title = "Swallowed exception", Description = "d",
        Category = FindingCategory.ErraticBehavior, Severity = Severity.High, FilePath = "a.cs", Line = 3,
        Evidence = "catch (Exception ex) { }", SuggestedAction = "log + rethrow"
    };

    [Fact]
    public async Task Falls_back_to_deterministic_template_when_ai_offline()
    {
        var builder = new PromptBuilder(new OfflineClient());
        var result = await builder.BuildAsync(Snap(), [F()], RunOptions.Default, null, Guid.NewGuid(), CancellationToken.None);

        Assert.Contains("Objective", result.Prompt);
        Assert.Contains("Required changes", result.Prompt);
        Assert.Contains("Acceptance criteria", result.Prompt);
        Assert.Contains("a.cs", result.Prompt);
        Assert.Contains("Next steps (CI/CD loop)", result.Prompt);
        Assert.Equal(1, result.TargetFindings.Count);
    }

    [Fact]
    public async Task Empty_scope_produces_placeholder_title()
    {
        var builder = new PromptBuilder(new OfflineClient());
        var result = await builder.BuildAsync(Snap(), [], RunOptions.Default, null, Guid.NewGuid(), CancellationToken.None);
        Assert.Contains("No actionable findings", result.Title);
    }

    [Fact]
    public async Task Chained_prompt_references_previous_run_and_regressions()
    {
        var continuity = new ContinuityReport
        {
            TargetKey = "site:demo.test", TargetName = "demo.test", RunNumber = 4,
            PreviousRunUtc = DateTimeOffset.UtcNow.AddDays(-2),
            PreviousPromptTitle = "Improve stability: erratic behavior (2 scoped finding(s))",
            RegressionFindings =
            [
                new TrackedFinding { Fingerprint = "ABC", Rule = "dead-link", Title = "Dead link", FilePath = "index.html", LastLine = 12,
                    FeedbackStatus = FeedbackStatus.Fixed, FeedbackNote = "claimed fixed in PR 42" }
            ],
            PersistingFindings =
            [
                new TrackedFinding { Fingerprint = "DEF", Rule = "xss-sink", Title = "XSS sink", FilePath = "app.js", LastLine = 5, Occurrences = 3 }
            ]
        };

        // the scoped finding itself is persisting (seen 3 runs) so the tag appears in Required changes
        var scoped = F();
        scoped.Continuity = ContinuityStatus.Persisting;
        scoped.Occurrences = 3;

        var builder = new PromptBuilder(new OfflineClient());
        var result = await builder.BuildAsync(Snap(), [scoped], RunOptions.Default, continuity, Guid.NewGuid(), CancellationToken.None);

        Assert.Contains("run #4", result.Prompt);
        Assert.Contains("REGRESSIONS", result.Prompt);
        Assert.Contains("persisting, seen 3 run(s)", result.Prompt);
        Assert.Contains("Verification of previous fixes", result.Prompt);
        Assert.Contains("/feedback", result.Prompt);
        Assert.Contains("run #4", result.Title);
    }
}
