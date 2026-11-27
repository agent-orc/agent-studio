namespace AgentStudio.Diagnostics;

/// <summary>
/// Always-on start-up phase log with a slow-phase watchdog (AGT-4239).
///
/// <para>
/// Every named start-up phase (a checkpoint, a boot sweep, a deferred repair,
/// the hosted-service start) logs one <c>startup-phase</c> line with its
/// elapsed time when it ends. A dedicated watchdog thread logs one
/// <c>startup-phase-slow</c> line per threshold interval for every phase that
/// is still running past the threshold, so a start-up that never becomes
/// healthy names the phase it is stuck in. The watchdog runs on its own
/// thread: a blocked main thread or a starved thread pool cannot silence it.
/// </para>
/// <para>
/// The opt-in <see cref="HostTimelineRecorder"/> keeps recording the same
/// sweeps for its JSON line; this log only adds the operator-visible lines.
/// </para>
/// </summary>
public sealed class StartupPhaseLog : IDisposable
{
    public static readonly TimeSpan DefaultSlowThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly HostTimelineRecorder _timeline;
    private readonly TimeProvider _time;
    private readonly TimeSpan _slowThreshold;
    private readonly TimeSpan _pollInterval;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, RunningPhase> _running = [];
    private readonly List<(string Phase, double Ms)> _pendingCheckpoints = [];
    private readonly ManualResetEventSlim _stop = new(false);
    private long _nextPhaseId;
    private long _checkpointTimestamp;
    private ILogger? _logger;
    private Thread? _watchdog;
    private int _finished;

    public StartupPhaseLog(
        HostTimelineRecorder timeline,
        TimeProvider time,
        TimeSpan? slowThreshold = null,
        TimeSpan? pollInterval = null)
    {
        _timeline = timeline;
        _time = time;
        _slowThreshold = slowThreshold ?? DefaultSlowThreshold;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _checkpointTimestamp = time.GetTimestamp();
    }

    /// <summary>Phases that have begun and not ended yet, oldest first.</summary>
    public IReadOnlyList<string> RunningPhases
    {
        get
        {
            lock (_gate)
                return _running.OrderBy(item => item.Key).Select(item => item.Value.Name).ToList();
        }
    }

    /// <summary>
    /// Starts logging. Checkpoints taken before the logger existed are logged
    /// now, and the watchdog thread starts unless <paramref name="startWatchdog"/>
    /// is false (tests drive <see cref="CheckSlowPhases"/> directly).
    /// </summary>
    public void Attach(ILogger logger, bool startWatchdog = true)
    {
        List<(string Phase, double Ms)> pending;
        lock (_gate)
        {
            _logger = logger;
            pending = [.. _pendingCheckpoints];
            _pendingCheckpoints.Clear();
        }
        foreach (var (phase, ms) in pending)
            logger.LogInformation("startup-phase phase={Phase} elapsedMs={ElapsedMs} outcome={Outcome}", phase, ms, "completed");
        if (!startWatchdog || _watchdog is not null) return;
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "startup-phase-watchdog" };
        _watchdog.Start();
    }

    /// <summary>Records the time since the previous checkpoint as one phase.</summary>
    public void Checkpoint(string phase)
    {
        _timeline.Checkpoint(phase);
        var now = _time.GetTimestamp();
        double ms;
        ILogger? logger;
        lock (_gate)
        {
            ms = Milliseconds(_checkpointTimestamp, now);
            _checkpointTimestamp = now;
            logger = _logger;
            if (logger is null) _pendingCheckpoints.Add((phase, ms));
        }
        logger?.LogInformation("startup-phase phase={Phase} elapsedMs={ElapsedMs} outcome={Outcome}", phase, ms, "completed");
    }

    /// <summary>Runs one named synchronous start-up step as its own phase.</summary>
    public void Run(string name, Action step)
    {
        using var phase = Begin(name);
        try
        {
            _timeline.Sweep(name, step);
        }
        catch
        {
            phase.Fail();
            throw;
        }
    }

    /// <inheritdoc cref="Run(string, Action)"/>
    public T Run<T>(string name, Func<T> step)
    {
        using var phase = Begin(name);
        try
        {
            return _timeline.Sweep(name, step);
        }
        catch
        {
            phase.Fail();
            throw;
        }
    }

    /// <summary>Begins a phase that ends when the returned scope is disposed.</summary>
    public PhaseScope Begin(string name)
    {
        long id;
        lock (_gate)
        {
            id = ++_nextPhaseId;
            _running[id] = new RunningPhase(name, _time.GetTimestamp());
        }
        return new PhaseScope(this, id);
    }

    /// <summary>
    /// Ends the watchdog once start-up work is over. Phases that are still
    /// running keep their own completion line.
    /// </summary>
    public void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;
        _logger?.LogInformation("startup-phases-finished running={Running}", string.Join(",", RunningPhases));
        _stop.Set();
    }

    /// <summary>
    /// Logs every phase that has been running past the threshold, once per
    /// threshold interval. Returns the names it reported.
    /// </summary>
    public IReadOnlyList<string> CheckSlowPhases()
    {
        var now = _time.GetTimestamp();
        var slow = new List<(string Name, double Seconds)>();
        ILogger? logger;
        lock (_gate)
        {
            logger = _logger;
            foreach (var (id, phase) in _running.OrderBy(item => item.Key))
            {
                var elapsed = _time.GetElapsedTime(phase.Started, now);
                var due = _slowThreshold * (phase.SlowReports + 1);
                if (elapsed < due) continue;
                _running[id] = phase with { SlowReports = phase.SlowReports + 1 };
                slow.Add((phase.Name, Math.Round(elapsed.TotalSeconds, 0)));
            }
        }
        foreach (var (name, seconds) in slow)
            logger?.LogWarning(
                "startup-phase-slow phase={Phase} runningSeconds={RunningSeconds} thresholdSeconds={ThresholdSeconds}; start-up is still inside this phase",
                name, seconds, Math.Round(_slowThreshold.TotalSeconds, 0));
        return slow.Select(item => item.Name).ToList();
    }

    public void Dispose()
    {
        _stop.Set();
        _watchdog?.Join(TimeSpan.FromSeconds(2));
    }

    private void End(long id, bool failed)
    {
        RunningPhase phase;
        ILogger? logger;
        lock (_gate)
        {
            if (!_running.Remove(id, out phase!)) return;
            logger = _logger;
        }
        var ms = Milliseconds(phase.Started, _time.GetTimestamp());
        logger?.LogInformation("startup-phase phase={Phase} elapsedMs={ElapsedMs} outcome={Outcome}",
            phase.Name, ms, failed ? "failed" : "completed");
    }

    private void WatchdogLoop()
    {
        while (!_stop.Wait(_pollInterval))
        {
            try
            {
                CheckSlowPhases();
            }
            catch (Exception ex)
            {
                // The watchdog must never take the host down.
                SilentCatch.Note(ex, "StartupPhaseLog: watchdog check failed");
            }
        }
    }

    private double Milliseconds(long from, long to)
        => Math.Round(_time.GetElapsedTime(from, to).TotalMilliseconds, 1);

    private sealed record RunningPhase(string Name, long Started, int SlowReports = 0);

    /// <summary>One running phase; disposing it logs its elapsed time.</summary>
    public sealed class PhaseScope : IDisposable
    {
        private readonly StartupPhaseLog _owner;
        private readonly long _id;
        private bool _failed;
        private int _disposed;

        internal PhaseScope(StartupPhaseLog owner, long id) => (_owner, _id) = (owner, id);

        public void Fail() => _failed = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _owner.End(_id, _failed);
        }
    }
}
