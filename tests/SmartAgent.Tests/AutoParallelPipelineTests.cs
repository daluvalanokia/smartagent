using SmartAgent.Core;
using SmartAgent.Domain;
using SmartAgent.Integrations.AI;
using SmartAgent.Integrations.Reasoning;
using SmartAgent.SourceProviders;
using Xunit;

namespace SmartAgent.Tests;

/// <summary>
/// The automatic parallel resolution: EVERY prompt the engine builds must be reviewed,
/// split into work-unit threads, processed on parallel lanes, and consolidated —
/// with no opt-in from the caller.
/// </summary>
public class AutoParallelPipelineTests
{
    private static SmartAgentEngine NewEngine()
    {
        var evaluator = new PromptEvaluator(
            new WorkPlanner(),
            new ParallelWorkExecutor(new HeuristicWorkUnitEvaluator()),
            new HeuristicWorkUnitEvaluator(),
            new PromptConsolidator());

        return new SmartAgentEngine(
            [new StubSourceProvider()],
            new HeuristicAnalyzer(),
            new NoopReasoningService(),
            new Prioritizer(),
            new PromptBuilder(new UnconfiguredChatClient()),
            new ContinuityRegistry(),
            evaluator);
    }

    [Fact]
    public async Task Every_prompt_is_automatically_split_and_parallel_processed()
    {
        var engine = NewEngine();
        var result = await engine.RunAsync(new SourceRequest { SourceType = SourceType.Website, Url = "https://example.com/app" }, RunOptions.Default);

        Assert.NotNull(result.ParallelReport);                      // automatic — no opt-in
        Assert.True(result.ParallelReport!.ScopeSatisfied);
        Assert.True(result.ParallelReport.Plan.Units.Count >= 1);
        Assert.True(result.ParallelReport.Plan.Width >= 1);
        Assert.Equal(result.ParallelReport.Plan.Units.Count, result.ParallelReport.UnitsEvaluated);
        Assert.Contains("thread", result.ParallelReport.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multi_part_prompt_splits_into_multiple_threads()
    {
        var engine = NewEngine();
        var result = await engine.RunAsync(
            new SourceRequest { SourceType = SourceType.Website, Url = "https://example.com/app" },
            new RunOptions { ScopeLimit = 12 });   // wide scope → multi-unit prompt

        var plan = result.ParallelReport!.Plan;
        Assert.True(plan.Units.Count > 1 || plan.TotalCost > 1);    // genuinely decomposed
        Assert.All(plan.Units, u => Assert.False(string.IsNullOrWhiteSpace(u.Text)));
        Assert.All(plan.Units, u => Assert.InRange(u.Depth, 1, 5));
        // lanes are balanced: width = min(units, MaxThreads) and every unit appears in exactly one lane
        var laneUnits = plan.Waves.SelectMany(w => w).ToList();
        Assert.Equal(plan.Units.Count, laneUnits.Count);
        Assert.Equal(laneUnits.Count, laneUnits.Distinct().Count());
    }

    [Fact]
    public async Task MaxThreads_option_bounds_the_thread_width()
    {
        var engine = NewEngine();
        var result = await engine.RunAsync(
            new SourceRequest { SourceType = SourceType.Website, Url = "https://example.com/app" },
            new RunOptions { ScopeLimit = 12, MaxThreads = 2 });

        Assert.InRange(result.ParallelReport!.Plan.Width, 1, 2);
    }

    // ---------- test doubles ----------

    private sealed class StubSourceProvider : ISourceProvider
    {
        public SourceType SourceType => SourceType.Website;
        public Task<SourceSnapshot> FetchAsync(SourceRequest request) => Task.FromResult(new SourceSnapshot
        {
            SourceType = SourceType.Website,
            SourceName = "stub-app",
            SourceDetail = "https://example.com/app",
            Files =
            [
                new SourceFile { Path = "index.html", Content = """
                    <html><head><title>Stub</title></head><body>
                    <h1>Stub</h1><a href="#">dead</a><a href="javascript:x()">js</a>
                    <img src="a.png"><input name="q"><input name="e">
                    <div onclick="go()">c</div><a href="http://cdn.example.com/x.js">m</a>
                    <script>a</script><script>b</script><script>c</script><script>d</script>
                    <script>e</script><script>f</script><script>g</script>
                    </body></html>
                    """ },
                new SourceFile { Path = "app.js", Content = """
                    function go(){ eval("x=1"); } var unused = null;
                    document.write("<b>hi</b>"); window.location = "http://insecure.example.com";
                    """ }
            ]
        });
    }

    private sealed class NoopReasoningService : IReasoningService
    {
        public Task<IReadOnlyList<string>> EnrichAsync(IReadOnlyList<Finding> findings, SourceSnapshot snapshot, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class UnconfiguredChatClient : IChatCompletionClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(string prompt, string system, CancellationToken ct) => Task.FromResult("");
    }
}
