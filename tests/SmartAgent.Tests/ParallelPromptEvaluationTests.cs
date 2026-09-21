using System.Collections.Concurrent;
using SmartAgent.Core;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class ParallelPromptEvaluationTests
{
    private const string NumberedPrompt = """
        Improve the app. Required changes:
        1. Escape the API values before inserting them into the DOM in `app.js` and `render.js` to prevent XSS.
        2. Point the dead Settings link in `index.html` to the real settings route.
        3. Replace the inline onclick handler with an addEventListener binding and add a regression test.
        """;

    private static EvaluationOptions Opts(int threads = 4, int retries = 1) => new()
    {
        MaxThreads = threads,
        RetriesPerUnit = retries,
        UnitTimeout = TimeSpan.FromSeconds(5)
    };

    // ---------- planner: scope / depth / width ----------

    [Fact]
    public void Planner_extracts_numbered_units_and_derives_width()
    {
        var plan = new WorkPlanner().Plan(NumberedPrompt, Opts());

        Assert.Equal(3, plan.Units.Count);
        Assert.Equal(3, plan.Width);               // min(units=3, maxThreads=4)
        Assert.All(plan.Units, u => Assert.Equal("numbered item", u.Source));
        Assert.Contains(plan.Units, u => u.FileTargets.Contains("app.js"));
        Assert.Equal(plan.Units.Sum(u => u.Cost), plan.TotalCost);
    }

    [Fact]
    public void Width_is_capped_by_max_threads()
    {
        var plan = new WorkPlanner().Plan(NumberedPrompt, Opts(threads: 2));
        Assert.Equal(2, plan.Width);
        Assert.True(plan.Waves.Count >= 2);        // 3 units on 2 threads → at least 2 waves
    }

    [Fact]
    public void Prose_prompts_fall_back_to_sentence_chunks()
    {
        var prose = string.Join(' ', Enumerable.Range(0, 40)
            .Select(i => $"Sentence number {i} carries evaluation work about validation logic and details."));

        var plan = new WorkPlanner().Plan(prose, Opts());
        Assert.True(plan.Units.Count >= 3);
        Assert.Equal("sentence group", plan.Units[0].Source);
        Assert.Equal(plan.Units.Count, plan.Units.Select(u => u.Id).Distinct().Count());
    }

    [Fact]
    public void Depth_grows_with_risk_signals_and_file_spread()
    {
        var planner = new WorkPlanner();
        var trivial = planner.Plan("1. Fix the typo in the button label.", Opts());
        var complex = planner.Plan("1. Refactor the database schema migration and concurrent security validation pipeline in `a.cs` and `b.cs`, covering regression integrity checks and audit workflow integration across the full architecture.", Opts());

        Assert.True(complex.Units[0].Depth > trivial.Units[0].Depth);
        Assert.True(complex.Units[0].Cost > trivial.Units[0].Cost);
        Assert.True(complex.AverageDepth > trivial.AverageDepth);
    }

    [Fact]
    public void Waves_cover_every_unit_exactly_once()
    {
        var plan = new WorkPlanner().Plan(NumberedPrompt, Opts(threads: 2));
        var waveIds = plan.Waves.SelectMany(w => w).ToList();
        Assert.Equal(plan.Units.Count, waveIds.Count);
        Assert.Equal(plan.Units.Count, waveIds.Distinct().Count());
        foreach (var w in plan.Waves) Assert.True(w.Count <= plan.Width);
    }

    // ---------- executor: parallelism, failure tolerance ----------

    private sealed class TrackedEvaluator : IWorkUnitEvaluator
    {
        public int Concurrent;
        public int MaxConcurrent;
        public int SlowUnitMs = 50;
        public string? FailUnitId;

        public async Task<UnitEvaluation> EvaluateAsync(WorkUnit unit, CancellationToken ct)
        {
            var c = Interlocked.Increment(ref Concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, c);
            try
            {
                await Task.Delay(SlowUnitMs, ct);
                if (FailUnitId == unit.Id) throw new InvalidOperationException("simulated unit failure");
                return new UnitEvaluation { UnitId = unit.Id, Complexity = 5, Effort = unit.Cost, Risk = "Medium", Attempts = 1 };
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
    }

    [Fact]
    public async Task Units_run_in_parallel_within_the_width_bound()
    {
        var evaluator = new TrackedEvaluator { SlowUnitMs = 150 };
        var planner = new WorkPlanner();
        var plan = planner.Plan(NumberedPrompt, Opts(threads: 3));
        var executor = new ParallelWorkExecutor(evaluator);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await executor.ExecuteAsync(plan, Opts(threads: 3));
        sw.Stop();

        Assert.True(evaluator.MaxConcurrent > 1);          // genuinely parallel
        Assert.True(evaluator.MaxConcurrent <= 3);         // bounded by width
        Assert.True(sw.ElapsedMilliseconds < 3 * 150);      // faster than sequential
    }

    [Fact]
    public async Task Failing_units_are_reported_not_fatal()
    {
        var evaluator = new TrackedEvaluator { FailUnitId = "W001" };
        var promptEvaluator = Build(evaluator, retries: 1);
        var report = await promptEvaluator.EvaluateAsync(NumberedPrompt, Opts(threads: 4, retries: 1));

        Assert.Equal(1, report.UnitsFailed);
        Assert.Equal(2, report.UnitsEvaluated);
        Assert.True(report.ScopeSatisfied);                // failure recorded → scope accounted
        Assert.Contains("W001", report.Results.Single(r => r.Failed).UnitId);
        Assert.Contains("re-evaluation", report.Summary);
    }

    [Fact]
    public async Task Failed_unit_retries_then_records_attempts()
    {
        var evaluator = new TrackedEvaluator { FailUnitId = "W001" };
        var report = await Build(evaluator, retries: 2).EvaluateAsync(NumberedPrompt, Opts(threads: 4, retries: 2));

        var failed = report.Results.Single(r => r.Failed);
        Assert.Equal(3, failed.Attempts);                  // initial + 2 retries
    }

    [Fact]
    public async Task Consolidation_reports_missing_units_as_scope_not_satisfied()
    {
        var planner = new WorkPlanner();
        var plan = planner.Plan(NumberedPrompt, Opts());
        var partial = plan.Units.Take(2).ToDictionary(u => u.Id,
            u => new UnitEvaluation { UnitId = u.Id, Complexity = 3, Effort = u.Cost, Risk = "Low" });

        var report = new PromptConsolidator().Consolidate(plan, partial, 100);

        Assert.False(report.ScopeSatisfied);
        Assert.Contains("Scope NOT satisfied", report.Summary);
    }

    [Fact]
    public async Task Big_prompt_of_20_units_splits_into_bounded_waves()
    {
        var prompt = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"{i}. Implement improvement step {i} in `file{i}.cs`."));
        var evaluator = new TrackedEvaluator { SlowUnitMs = 20 };

        var report = await Build(evaluator).EvaluateAsync(prompt, Opts(threads: 4));

        Assert.Equal(20, report.Plan.Units.Count);
        Assert.Equal(4, report.Plan.Width);
        Assert.True(report.Plan.Waves.Count >= 4);          // 20 units ÷ 4 threads = 4 balanced waves
        Assert.True(report.ScopeSatisfied);
        Assert.True(report.UnitsEvaluated == 20);
    }

    private static PromptEvaluator Build(IWorkUnitEvaluator evaluator, int retries = 1) =>
        new(new WorkPlanner(), new ParallelWorkExecutor(evaluator), evaluator, new PromptConsolidator());
}
