using System.Collections.Concurrent;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed record GateSlotUsage(int Busy, int Total);

/// <summary>One slot of the gate host and the gate of this process that holds it, if any.</summary>
public sealed record GateSlotBoardSlot(int Slot, bool ShortSlot, string? Project, string? GateId,
    GateDurationClass? DurationClass, DateTimeOffset? Since);

/// <summary>A gate of this process that waits for a slot, and why.</summary>
public sealed record GateSlotBoardWaiter(string Project, string GateId, GateDurationClass DurationClass,
    DateTimeOffset Since, string Reason);

/// <summary>Which slot runs what and why a gate waits (AGT-3423).</summary>
public sealed record GateSlotBoard(int Total, int? ShortSlot, string Rule,
    IReadOnlyList<GateSlotBoardSlot> Slots, IReadOnlyList<GateSlotBoardWaiter> Waiting);

/// <summary>Live slot use observed by this dispatching API process.</summary>
public sealed class GateSlotUsageTracker
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly object _dispatchGate = new();
    private readonly HashSet<string> _dispatching = new(StringComparer.Ordinal);
    private bool _restartFenced;
    private readonly TimeProvider _clock;
    private readonly GateCapacitySettings? _capacity;
    private readonly object _reachabilityLock = new();
    private int _total;
    private DateTimeOffset? _lastReachedAt;
    private DateTimeOffset? _lastFailureAt;
    private bool? _lastSucceeded;
    private (GateSlotUsage Usage, DateTimeOffset At)? _remote;
    private readonly ConcurrentDictionary<string, GateRun> _runs = new(StringComparer.Ordinal);

    private sealed record GateRun(string Project, string GateId, GateDurationClass DurationClass,
        DateTimeOffset QueuedAt, int? Slot = null, DateTimeOffset? AcquiredAt = null,
        DateTimeOffset? WaitingSince = null);

    public GateSlotUsageTracker(IConfiguration? configuration = null, TimeProvider? clock = null,
        GateCapacitySettings? capacity = null)
    {
        _clock = clock ?? TimeProvider.System;
        _capacity = capacity;
        var configured = configuration?.GetValue("RemoteGate:GateSlots", 1) ?? 1;
        _total = configured is >= 1 and <= 64 ? configured : 1;
    }

    private int CurrentTotal => _capacity is { HasOverride: true }
        ? _capacity.Current().Slots : Volatile.Read(ref _total);

    public GateSlotUsage Snapshot() => new(_active.Count, CurrentTotal);

    /// <summary>Use the gate host's recent report when it has one; it includes workers from older server processes.</summary>
    public GateSlotUsage ReportedOrLocalSnapshot()
    {
        var remote = RemoteSnapshot(TimeSpan.FromMinutes(10));
        return remote ?? Snapshot();
    }

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

    public IRestartClaimFence? TryFenceDispatch(bool requireFreshRemote = false)
    {
        lock (_dispatchGate)
        {
            if (_restartFenced || _dispatching.Count > 0 || !_active.IsEmpty
                || requireFreshRemote && RemoteSnapshot(TimeSpan.FromMinutes(10)) is not { Busy: 0 }) return null;
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

    /// <summary>The gate host reports that <paramref name="runId"/> waits for a slot.</summary>
    public void RemoteWaiting(string runId, int busy, int total)
    {
        RemoteWaiting(busy, total);
        Configure(total);
        if (_runs.TryGetValue(runId, out var run) && run.WaitingSince is null && run.AcquiredAt is null)
            _runs.TryUpdate(runId, run with { WaitingSince = _clock.GetUtcNow() }, run);
    }

    /// <summary>The last remote slot count, or null when none was reported within <paramref name="maxAge"/>.</summary>
    public GateSlotUsage? RemoteSnapshot(TimeSpan maxAge)
    {
        lock (_reachabilityLock)
            return _remote is { } remote && _clock.GetUtcNow() - remote.At <= maxAge ? remote.Usage : null;
    }

    public void Configure(int total) => Volatile.Write(ref _total, total);

    /// <summary>A dispatched gate starts to queue for a slot of this host.</summary>
    public void Queued(string runId, string project, string gateId, GateDurationClass durationClass)
        => _runs[runId] = new GateRun(project, gateId, durationClass, _clock.GetUtcNow());

    public void Acquired(string runId, int total) => Acquired(runId, null, total);

    public void Acquired(string runId, int? slot, int total)
    {
        Configure(total);
        _active[runId] = 0;
        if (_runs.TryGetValue(runId, out var run))
            _runs.TryUpdate(runId, run with { Slot = slot, AcquiredAt = _clock.GetUtcNow() }, run);
    }

    public void Released(string runId)
    {
        _active.TryRemove(runId, out _);
        _runs.TryRemove(runId, out _);
    }

    /// <summary>How long the run held its slot so far, or null when it never held one.</summary>
    public TimeSpan? HeldFor(string runId)
        => _runs.TryGetValue(runId, out var run) && run.AcquiredAt is { } at ? _clock.GetUtcNow() - at : null;

    public GateSlotBoard Board()
    {
        var total = CurrentTotal;
        var now = _clock.GetUtcNow();
        var runs = _runs.Values.ToArray();
        var visibleSlots = Math.Max(total, runs.Where(run => run.Slot is >= 0)
            .Select(run => run.Slot!.Value + 1).DefaultIfEmpty(0).Max());
        var holders = runs.Where(run => run.Slot is { } slot && slot < visibleSlots)
            .GroupBy(run => run.Slot!.Value).ToDictionary(group => group.Key, group => group.First());
        var shortSlot = GateSlotFairnessPolicy.ReservedSlot(total);
        var slots = Enumerable.Range(0, visibleSlots).Select(slot => holders.TryGetValue(slot, out var run)
            ? new GateSlotBoardSlot(slot, slot == shortSlot, run.Project, run.GateId, run.DurationClass, run.AcquiredAt)
            : new GateSlotBoardSlot(slot, slot == shortSlot, null, null, null, null)).ToArray();
        var waiting = runs.Where(run => run.AcquiredAt is null).OrderBy(run => run.QueuedAt)
            .Select(run => new GateSlotBoardWaiter(run.Project, run.GateId, run.DurationClass,
                run.WaitingSince ?? run.QueuedAt,
                run.WaitingSince is { } since
                    ? GateSlotFairnessPolicy.WaitReason(total, run.DurationClass, now - since)
                    : "Preparing the source on the gate host; no slot requested yet.")).ToArray();
        return new GateSlotBoard(total, shortSlot, GateSlotFairnessPolicy.Describe(total), slots, waiting);
    }
}
