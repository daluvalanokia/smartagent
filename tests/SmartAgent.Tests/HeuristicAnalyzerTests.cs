using SmartAgent.Core;
using SmartAgent.Domain;
using Xunit;

namespace SmartAgent.Tests;

public class HeuristicAnalyzerTests
{
    private static SourceSnapshot Snap(params (string path, string content)[] files) => new()
    {
        SourceType = SourceType.ZipArchive,
        SourceName = "test",
        SourceDetail = "test",
        Files = files.Select(f => new SourceFile { Path = f.path, Content = f.content }).ToList()
    };

    [Fact]
    public void Detects_empty_catch_and_todo()
    {
        var snap = Snap(("a.cs", "try { DoIt(); }\ncatch (Exception ex) { }\n// TODO: implement"));
        var findings = new HeuristicAnalyzer().Analyze(snap);
        Assert.Contains(findings, f => f.Rule == "empty-catch");
        Assert.Contains(findings, f => f.Rule == "todo-marker");
    }

    [Fact]
    public void Detects_hardcoded_secret_and_xss()
    {
        var snap = Snap(("site.js", """
            var apiKey = "sk_9f8e7d6c5b4a3210fedcba";
            el.innerHTML = userInput;
            """));
        var findings = new HeuristicAnalyzer().Analyze(snap);
        Assert.Contains(findings, f => f.Rule == "hardcoded-secret");
        Assert.Contains(findings, f => f.Rule == "dom-xss");
    }

    [Fact]
    public void Detects_website_issues()
    {
        var html = """
            <html><body>
            <a href="#">Learn more</a>
            <div onclick="doThing()">click</div>
            </body></html>
            """;
        var findings = new HeuristicAnalyzer().Analyze(Snap(("index.html", html)));
        Assert.Contains(findings, f => f.Rule == "empty-href");
        Assert.Contains(findings, f => f.Rule == "inline-handler");
        Assert.Contains(findings, f => f.Rule == "missing-title");
    }

    [Fact]
    public void Clean_source_yields_no_findings()
    {
        var snap = Snap(("clean.cs", "public int Add(int a, int b) => a + b;\n"));
        Assert.Empty(new HeuristicAnalyzer().Analyze(snap));
    }
}
