using System.Text.RegularExpressions;
using SmartAgent.Domain;

namespace SmartAgent.Core;

/// <summary>
/// Static, fast heuristics that turn a source snapshot into concrete
/// findings. Language-agnostic by design: code smells from C#, JS/TS, HTML,
/// CSS and config files are all covered.
/// </summary>
public sealed partial class HeuristicAnalyzer
{
    public IReadOnlyList<Finding> Analyze(SourceSnapshot snapshot)
    {
        var findings = new List<Finding>();
        var n = 0;

        foreach (var file in snapshot.Files)
        {
            if (file.Content.Length == 0) continue;
            var isHtml = file.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                         file.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
            var lines = file.Content.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var num = i + 1;

                // 1. TODO / FIXME / HACK markers
                if (TryMatchTodo(line, out var marker))
                {
                    findings.Add(Make(ref n, "todo-marker", "Unresolved marker in code",
                        $"A '{marker}' marker indicates unfinished work.", FindingCategory.FunctionalityImprovement,
                        Severity.Medium, file.Path, num, line, "Resolve or convert into a tracked task with an owner."));
                    continue;
                }

                // 2. Empty catch block (C# / JS / TS / Java style)
                if (EmptyCatchRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "empty-catch", "Swallowed exception",
                        "An exception is caught and ignored, hiding failures and causing erratic behavior.",
                        FindingCategory.ErraticBehavior, Severity.High, file.Path, num, line,
                        "Log the exception and either handle it or rethrow with context."));
                    continue;
                }

                // 3. catch (...) { } on next line
                if (line.Trim().StartsWith("catch", StringComparison.Ordinal) && i + 1 < lines.Length &&
                    lines[i + 1].Trim() is "{ }" or "{}" or "{;}" or "{ ; }")
                {
                    findings.Add(Make(ref n, "empty-catch", "Swallowed exception",
                        "An exception is caught and ignored, hiding failures and causing erratic behavior.",
                        FindingCategory.ErraticBehavior, Severity.High, file.Path, num + 1, lines[i + 1],
                        "Log the exception and either handle it or rethrow with context."));
                    continue;
                }

                // 4. Fixed sleep/pause (stability / perf smell)
                if (SleepRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "fixed-sleep", "Hard-coded wait",
                        "Fixed delays make behavior erratic and waste time; they usually mask a synchronization bug.",
                        FindingCategory.Performance, Severity.Medium, file.Path, num, line,
                        "Replace with an event/callback or an explicit completion condition."));
                    continue;
                }

                // 5. console.log / Debug.WriteLine left behind
                if (DebugOutputRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "debug-output", "Leftover debug output",
                        "Debug output left in production paths can leak data and add noise.",
                        FindingCategory.Maintainability, Severity.Low, file.Path, num, line,
                        "Remove it or route through a proper logger with levels."));
                    continue;
                }

                // 6. Vulnerable DOM sinks (XSS)
                if (DomXssRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "dom-xss", "Potential XSS sink",
                        "Untrusted data is written directly into the DOM without sanitization.",
                        FindingCategory.Security, Severity.Critical, file.Path, num, line,
                        "Escape the value or use a sanitizer before inserting into the DOM."));
                    continue;
                }

                // 7. Hard-coded secrets
                if (SecretRegex().IsMatch(line) && !file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(Make(ref n, "hardcoded-secret", "Hard-coded credential",
                        "A credential/token pattern appears in source; it will leak through VCS.",
                        FindingCategory.Security, Severity.Critical, file.Path, num, line,
                        "Move the value to environment/secret storage and rotate the exposed credential."));
                    continue;
                }

                // 8. Empty href (dead link)
                if (isHtml && EmptyHrefRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "empty-href", "Dead link",
                        "An anchor with an empty or '#' href leads nowhere, breaking navigation.",
                        FindingCategory.ErraticBehavior, Severity.Medium, file.Path, num, line,
                        "Point it to a real route or disable the control until it exists."));
                    continue;
                }

                // 9. onclick handlers inline (spaghetti behavior)
                if (isHtml && InlineHandlerRegex().IsMatch(line))
                {
                    findings.Add(Make(ref n, "inline-handler", "Inline event handler",
                        "Inline JS handlers scatter behavior through markup and are hard to test.",
                        FindingCategory.Maintainability, Severity.Low, file.Path, num, line,
                        "Bind the event from a script with addEventListener."));
                    continue;
                }
            }

            // 10. Very long file heuristic
            if (lines.Length > 800 && file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Make(ref n, "long-file", "Oversized file",
                    $"{lines.Length} lines in one file; the class mixes responsibilities.",
                    FindingCategory.Maintainability, Severity.Low, file.Path, 1,
                    file.Path, "Split by responsibility."));
            }

            // 11. Website page with no <title>
            if (isHtml && !file.Content.Contains("<title", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Make(ref n, "missing-title", "Page without title",
                    "The page has no <title>, hurting UX, SEO and accessibility.",
                    FindingCategory.FunctionalityImprovement, Severity.Medium, file.Path, 1,
                    "<head>…</head>", "Add a concise descriptive <title>."));
            }
        }

        return findings;
    }

    private static Finding Make(ref int n, string rule, string title, string description,
        FindingCategory category, Severity severity, string path, int line, string evidence, string action) =>
        new()
        {
            Id = $"F{++n:D3}",
            Rule = rule,
            Title = title,
            Description = description,
            Category = category,
            Severity = severity,
            FilePath = path,
            Line = line,
            Evidence = Truncate(evidence.Trim(), 240),
            SuggestedAction = action
        };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static bool TryMatchTodo(string line, out string marker)
    {
        marker = "";
        foreach (var m in new[] { "TODO", "FIXME", "HACK", "XXX:" })
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase))
            {
                marker = m;
                return true;
            }
        return false;
    }

    [GeneratedRegex("""catch\s*(\([^)]*\))?\s*\{\s*\}""")]
    private static partial Regex EmptyCatchRegex();

    [GeneratedRegex("""Thread\.Sleep|Task\.Delay\( *\d|setTimeout\( *\d""")]
    private static partial Regex SleepRegex();

    [GeneratedRegex("""console\.(log|debug|info)|System\.Diagnostics\.Debug\.WriteLine""")]
    private static partial Regex DebugOutputRegex();

    [GeneratedRegex("""(innerHTML|document\.write)\s*=""")]
    private static partial Regex DomXssRegex();

    [GeneratedRegex("""(?i)(api[_-]?key|secret|password|token)\s*[:=]\s*["'][A-Za-z0-9_\-]{16,}["']""")]
    private static partial Regex SecretRegex();

    [GeneratedRegex("""<a\b[^>]*href\s*=\s*["'](#|)["']""")]
    private static partial Regex EmptyHrefRegex();

    [GeneratedRegex("""\son(click|change|submit|load|error)\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();
}
