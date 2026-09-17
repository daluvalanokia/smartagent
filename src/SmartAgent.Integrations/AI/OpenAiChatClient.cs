using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;


namespace SmartAgent.Integrations.AI;

/// <summary>Default implementation: works with OpenAI, Azure OpenAI, OpenRouter, Ollama, etc.</summary>
public sealed class OpenAiChatClient(HttpClient http, AiProviderOptions options) : IChatCompletionClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AiProviderOptions _opt = options;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.BaseUrl) && !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("AI provider is not configured.");

        var payload = new
        {
            model = _opt.Model,
            temperature = _opt.Temperature,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TrimSlash(_opt.BaseUrl) + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI provider returned {(int)resp.StatusCode}: {Truncate(body, 500)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return content ?? string.Empty;
    }

    private static string TrimSlash(string s) => s.TrimEnd('/');
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
