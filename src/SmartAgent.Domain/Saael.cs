namespace SmartAgent.Domain;

/// <summary>
/// SAAEL — SmartAgent Autonomous Agile Engineering Lifecycle.
/// Domain model: lifecycle states, automation levels, human gates,
/// governance records and traceability for the AI-orchestrated pipeline.
/// </summary>

/// <summary>Story lifecycle per the SAAEL operating model (§8).</summary>
public enum LifecycleState
{
    New, Analyzing, Ready, InDevelopment, CodeReview, Build, UnitTest,
    IntegrationTest, Qa, BusinessTest, Staging, Uat, ReleaseApproved,
    Production, Monitoring, Completed
}

/// <summary>Automation decision levels (§22). AI performs work; humans own decisions.</summary>
public enum AutomationLevel
{
    /// <summary>L1 — AI recommends, human decides every step.</summary>
    L1_Recommend = 1,
    /// <summary>L2 — AI prepares and executes after approval (default).</summary>
    L2_ApprovedExecution = 2,
    /// <summary>L3 — AI automatically executes low-risk (technical) actions.</summary>
    L3_AutoLowRisk = 3,
    /// <summary>L4 — autonomous execution within predefined policy; human oversight.</summary>
    L4_PolicyAutonomous = 4
}

/// <summary>Human roles that own decisions at the gates.</summary>
public enum HumanRole
{
    ProductOwner, ProjectManager, BusinessAnalyst, SolutionArchitect,
    DevelopmentManager, Developer, QaManager, QaEngineer, SecurityEngineer,
    DevOpsEngineer, ImplementationCoordinator, BusinessUser, SupportOperations
}

/// <summary>One work item (user story) travelling the lifecycle.</summary>
public sealed class WorkItem
{
    public required string Id { get; init; }                 // SA-101 …
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];
    public IReadOnlyList<string> NonFunctionalRequirements { get; init; } = [];
    public LifecycleState State { get; set; } = LifecycleState.New;
    public AutomationLevel Level { get; init; } = AutomationLevel.L2_ApprovedExecution;
    /// <summary>High when the story touches security/data/external APIs — gates stay human.</summary>
    public string Risk { get; init; } = "Medium";
    public int StoryPoints { get; init; } = 3;
    public int Priority { get; init; } = 2;                  // 1 = highest
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool RequiresHighImpactReview => Risk == "High" || Level >= AutomationLevel.L4_PolicyAutonomous;
}

/// <summary>An ambiguity the BA Agent raised; the human BA must resolve it before Ready.</summary>
public sealed class Ambiguity
{
    public required string Id { get; init; }
    public required string StoryId { get; init; }
    public required string Question { get; init; }
    public string? Resolution { get; set; }
    public string? ResolvedBy { get; set; }
    public bool Resolved => Resolution is not null;
}

/// <summary>A human-approval request at a gate. All required roles must decide before the transition fires.</summary>
public sealed class ApprovalRequest
{
    public required string Id { get; init; }
    public required string StoryId { get; init; }
    public required LifecycleState FromState { get; init; }
    public required LifecycleState ToState { get; init; }
    public required IReadOnlyList<HumanRole> RequiredRoles { get; init; }
    public List<RoleDecision> Decisions { get; } = [];
    public DateTimeOffset OpenedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Satisfied => RequiredRoles.All(r => Decisions.Any(d => d.Role == r && d.Approved));
    public bool Rejected => Decisions.Any(d => !d.Approved);
}

public sealed class RoleDecision
{
    public required HumanRole Role { get; init; }
    public required string Actor { get; init; }
    public bool Approved { get; init; }
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset DecidedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>AI engineering audit trail (§23): every AI action is recorded and reviewable.</summary>
public sealed class GovernanceRecord
{
    public required string RecordId { get; init; }
    public required string AgentId { get; init; }
    public required string Action { get; init; }
    public required string InputSummary { get; init; }
    public required string OutputSummary { get; init; }
    public string ModelVersion { get; init; } = "deterministic-heuristic-1.0";
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? HumanReviewer { get; init; }
    public string? Decision { get; init; }
    public string Environment { get; init; } = "development";
    public string Result { get; init; } = "executed";
    public int TokensUsed { get; init; }
}

/// <summary>Traceability chain (§6): requirement → story → code → test → release → telemetry.</summary>
public enum TraceLinkType { Requirement, Story, Code, Api, Database, Test, Release, Telemetry, Approval }

public sealed class TraceLink
{
    public required string StoryId { get; init; }
    public required TraceLinkType Type { get; init; }
    public required string TargetRef { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ReleaseReadinessCheck
{
    public required string Area { get; init; }
    /// <summary>Pass | Fail | Outstanding.</summary>
    public required string Status { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class ReleaseReadinessReport
{
    public required string StoryId { get; init; }
    public IReadOnlyList<ReleaseReadinessCheck> Checks { get; init; } = [];
    public bool ReadyForProduction { get; init; }
    public IReadOnlyList<string> OutstandingApprovals { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

/// <summary>Sprint proposal produced by the PM Agent — a recommendation, never a silent priority change (§7).</summary>
public sealed class SprintProposal
{
    public required string ProposalId { get; init; }
    public IReadOnlyList<string> StoryIds { get; init; } = [];
    public int TotalPoints { get; init; }
    public string Rationale { get; init; } = string.Empty;
    public string Status { get; set; } = "Proposed";          // Proposed | Approved | Rejected
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Data-driven retrospective insight from the Learning Agent (§19).</summary>
public sealed class SprintInsight
{
    public required string Observation { get; init; }
    public required string SuggestedAction { get; init; }
    public required string Evidence { get; init; }
}

/// <summary>Token-resource accounting per agent call (the "token budget" resource).</summary>
public sealed class TokenUsage
{
    public required string AgentId { get; init; }
    public int Tokens { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A change proposal from the Developer Agent — a proposal, not a direct code modification (§9).</summary>
public sealed class ChangeProposal
{
    public required string StoryId { get; init; }
    public IReadOnlyList<string> FilesAffected { get; init; } = [];
    public IReadOnlyList<string> PotentialImpact { get; init; } = [];
    public string Risk { get; init; } = "Medium";
    public int SuggestedUnitTests { get; init; }
    public int SuggestedIntegrationTests { get; init; }
    public int SuggestedNegativeScenarios { get; init; }
    public string Status { get; set; } = "Proposed";           // Proposed | Accepted | Modified | Rejected
}

/// <summary>Test scenarios generated by the QA Agent (§12).</summary>
public sealed class TestScenario
{
    public required string StoryId { get; init; }
    public required string Category { get; init; }             // Normal | Boundary | Negative | Security | Performance | Regression
    public required string Title { get; init; }
}

/// <summary>Root-cause analysis of a failed CI build (§10).</summary>
public sealed class CiAnalysis
{
    public required string PrimaryCause { get; init; }
    public string LikelyCommit { get; init; } = "unknown";
    public IReadOnlyList<string> AffectedTests { get; init; } = [];
    public required string SuggestedCorrection { get; init; }
    public double Confidence { get; init; }
    public bool DeveloperReviewRequired { get; init; } = true;
}
