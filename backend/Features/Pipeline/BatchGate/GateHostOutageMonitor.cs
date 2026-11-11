using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>One open gate host outage: when it began, the last probe, and the runs it held.</summary>
public sealed record GateHostOutage(
    DateTimeOffset SinceUtc, DateTimeOffset LastAttemptUtc, int FailedProbes, bool Escalated,
    IReadOnlyList<string> AffectedRuns, string LastError)
{
    public string WaitingReason => MonolithTransitionAdapter.GateHostWaitingReason(SinceUtc);
}

/// <summary>A closed outage, raised once as an Operations event.</summary>
public sealed record GateHostOutageEnded(
    DateTimeOffset SinceUtc, DateTimeOffset EndedUtc, int FailedProbes, bool Escalated,
    IReadOnlyList<string> AffectedRuns);

/// <summary>
/// AGT-3746: process view of gate host availability. A run that fails with a
/// gate host transport failure opens or extends the outage; any run that
/// reaches the host closes it. While it is open, <see cref="TryStartRun"/>
/// admits one probe run per backoff step and holds every other run. The
/// outage is process memory: after a restart the first run probes the host.
/// </summary>
public sealed class GateHostOutageMonitor(TimeProvider time)
{
    private readonly object _lock = new();
    private GateHostOutage? _open;

    public GateHostOutage? Current
    {
        get { lock (_lock) return _open; }
    }

    /// <summary>True while an outage holds every run until its next probe is due.</summary>
    public bool RunsHeld
    {
        get
        {
            lock (_lock)
                return _open is not null && !MonolithTransitionAdapter.GateHostMayStartRun(_open.SinceUtc,
                    _open.LastAttemptUtc, _open.FailedProbes, time.GetUtcNow());
        }
    }

    /// <summary>
    /// True when a gate run may start now. During an outage a due probe is
    /// admitted and moves the probe clock, so concurrent callers wait for it.
    /// </summary>
    public bool TryStartRun()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (!MonolithTransitionAdapter.GateHostMayStartRun(_open?.SinceUtc,
                    _open?.LastAttemptUtc ?? now, _open?.FailedProbes ?? 0, now))
                return false;
            if (_open is not null) _open = _open with { LastAttemptUtc = now };
            return true;
        }
    }

    /// <summary>A run could not reach the gate host. Opens the outage or counts a failed probe.</summary>
    public GateHostOutage Unreachable(string runKey, string error)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            _open = _open is null
                ? new GateHostOutage(now, now, 1, false, [runKey], error)
                : _open with
                {
                    LastAttemptUtc = now,
                    FailedProbes = _open.FailedProbes + 1,
                    AffectedRuns = _open.AffectedRuns.Contains(runKey, StringComparer.Ordinal)
                        ? _open.AffectedRuns : [.. _open.AffectedRuns, runKey],
                    LastError = error,
                };
            return _open;
        }
    }

    /// <summary>A run held by the outage; it is listed as affected.</summary>
    public void Held(string runKey)
    {
        lock (_lock)
        {
            if (_open is null || _open.AffectedRuns.Contains(runKey, StringComparer.Ordinal)) return;
            _open = _open with { AffectedRuns = [.. _open.AffectedRuns, runKey] };
        }
    }

    /// <summary>The open outage the first time it outlasts <paramref name="escalateAfter"/>, otherwise null.</summary>
    public GateHostOutage? EscalationDue(TimeSpan escalateAfter)
    {
        lock (_lock)
        {
            if (_open is null || !MonolithTransitionAdapter.GateHostOutageEscalates(_open.SinceUtc,
                    time.GetUtcNow(), escalateAfter, _open.Escalated))
                return null;
            _open = _open with { Escalated = true };
            return _open;
        }
    }

    /// <summary>A run reached the gate host. Returns the outage it closed, or null.</summary>
    public GateHostOutageEnded? Reachable()
    {
        lock (_lock)
        {
            if (_open is not { } open) return null;
            _open = null;
            return new GateHostOutageEnded(open.SinceUtc, time.GetUtcNow(), open.FailedProbes,
                open.Escalated, open.AffectedRuns);
        }
    }
}
