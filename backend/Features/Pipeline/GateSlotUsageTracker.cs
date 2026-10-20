using System.Collections.Concurrent;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed record GateSlotUsage(int Busy, int Total);

/// <summary>Live slot use observed by this dispatching API process.</summary>
public sealed class GateSlotUsageTracker
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly object _dispatchGate = new();
    private readonly HashSet<string> _dispatching = new(StringComparer.Ordinal);
    private bool _restartFenced;
    private readonly TimeProvider _clock;
    private readonly object _reachabilityLock = new();
    private int _total;
    private DateTimeOffset? _lastReachedAt;
    private DateTimeOffset? _lastFailureAt;
    private bool? _lastSucceeded;
    private (GateSlotUsage Usage, DateTimeOffset At)? _remote;

    public GateSlotUsageTracker(IConfiguration? configuration = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var configured = configuration?.GetValue("RemoteGate:GateSlots", 1) ?? 1;
        _total = configured is >= 1 and <= 64 ? configured : 1;
    }

    public GateSlotUsage Snapshot() => new(_active.Count, Volatile.Read(ref _total));

    public int Dispatching { get { lock (_dispatchGate) return _dispatching.Count; } }

    public IDisposable? TryBeginDispatch(string runId)
    {
        lock (_dispatchGate)
        {
            if (_restartFenced) return null;
            _dispatching.Add(runId);
            return new DispatchLease(this, runId);
        }
    }

    public IRestartClaimFence? TryFenceDispatch()
    {
        lock (_dispatchGate)
        {
            if (_restartFenced || _dispatching.Count > 0 || !_active.IsEmpty) return null;
            _restartFenced = true;
            return new DispatchFence(this);
        }
    }

    public void ReleaseRestartFence()
    {
        lock (_dispatchGate) _restartFenced = false;
    }

    private sealed class DispatchLease(GateSlotUsageTracker owner, string runId) : IDisposable
    {
        public void Dispose() { lock (owner._dispatchGate) owner._dispatching.Remove(runId); }
    }

    private sealed class DispatchFence(GateSlotUsageTracker owner) : IRestartClaimFence
    {
        private bool _retained;
        public void Retain() => _retained = true;
        public void ConfirmTerminal() => _retained = false;
        public void Dispose() { if (!_retained) owner.ReleaseRestartFence(); }
    }

    public DateTimeOffset? LastReachedAt { get { lock (_reachabilityLock) return _lastReachedAt; } }
    public DateTimeOffset? LastFailureAt { get { lock (_reachabilityLock) return _lastFailureAt; } }
    public string Reachability
    {
        get
        {
            lock (_reachabilityLock)
            {
                if (_lastSucceeded == false) return "unreachable";
                return _lastSucceeded == true && _lastReachedAt is { } reached
                    && _clock.GetUtcNow() - reached < TimeSpan.FromMinutes(5)
                    ? "reachable" : "unverified";
            }
        }
    }

    public void Reached()
    {
        lock (_reachabilityLock)
        {
            _lastReachedAt = _clock.GetUtcNow();
            _lastSucceeded = true;
        }
    }

    public void Failed()
    {
        lock (_reachabilityLock)
        {
            _lastFailureAt = _clock.GetUtcNow();
            _lastSucceeded = false;
        }
    }

    /// <summary>
    /// The gate host's own slot count, reported to a run of this process that
    /// waits for a slot (AGT-3207). Unlike <see cref="Snapshot"/> it includes
    /// slots held by gate workers of an earlier Studio process.
    /// </summary>
    public void RemoteWaiting(int busy, int total)
    {
        lock (_reachabilityLock) _remote = (new GateSlotUsage(busy, total), _clock.GetUtcNow());
    }

    /// <summary>The last remote slot count, or null when none was reported within <paramref name="maxAge"/>.</summary>
    public GateSlotUsage? RemoteSnapshot(TimeSpan maxAge)
    {
        lock (_reachabilityLock)
            return _remote is { } remote && _clock.GetUtcNow() - remote.At <= maxAge ? remote.Usage : null;
    }

    public void Configure(int total) => Volatile.Write(ref _total, total);

    public void Acquired(string runId, int total)
    {
        Configure(total);
        _active[runId] = 0;
    }

    public void Released(string runId) => _active.TryRemove(runId, out _);
}
