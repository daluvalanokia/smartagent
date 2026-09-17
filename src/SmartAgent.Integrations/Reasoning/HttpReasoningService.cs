using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using SmartAgent.Domain;

namespace SmartAgent.Integrations.Reasoning;

/// <summary>
/// Calls every configured reasoning-site endpoint in parallel and merges its
/// response into the findings it references. Expected response contract (JSON):
/// { "notes": [ { "findingId": "<id>", "note": "…", "severityAdjust": 1 } ] }
/// </summary>
public sealed class HttpReasoningService(HttpClient http, ReasoningOptions options) : IReasoningService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ReasoningOptions _opt = options;

    public async Task<IReadOnlyList<string>> EnrichAsync(IReadOnlyList<Finding> findings, SourceSnapshot snapshot, CancellationToken ct)
    {
        var warnings = new List<string>();
        var activeSites = _opt.Sites.Where(s => !string.IsNullOrWhiteSpace(s.Endpoint)).ToList();
        if (activeSites.Count == 0) return warnings;

        var payload = JsonSerializer.Serialize(new ReasoningPayload(snapshot, findings), Json);

        var tasks = activeSites.Select(async site =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, site.TimeoutSeconds)));

                using var req = new HttpRequestMessage(HttpMethod.Post, site.Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(site.ApiKey))
                    req.Headers.Add("X-Api-Key", site.ApiKey);

                using var resp = await http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    warnings.Add($"Reasoning site '{site.Name}' returned {(int)resp.StatusCode}.");
                    return;
                }

                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                Merge(site.Name, body, findings);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                warnings.Add($"Reasoning site '{site.Name}' unavailable: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        return warnings;
    }

    private static void Merge(string site, string body, IReadOnlyList<Finding> findings)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Array) return;

        var byId = findings.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes.EnumerateArray())
        {
            if (!n.TryGetProperty("findingId", out var fid)) continue;
            if (!byId.TryGetValue(fid.GetString() ?? string.Empty, out var finding)) continue;

            var note = n.TryGetProperty("note", out var nv) ? nv.GetString() : null;
            if (!string.IsNullOrWhiteSpace(note))
                finding.ReasoningNote = string.IsNullOrWhiteSpace(finding.ReasoningNote)
                    ? $"[{site}] {note}"
                    : $"{finding.ReasoningNote} [{site}] {note}";

            // severityAdjust: signed int, e.g. +1 / -1, clamped to the enum range
            if (n.TryGetProperty("severityAdjust", out var adj) && adj.ValueKind == JsonValueKind.Number)
            {
                var shifted = (int)finding.Severity + adj.GetInt32();
                finding.Severity = (Severity)Math.Clamp(shifted, (int)Severity.Low, (int)Severity.Critical);
            }
        }
    }

    private sealed record ReasoningPayload(SourceSnapshot Snapshot, IReadOnlyList<Finding> Findings);
}
