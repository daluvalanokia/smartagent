using SmartAgent.Core;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class PrioritizerTests
{
    private static Finding F(int i, FindingCategory cat, Severity sev, string path = "a.cs", int line = 0) => new()
    {
        Id = $"F{i:D3}", Rule = "r", Title = $"t{i}", Description = "d",
        Category = cat, Severity = sev, FilePath = path, Line = line == 0 ? i + 1 : line
    };

    [Fact]
    public void Scope_limits_output_to_scope_limit()
    {
        var findings = Enumerable.Range(0, 20).Select(i => F(i, FindingCategory.Maintainability, Severity.Low)).ToList();
        var scoped = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 3 });
        Assert.Equal(3, scoped.Count);
    }

    [Fact]
    public void Scope_orders_by_score_descending()
    {
        var findings = new List<Finding>
        {
            F(1, FindingCategory.Maintainability, Severity.Low),
            F(2, FindingCategory.ErraticBehavior, Severity.Critical),
            F(3, FindingCategory.FunctionalityImprovement, Severity.High)
        };
        var scoped = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 5 });
        Assert.Equal("F002", scoped[0].Id);
        Assert.Equal("F003", scoped[1].Id);
        Assert.Equal("F001", scoped[2].Id);
        Assert.True(scoped[0].Score > scoped[1].Score);
    }

    [Fact]
    public void Stability_focus_boosts_erratic_behavior_over_maintainability()
    {
        var findings = new List<Finding>
        {
            F(1, FindingCategory.Maintainability, Severity.High),
            F(2, FindingCategory.ErraticBehavior, Severity.Medium)
        };
        var scoped = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 2, Focus = FocusArea.Stability });
        Assert.Equal("F002", scoped[0].Id);
    }

    [Fact]
    public void Functionality_focus_boosts_functionality_over_erratic()
    {
        var findings = new List<Finding>
        {
            F(1, FindingCategory.ErraticBehavior, Severity.High),
            F(2, FindingCategory.FunctionalityImprovement, Severity.Medium)
        };
        // Balanced: High erratic (7*1.3=9.1) vs Medium functionality (4*1.15=4.6)
        var balanced = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 2 });
        Assert.Equal("F001", balanced[0].Id);

        // Functionality focus: 4*1.15*1.5=6.9 vs 9.1 — still erratic first.
        // Raise functionality severity to make it win under focus.
        findings[1] = F(2, FindingCategory.FunctionalityImprovement, Severity.High);
        var focused = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 2, Focus = FocusArea.Functionality });
        Assert.Equal("F002", focused[0].Id); // 7*1.15*1.5 = 12.07 vs 9.1
    }

    [Fact]
    public void Scope_de_duplicates_same_rule_file_line()
    {
        var findings = new List<Finding>
        {
            F(1, FindingCategory.ErraticBehavior, Severity.High, line: 5),
            F(2, FindingCategory.ErraticBehavior, Severity.High, line: 5)  // same (rule, path, line)
        };
        var scoped = new Prioritizer().Scope(findings, new RunOptions { ScopeLimit = 5 });
        Assert.Single(scoped);
    }

    [Fact]
    public void Reasoning_note_nudges_score_up()
    {
        var a = F(1, FindingCategory.ErraticBehavior, Severity.High);
        var b = F(2, FindingCategory.ErraticBehavior, Severity.High, path: "b.cs");
        b.ReasoningNote = "[site] known regression";
        var scoped = new Prioritizer().Scope([a, b], new RunOptions { ScopeLimit = 2 });
        Assert.Equal("F002", scoped[0].Id);
    }

    [Fact]
    public void Scope_throws_on_null_inputs()
    {
        var p = new Prioritizer();
        Assert.Throws<ArgumentNullException>(() => p.Scope(null!, RunOptions.Default));
    }
}
