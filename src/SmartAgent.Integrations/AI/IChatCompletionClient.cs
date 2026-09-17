namespace SmartAgent.Integrations.AI;

/// <summary>Abstraction over an OpenAI-compatible chat-completion API.</summary>
public interface IChatCompletionClient
{
    /// <summary>True when configured (BaseUrl + ApiKey present).</summary>
    bool IsConfigured { get; }

    /// <summary>Sends one chat completion request. Returns the assistant message.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
