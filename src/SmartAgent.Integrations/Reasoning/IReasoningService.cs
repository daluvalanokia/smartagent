using SmartAgent.Domain;

namespace SmartAgent.Integrations.Reasoning;

/// <summary>
/// Integrates with external "smart logic / reasoning" websites: sends the
/// current findings, receives enrichment (extra context, severity
/// adjustments). Implementations must degrade gracefully when endpoints are
/// unreachable, so the pipeline never hard-depends on one site.
/// </summary>
public interface IReasoningService
{
    /// <summary>Enriches findings in place; returns warnings for unreachable sites.</summary>
    Task<IReadOnlyList<string>> EnrichAsync(IReadOnlyList<Finding> findings, SourceSnapshot snapshot, CancellationToken ct);
}
