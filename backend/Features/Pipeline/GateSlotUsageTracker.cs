using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

public sealed record GateSlotUsage(int Busy, int Total);

/// <summary>Live slot use observed by this dispatching API process.</summary>
public sealed class GateSlotUsageTracker
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly TimeProvider _clock;
    private int _total;
    private long _lastReachedTicks;
    private long _lastFailureTicks;

    public GateSlotUsageTracker(IConfiguration? configuration = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var configured = configuration?.GetValue("RemoteGate:GateSlots", 1) ?? 1;
        _total = configured is >= 1 and <= 64 ? configured : 1;
    }

    public GateSlotUsage Snapshot() => new(_active.Count, Volatile.Read(ref _total));

    public DateTimeOffset? LastReachedAt => ReadTime(Interlocked.Read(ref _lastReachedTicks));
    public DateTimeOffset? LastFailureAt => ReadTime(Interlocked.Read(ref _lastFailureTicks));
    public string Reachability => LastFailureAt is { } failed && (LastReachedAt is null || failed > LastReachedAt)
        ? "unreachable"
        : LastReachedAt is { } reached && _clock.GetUtcNow() - reached < TimeSpan.FromMinutes(5)
            ? "reachable" : "unverified";

    public void Reached() => Interlocked.Exchange(ref _lastReachedTicks, _clock.GetUtcNow().UtcTicks);
    public void Failed() => Interlocked.Exchange(ref _lastFailureTicks, _clock.GetUtcNow().UtcTicks);

    private static DateTimeOffset? ReadTime(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    public void Configure(int total) => Volatile.Write(ref _total, total);

    public void Acquired(string runId, int total)
    {
        Configure(total);
        _active[runId] = 0;
    }

    public void Released(string runId) => _active.TryRemove(runId, out _);
}
