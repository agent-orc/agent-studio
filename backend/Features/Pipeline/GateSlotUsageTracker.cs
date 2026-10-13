using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

public sealed record GateSlotUsage(int Busy, int Total);

/// <summary>Live slot use observed by this dispatching API process.</summary>
public sealed class GateSlotUsageTracker
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private int _total;

    public GateSlotUsageTracker(IConfiguration? configuration = null)
    {
        var configured = configuration?.GetValue("RemoteGate:GateSlots", 1) ?? 1;
        _total = configured is >= 1 and <= 64 ? configured : 1;
    }

    public GateSlotUsage Snapshot() => new(_active.Count, Volatile.Read(ref _total));

    public void Configure(int total) => Volatile.Write(ref _total, total);

    public void Acquired(string runId, int total)
    {
        Configure(total);
        _active[runId] = 0;
    }

    public void Released(string runId) => _active.TryRemove(runId, out _);
}
