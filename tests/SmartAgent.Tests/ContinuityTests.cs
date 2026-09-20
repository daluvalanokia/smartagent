using SmartAgent.Core;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class ContinuityTests
{
    private static SourceSnapshot Snap(string name = "demo.test") => new()
    {
        SourceType = SourceType.Website, SourceName = name, SourceDetail = "1 page crawled",
        Files = [new SourceFile { Path = "index.html", Content = "x" }]
    };

    private static Finding F(string id = "F001", string file = "index.html", int line = 10) => new()
    {
        Id = id, Rule = "dead-link", Title = "Dead link", Description = "d",
        Category = FindingCategory.ErraticBehavior, Severity = Severity.Medium, FilePath = file, Line = line,
        Evidence = "href=\"#\"", SuggestedAction = "point to a real route"
    };

    [Fact]
    public void Target_keys_are_stable_per_source()
    {
        Assert.Equal("site:demo.test", ContinuityRegistry.DeriveTargetKey(Snap()));
        var gh = new SourceSnapshot
        {
            SourceType = SourceType.GitHub, SourceName = "daluvalanokia/smartagent", SourceDetail = "branch main",
            Files = []
        };
        Assert.Equal("github:daluvalanokia/smartagent", ContinuityRegistry.DeriveTargetKey(gh));
    }

    [Fact]
    public void Findings_progress_new_then_persisting_then_resolved()
    {
        var reg = new ContinuityRegistry(); // in-memory (no store path)

        // run 1: finding appears
        var r1 = reg.MatchAndBeginRun("site:demo.test", "demo.test", [F()]);
        Assert.Equal(1, r1.RunNumber);
        Assert.Single(r1.NewFindings);

        // run 2: same finding again → persisting, occurrences escalate
        var f2 = F();
        var r2 = reg.MatchAndBeginRun("site:demo.test", "demo.test", [f2]);
        Assert.Equal(2, r2.RunNumber);
        Assert.Empty(r2.NewFindings);
        Assert.Single(r2.PersistingFindings);
        Assert.Equal(ContinuityStatus.Persisting, f2.Continuity);
        Assert.Equal(2, f2.Occurrences);

        // run 3: finding is gone → resolved since last run
        var r3 = reg.MatchAndBeginRun("site:demo.test", "demo.test", []);
        Assert.Single(r3.ResolvedSinceLastRun);
        Assert.Equal(3, r3.RunNumber);
    }

    [Fact]
    public void Fixed_feedback_then_reappearance_is_a_regression()
    {
        var reg = new ContinuityRegistry();

        var f1 = F();
        var r1 = reg.MatchAndBeginRun("site:demo.test", "demo.test", [f1]);
        reg.CommitRun("site:demo.test", Guid.NewGuid(), "t");

        // CI reports the fix
        Assert.Equal(1, reg.ApplyFeedback("site:demo.test",
            [(f1.Fingerprint, FeedbackStatus.Fixed, "PR 42")]));

        // next run: the finding is back → regression
        var f2 = F();
        var r2 = reg.MatchAndBeginRun("site:demo.test", "demo.test", [f2]);
        Assert.Single(r2.RegressionFindings);
        Assert.Equal(ContinuityStatus.Regression, f2.Continuity);
    }

    [Fact]
    public void Wont_fix_feedback_suppresses_findings_from_scope()
    {
        var reg = new ContinuityRegistry();
        var f1 = F();
        reg.MatchAndBeginRun("site:demo.test", "demo.test", [f1]);
        reg.ApplyFeedback("site:demo.test", [(f1.Fingerprint, FeedbackStatus.WontFix, "by design")]);

        var f2 = F();
        var r2 = reg.MatchAndBeginRun("site:demo.test", "demo.test", [f2]);
        Assert.True(f2.SuppressFromScope);
        Assert.Single(r2.SuppressedFindings);

        var prioritizer = new Prioritizer();
        var scoped = prioritizer.Scope([f2], RunOptions.Default);
        Assert.Empty(scoped);   // suppressed never enters a prompt
    }

    [Fact]
    public void Escalation_boosts_recurring_and_regressions()
    {
        var fresh = F("F1", "a.html", 10);                 // occurrences 1, new
        var persisting = F("F2", "b.html", 20); persisting.Occurrences = 3; persisting.Continuity = ContinuityStatus.Persisting;
        var regression = F("F3", "c.html", 30); regression.Occurrences = 2; regression.Continuity = ContinuityStatus.Regression;

        var scoped = new Prioritizer().Scope([fresh, persisting, regression], RunOptions.Default);

        Assert.Equal(regression, scoped[0]);                    // regression tops the list
        Assert.Equal(persisting, scoped[1]);                    // escalated above the fresh equal finding
        Assert.True(scoped[0].Score > scoped[2].Score);
        Assert.True(scoped[1].Score > scoped[2].Score);
    }

    [Fact]
    public void History_persists_to_disk_across_registry_instances()
    {
        var path = Path.Combine(Path.GetTempFileName());
        try
        {
            var reg1 = new ContinuityRegistry(path);
            var f = F();
            reg1.MatchAndBeginRun("site:demo.test", "demo.test", [f]);
            reg1.ApplyFeedback("site:demo.test", [(f.Fingerprint, FeedbackStatus.Fixed, "done")]);

            var reg2 = new ContinuityRegistry(path);   // fresh instance, same store
            var hist = reg2.GetHistory("site:demo.test");
            Assert.NotNull(hist);
            Assert.Equal(1, hist!.RunCount);
            Assert.Single(hist.Findings);
            Assert.Equal(FeedbackStatus.Fixed, hist.Findings[0].FeedbackStatus);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Run_numbers_chain_per_target_independently()
    {
        var reg = new ContinuityRegistry();
        reg.MatchAndBeginRun("site:a.test", "a", [F()]);
        reg.MatchAndBeginRun("site:b.test", "b", [F(file: "other.html")]);
        var r = reg.MatchAndBeginRun("site:a.test", "a", [F()]);

        Assert.Equal(2, r.RunNumber);   // second run for a, independent of b
        Assert.Equal(2, reg.GetHistory("site:a.test")!.RunCount);
        Assert.Equal(1, reg.GetHistory("site:b.test")!.RunCount);
    }
}
