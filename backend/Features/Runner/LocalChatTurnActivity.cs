namespace AgentStudio.Runner;

/// <summary>In-process chat occupancy; token and cost history remains on durable turns.</summary>
public sealed class LocalChatTurnActivity
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Project, DateTime StartedAt)> _active = [];

    public IDisposable Start(string project)
    {
        var id = Guid.NewGuid();
        lock (_gate) _active[id] = (project, DateTime.UtcNow);
        return new Lease(this, id);
    }

    public IReadOnlyList<RemoteChatUsage> Snapshot(DateTime now)
    {
        lock (_gate)
            return _active.Values.GroupBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .Select(group => new RemoteChatUsage(Environment.MachineName, group.Key,
                    group.Count(), group.Count(item => IsHeavy(item.StartedAt, now)),
                    null, 0, null)).ToArray();
    }

    public static bool IsHeavy(DateTime startedAt, DateTime now)
        => now - startedAt >= TimeSpan.FromSeconds(30);

    private sealed class Lease(LocalChatTurnActivity owner, Guid id) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._active.Remove(id);
        }
    }
}
