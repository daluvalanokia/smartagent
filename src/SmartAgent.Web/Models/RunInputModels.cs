using SmartAgent.Domain;

namespace SmartAgent.Web.Models;

public class RunInput
{
    /// <summary>a / b / c select.</summary>
    public SourceType SourceType { get; set; } = SourceType.ZipArchive;

    public IFormFile? ZipFile { get; set; }

    public string? GitHubUrl { get; set; }
    public string? WebsiteUrl { get; set; }

    /// <summary>1..8, keeps the run's prompt scope tight.</summary>
    public int ScopeLimit { get; set; } = 3;
    public FocusArea Focus { get; set; } = FocusArea.Balanced;
    public string? Notes { get; set; }
}

public class PromptEvaluationInput
{
    /// <summary>Any prompt text: numbered requirements, bullets, sections or free prose.</summary>
    public string? Prompt { get; set; }

    /// <summary>Max concurrent evaluation threads (blank = CPU count).</summary>
    public int? MaxThreads { get; set; }
}

public class SaaelInput
{
    public string? Idea { get; set; }
}

public sealed record SaaelStep(string Stage, string Who, string What, string Result);

public sealed class SaaelDemoViewModel
{
    public required string Idea { get; init; }
    public required SmartAgent.Core.SaaelOrchestrator.SaaelPlanView Plan { get; init; }
    public required IReadOnlyList<string> Walk { get; init; }
    public required SmartAgent.Domain.ReleaseReadinessReport Readiness { get; init; }
    public required IReadOnlyList<SmartAgent.Domain.SprintInsight> Insights { get; init; }
    public required SmartAgent.Core.SaaelOrchestrator.TokenLedgerView Tokens { get; init; }
    public required IReadOnlyList<SmartAgent.Domain.GovernanceRecord> Governance { get; init; }
}

public class AnalyzeAppInput
{
    public string? Url { get; set; }
    public string? Prompt { get; set; }
}

public sealed class AnalyzeAppResultViewModel
{
    public required SmartAgent.Domain.AppAnalysisReport Report { get; init; }
    public required IReadOnlyList<SmartAgent.Domain.WorkItem> Stories { get; init; }
}
