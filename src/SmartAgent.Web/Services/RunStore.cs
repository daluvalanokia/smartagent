using System.Collections.Concurrent;
using SmartAgent.Domain;

namespace SmartAgent.Web.Services;

/// <summary>Bounded in-memory store of recent run results.</summary>
public sealed class RunStore
{
    private const int Capacity = 50;
    private readonly ConcurrentDictionary<Guid, PromptRunResult> _runs = new();

    public void Save(PromptRunResult result)
    {
        if (_runs.Count >= Capacity)
        {
            var oldest = _runs.OrderBy(kv => kv.Value.StartedUtc).FirstOrDefault();
            if (oldest.Key != default) _runs.TryRemove(oldest.Key, out _);
        }
        _runs[result.RunId] = result;
    }

    public PromptRunResult? Get(Guid id) => _runs.TryGetValue(id, out var r) ? r : null;
}
