using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>One open gate host outage: when it began, the last probe, and the runs it held.</summary>
public sealed record GateHostOutage(
    DateTimeOffset SinceUtc, DateTimeOffset LastAttemptUtc, int FailedProbes, bool Escalated,
    IReadOnlyList<string> AffectedRuns, string LastError)
{
    /// <summary>The transport failure kind that marked the host unreachable (for example <c>connection-refused</c>).</summary>
    public string? Kind { get; init; }

    /// <summary>Project watch paths whose runs the outage held; the outage events go to their feeds.</summary>
    public IReadOnlyList<string> WatchPaths { get; init; } = [];

    public string WaitingReason => MonolithTransitionAdapter.GateHostWaitingReason(SinceUtc);
}

/// <summary>A closed outage, raised once as an Operations event.</summary>
public sealed record GateHostOutageEnded(
    DateTimeOffset SinceUtc, DateTimeOffset EndedUtc, int FailedProbes, bool Escalated,
    IReadOnlyList<string> AffectedRuns)
{
    public IReadOnlyList<string> WatchPaths { get; init; } = [];
}

/// <summary>The answer of one transport probe of the gate host.</summary>
public sealed record GateHostProbeResult(bool Reachable, string? Error = null);

/// <summary>
/// AGT-3792: a cheap check that the gate host transport answers, without a
/// gate run. <see cref="Host"/> is null when no transport probe is configured;
/// the outage then probes with one gate run per backoff step, as before.
/// </summary>
public interface IGateHostProbe
{
    string? Host { get; }

    Task<GateHostProbeResult> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// AGT-3746: process view of gate host availability. A run that fails with a
/// gate host transport failure opens or extends the outage; any run that
/// reaches the host closes it. While it is open, <see cref="TryStartRun"/>
/// admits one probe run per backoff step and holds every other run. The
/// outage is process memory: after a restart the first run probes the host.
/// <para>
/// AGT-3792: the one gate host health state of the installation. Every gate
/// transport attempt, the orphan sweep and the batch gate report here. A
/// few consecutive transport failures of one kind mark the host unreachable
/// from the first of them; with a transport probe (<see cref="IGateHostProbe"/>)
/// no gate run starts during the outage and <see cref="ProbeIfDueAsync"/>
/// checks the host with the same backoff.
/// </para>
/// </summary>
public sealed class GateHostOutageMonitor
{
    private static readonly TimeSpan ConfirmProbeInterval = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly IGateHostProbe? _probe;
    private readonly int _unreachableAfter;
    private readonly Dictionary<string, int> _streak = new(StringComparer.Ordinal);
    private readonly List<string> _streakRuns = [];
    private readonly HashSet<string> _streakWatchPaths = new(StringComparer.Ordinal);
    private DateTimeOffset? _streakSince;
    private int _streakFailures;
    private GateHostOutage? _open;
    private DateTimeOffset? _lastReached;
    private DateTimeOffset? _lastConfirmProbe;
    private bool _probing;

    public GateHostOutageMonitor(TimeProvider time, IGateHostProbe? probe = null,
        int unreachableAfter = AgentStudio.OrchestrationDecisions.GateHostOutagePolicy.DefaultUnreachableAfterFailures)
    {
        _time = time;
        _probe = probe;
        _unreachableAfter = unreachableAfter;
    }

    /// <summary>A gate host outage closed; raised once, outside the monitor lock.</summary>
    public event Action<GateHostOutageEnded>? Ended;

    public GateHostOutage? Current
    {
        get { lock (_lock) return _open; }
    }

    /// <summary>The gate host the probe checks, or null without a transport probe.</summary>
    public string? Host => _probe?.Host;

    /// <summary>True when a transport probe, not a gate run, checks the host during an outage.</summary>
    public bool TransportProbe => !string.IsNullOrWhiteSpace(_probe?.Host);

    /// <summary>The last time a run or probe reached the gate host in this process.</summary>
    public DateTimeOffset? LastReachableUtc
    {
        get { lock (_lock) return _lastReached; }
    }

    /// <summary>A gate replay waits while the host is unreachable; it would only spend a ladder rung.</summary>
    public bool HoldsGateReplay => MonolithTransitionAdapter.GateHostHoldsReplay(Current is not null);

    /// <summary>
    /// True when a card the gate transport escalated at <paramref name="escalatedAtUtc"/>
    /// may return: the host answered after it and no outage is open.
    /// </summary>
    public bool ReturnsTransportEscalation(DateTimeOffset escalatedAtUtc)
    {
        lock (_lock)
            return MonolithTransitionAdapter.GateTransportEscalationReturns(true, escalatedAtUtc, _lastReached,
                _open is not null);
    }

    /// <summary>True while an outage holds every run until its next probe is due.</summary>
    public bool RunsHeld
    {
        get
        {
            lock (_lock)
                return _open is not null && !MonolithTransitionAdapter.GateHostMayStartRun(_open.SinceUtc,
                    _open.LastAttemptUtc, _open.FailedProbes, _time.GetUtcNow(), TransportProbe);
        }
    }

    /// <summary>
    /// True when a gate run may start now. During an outage without a
    /// transport probe a due probe run is admitted and moves the probe clock,
    /// so concurrent callers wait for it.
    /// </summary>
    public bool TryStartRun()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (!MonolithTransitionAdapter.GateHostMayStartRun(_open?.SinceUtc,
                    _open?.LastAttemptUtc ?? now, _open?.FailedProbes ?? 0, now, TransportProbe))
                return false;
            if (_open is not null) _open = _open with { LastAttemptUtc = now };
            return true;
        }
    }

    /// <summary>
    /// A run could not reach the gate host. Counts toward the unreachable
    /// mark, or as a failed probe of the open outage. Null while the host is
    /// not marked unreachable yet.
    /// </summary>
    public GateHostOutage? Unreachable(string runKey, string error, string? watchPath = null)
        => TransportFailed(error, runKey, watchPath);

    /// <summary>
    /// One transport attempt to the gate host failed. Consecutive failures of
    /// one kind mark the host unreachable from the first of them.
    /// </summary>
    public GateHostOutage? TransportFailed(string error, string? runKey = null, string? watchPath = null)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_open is not null)
            {
                _open = Affect(_open, runKey, watchPath) with
                {
                    LastAttemptUtc = now,
                    FailedProbes = _open.FailedProbes + 1,
                    LastError = error,
                };
                return _open;
            }
            var kind = MonolithTransitionAdapter.GateTransportFailureKind(error) ?? "transport";
            _streakSince ??= now;
            _streakFailures++;
            _streak[kind] = _streak.GetValueOrDefault(kind) + 1;
            if (runKey is not null && !_streakRuns.Contains(runKey, StringComparer.Ordinal)) _streakRuns.Add(runKey);
            if (!string.IsNullOrEmpty(watchPath)) _streakWatchPaths.Add(watchPath);
            if (!MonolithTransitionAdapter.GateHostMarksUnreachable(_streak[kind], _unreachableAfter)) return null;
            _open = new GateHostOutage(_streakSince.Value, now, _streakFailures, false, [.. _streakRuns], error)
            {
                Kind = kind,
                WatchPaths = [.. _streakWatchPaths],
            };
            ResetStreak();
            return _open;
        }
    }

    /// <summary>A run held by the outage; it is listed as affected.</summary>
    public void Held(string runKey, string? watchPath = null)
    {
        lock (_lock)
        {
            if (_open is null) return;
            _open = Affect(_open, runKey, watchPath);
        }
    }

    /// <summary>The open outage the first time it outlasts <paramref name="escalateAfter"/>, otherwise null.</summary>
    public GateHostOutage? EscalationDue(TimeSpan escalateAfter)
    {
        lock (_lock)
        {
            if (_open is null || !MonolithTransitionAdapter.GateHostOutageEscalates(_open.SinceUtc,
                    _time.GetUtcNow(), escalateAfter, _open.Escalated))
                return null;
            _open = _open with { Escalated = true };
            return _open;
        }
    }

    /// <summary>A run or probe reached the gate host. Returns the outage it closed, or null.</summary>
    public GateHostOutageEnded? Reachable()
    {
        GateHostOutageEnded? ended = null;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _lastReached = now;
            ResetStreak();
            if (_open is { } open)
            {
                _open = null;
                ended = new GateHostOutageEnded(open.SinceUtc, now, open.FailedProbes, open.Escalated,
                    open.AffectedRuns) { WatchPaths = open.WatchPaths };
            }
        }
        if (ended is not null) Ended?.Invoke(ended);
        return ended;
    }

    /// <summary>
    /// Probes the gate host transport when the open outage's next probe is
    /// due. A probe that answers closes the outage; one that does not counts
    /// as a failed probe and moves the backoff on. Returns the probe answer,
    /// or null when no probe ran.
    /// </summary>
    public async Task<GateHostProbeResult?> ProbeIfDueAsync(CancellationToken ct)
    {
        if (!TransportProbe) return null;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_probing || _open is null || !MonolithTransitionAdapter.GateHostProbeDue(_open.SinceUtc,
                    _open.LastAttemptUtc, _open.FailedProbes, now))
                return null;
            _probing = true;
            _open = _open with { LastAttemptUtc = now };
        }
        return await ProbeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the gate host answered since <paramref name="sinceUtc"/>.
    /// Without such an answer a transport probe runs, at most once a minute;
    /// an open outage answers false without a probe.
    /// </summary>
    public async Task<bool> ConfirmReachableSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_open is not null) return false;
            if (_lastReached > sinceUtc) return true;
            var now = _time.GetUtcNow();
            if (!TransportProbe || _probing
                || _lastConfirmProbe is { } last && now - last < ConfirmProbeInterval)
                return false;
            _probing = true;
            _lastConfirmProbe = now;
        }
        var answer = await ProbeAsync(ct).ConfigureAwait(false);
        return answer.Reachable;
    }

    private async Task<GateHostProbeResult> ProbeAsync(CancellationToken ct)
    {
        GateHostProbeResult answer;
        try
        {
            answer = await _probe!.ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            answer = new GateHostProbeResult(false, error.Message);
        }
        finally
        {
            lock (_lock) _probing = false;
        }
        if (answer.Reachable) Reachable();
        else TransportFailed(answer.Error ?? "the gate host probe did not answer", runKey: null);
        return answer;
    }

    private static GateHostOutage Affect(GateHostOutage open, string? runKey, string? watchPath)
    {
        var affected = runKey is null || open.AffectedRuns.Contains(runKey, StringComparer.Ordinal)
            ? open.AffectedRuns : [.. open.AffectedRuns, runKey];
        var paths = string.IsNullOrEmpty(watchPath) || open.WatchPaths.Contains(watchPath, StringComparer.Ordinal)
            ? open.WatchPaths : [.. open.WatchPaths, watchPath];
        return ReferenceEquals(affected, open.AffectedRuns) && ReferenceEquals(paths, open.WatchPaths)
            ? open : open with { AffectedRuns = affected, WatchPaths = paths };
    }

    private void ResetStreak()
    {
        _streak.Clear();
        _streakRuns.Clear();
        _streakWatchPaths.Clear();
        _streakSince = null;
        _streakFailures = 0;
    }
}

/// <summary>
/// AGT-3792: the plain-words gate host state that Operations (pipeline
/// health) and Needs you show while the host is unreachable.
/// </summary>
public static class GateHostOutageText
{
    public static string Summary(GateHostOutage outage, string? host)
        => $"Gate host {(string.IsNullOrWhiteSpace(host) ? "" : host + " ")}unreachable since "
           + $"{outage.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC";

    /// <param name="waiting">Batch members that wait for the host, in queue order.</param>
    public static string Detail(GateHostOutage outage, string? host, IReadOnlyList<string> waiting)
    {
        var members = waiting.Count == 0
            ? "No batch member waits for it yet."
            : $"{waiting.Count} batch member(s) wait in {TaskStates.DisplayName(TaskStates.AutoReview)}, in order: {string.Join(", ", waiting)}.";
        return $"The gate transport fails because {MonolithTransitionAdapter.GateTransportFailureKindText(outage.Kind)} "
               + $"({outage.FailedProbes} failed attempt(s) and probe(s); last error: {OneLine(outage.LastError)}). "
               + $"No batch forms until the host answers. {members} "
               + MonolithTransitionAdapter.GateHostOwnerAdvice(host);
    }

    private static string OneLine(string text)
    {
        var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 240 ? line : line[..240] + "...";
    }
}
