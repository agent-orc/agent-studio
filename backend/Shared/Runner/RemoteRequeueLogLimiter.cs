using System.Collections.Concurrent;

namespace AgentStudio.Shared;

/// <summary>Emits the first deferred claim observation for a lease only.</summary>
public sealed class RemoteRequeueLogLimiter
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public bool ShouldLog(string taskKey, string? leaseId)
    {
        if (_seen.Count > 10000) _seen.Clear();
        return _seen.TryAdd($"{taskKey}:{leaseId ?? "unknown"}", 0);
    }
}
