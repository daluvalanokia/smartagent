namespace SmartAgent.Integrations;

/// <summary>Configuration for the AI processing provider (OpenAI-compatible).</summary>
public sealed class AiProviderOptions
{
    public const string SectionName = "SmartAgent:Ai";

    /// <summary>Any OpenAI-compatible base URL. Empty = AI consolidation disabled.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.2;
}

/// <summary>
/// One "smart logic / reasoning" website endpoint. SmartAgent POSTs the
/// current findings and receives back enrichment notes / score adjustments.
/// </summary>
public sealed class ReasoningSiteOptions
{
    public string Name { get; set; } = "reasoning-site";
    /// <summary>Full URL that accepts POST with the findings payload.</summary>
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class ReasoningOptions
{
    public const string SectionName = "SmartAgent:ReasoningSites";
    public List<ReasoningSiteOptions> Sites { get; set; } = [];
}
