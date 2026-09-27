namespace AgentStudio.Shared;

/// <summary>Emits the first deferred claim observation for a lease only.</summary>
public sealed class RemoteRequeueLogLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldLog(string taskKey, string? leaseId)
    {
        lock (_gate)
        {
            if (!_seen.TryGetValue(taskKey, out var leases))
                _seen[taskKey] = leases = new HashSet<string>(StringComparer.Ordinal);
            return leases.Add(leaseId ?? "unknown");
        }
    }

    /// <summary>Only Progress cards can still produce a deferral for a remembered lease.</summary>
    public void RetainProgressTasks(IReadOnlySet<string> progressTaskKeys)
    {
        lock (_gate)
        {
            foreach (var taskKey in _seen.Keys.ToArray())
                if (!progressTaskKeys.Contains(taskKey)) _seen.Remove(taskKey);
        }
    }

    /// <summary>A settled card cannot produce another deferral for its old lease.</summary>
    public void ForgetTask(string taskKey)
    {
        lock (_gate) _seen.Remove(taskKey);
    }
}
