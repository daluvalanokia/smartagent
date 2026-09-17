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
        var result = await builder.BuildAsync(Snap(), [F()], RunOptions.Default, CancellationToken.None);

        Assert.Contains("Objective", result.Prompt);
        Assert.Contains("Required changes", result.Prompt);
        Assert.Contains("Acceptance criteria", result.Prompt);
        Assert.Contains("a.cs", result.Prompt);
        Assert.Equal(1, result.TargetFindings.Count);
    }

    [Fact]
    public async Task Empty_scope_produces_placeholder_title()
    {
        var builder = new PromptBuilder(new OfflineClient());
        var result = await builder.BuildAsync(Snap(), [], RunOptions.Default, CancellationToken.None);
        Assert.Contains("No actionable findings", result.Title);
    }
}
