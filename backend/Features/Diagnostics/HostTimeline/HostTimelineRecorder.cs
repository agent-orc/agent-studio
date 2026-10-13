using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Opt-in boot and shutdown timeline for one host (AGT-3107). Off by default.
/// When <see cref="EnvironmentVariable"/> or <see cref="DirectoryKey"/> names a
/// directory, the host appends exactly one JSON line to
/// <c>host-timeline-&lt;pid&gt;.jsonl</c> in that directory when its service
/// provider is disposed. The line holds the duration of each startup phase and
/// named boot sweep, every hosted service's <c>StartAsync</c> and
/// <c>StopAsync</c>, the total stop and dispose, and process counters taken when
/// the host was created and when it was disposed. Comparing the counters across
/// hundreds of in-process test boots shows timers, threads or hosts that outlive
/// a stopped host.
/// <para>
/// All durations come from <see cref="TimeProvider.GetTimestamp"/> and
/// <see cref="TimeProvider.GetElapsedTime(long)"/>, so tests drive the recorder
/// with a fake clock. A disabled recorder only runs the measured work.
/// </para>
/// </summary>
public sealed class HostTimelineRecorder
{
    public const string EnvironmentVariable = "AGENT_STUDIO_HOST_TIMELINE";
    public const string DirectoryKey = "Diagnostics:HostTimeline:Directory";

    private static readonly Lock WriteGate = new();
    private static int s_hostSequence;
    private static int s_liveHosts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time;
    private readonly string? _directory;
    private readonly Func<int, HostProcessCounters> _captureCounters;
    private readonly Lock _gate = new();
    private readonly long _entryTimestamp;
    private long _checkpointTimestamp;
    private long _stoppingTimestamp;
    private long _stoppedTimestamp;
    private readonly List<HostTimelineEntry> _phases = [];
    private readonly List<HostTimelineEntry> _sweeps = [];
    private readonly List<HostTimelineEntry> _starts = [];
    private readonly List<HostTimelineEntry> _stops = [];
    private readonly int _hostSequence;
    private readonly string _profile;
    private readonly string _environment;
    private readonly DateTimeOffset _createdAtUtc;
    private readonly HostProcessCounters? _countersAtStart;
    private double? _applicationStartedMs;
    private bool _completed;

    public static HostTimelineRecorder Disabled { get; } = new(TimeProvider.System);

    private HostTimelineRecorder(TimeProvider time)
    {
        _time = time;
        _captureCounters = HostProcessCounters.Capture;
        _profile = string.Empty;
        _environment = string.Empty;
    }

    private HostTimelineRecorder(
        TimeProvider time,
        string directory,
        long entryTimestamp,
        string profile,
        string environment,
        Func<int, HostProcessCounters> captureCounters)
    {
        _time = time;
        _directory = directory;
        _captureCounters = captureCounters;
        _entryTimestamp = entryTimestamp;
        _checkpointTimestamp = entryTimestamp;
        _profile = profile;
        _environment = environment;
        _createdAtUtc = time.GetUtcNow();
        _hostSequence = Interlocked.Increment(ref s_hostSequence);
        _countersAtStart = captureCounters(Interlocked.Increment(ref s_liveHosts));
    }

    public bool IsEnabled => _directory is not null;

    /// <summary>
    /// Returns an enabled recorder when the environment variable or the
    /// configuration key names a directory, otherwise <see cref="Disabled"/>.
    /// <paramref name="entryTimestamp"/> is the clock reading taken at the top
    /// of the entry point, before the builder existed.
    /// </summary>
    public static HostTimelineRecorder Create(
        long entryTimestamp,
        IConfiguration configuration,
        TimeProvider time,
        string profile,
        string environment,
        Func<string, string?>? readEnvironment = null,
        Func<int, HostProcessCounters>? captureCounters = null)
    {
        var directory = (readEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory)) directory = configuration[DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory)) return Disabled;
        return new HostTimelineRecorder(
            time,
            directory.Trim(),
            entryTimestamp,
            profile,
            environment,
            captureCounters ?? HostProcessCounters.Capture);
    }

    public long GetTimestamp() => _time.GetTimestamp();

    /// <summary>Records the time since the previous checkpoint (or the entry) as <paramref name="phase"/>.</summary>
    public void Checkpoint(string phase)
    {
        if (!IsEnabled) return;
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            _phases.Add(new HostTimelineEntry(phase, Milliseconds(_checkpointTimestamp, now)));
            _checkpointTimestamp = now;
        }
    }

    /// <summary>Runs one named boot sweep and records its duration, also when it throws.</summary>
    public void Sweep(string name, Action sweep)
    {
        if (!IsEnabled)
        {
            sweep();
            return;
        }
        var started = _time.GetTimestamp();
        try
        {
            sweep();
        }
        finally
        {
            Add(_sweeps, name, started);
        }
    }

    /// <inheritdoc cref="Sweep(string, Action)"/>
    public T Sweep<T>(string name, Func<T> sweep)
    {
        if (!IsEnabled) return sweep();
        var started = _time.GetTimestamp();
        try
        {
            return sweep();
        }
        finally
        {
            Add(_sweeps, name, started);
        }
    }

    public void RecordStart(string service, long startedTimestamp) => Add(_starts, service, startedTimestamp);

    public void RecordStop(string service, long startedTimestamp) => Add(_stops, service, startedTimestamp);

    /// <summary>
    /// Hooks the host lifetime and the provider disposal. Call right after
    /// <c>Build()</c> so the disposal probe is one of the first singletons
    /// created and therefore one of the last disposed.
    /// </summary>
    public void Attach(IServiceProvider services)
    {
        if (!IsEnabled) return;
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(OnApplicationStarted);
        lifetime.ApplicationStopping.Register(() => Interlocked.CompareExchange(ref _stoppingTimestamp, _time.GetTimestamp(), 0));
        lifetime.ApplicationStopped.Register(() => Interlocked.CompareExchange(ref _stoppedTimestamp, _time.GetTimestamp(), 0));
        services.GetRequiredService<HostTimelineDisposalProbe>();
    }

    internal void OnApplicationStarted()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            _phases.Add(new HostTimelineEntry("start", Milliseconds(_checkpointTimestamp, now)));
            _checkpointTimestamp = now;
            _applicationStartedMs = Milliseconds(_entryTimestamp, now);
        }
    }

    /// <summary>Writes the host's line once. Called when the service provider disposes the probe.</summary>
    internal void Complete()
    {
        if (!IsEnabled) return;
        var disposed = _time.GetTimestamp();
        HostTimelineLine line;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            var stopping = Interlocked.Read(ref _stoppingTimestamp);
            var stopped = Interlocked.Read(ref _stoppedTimestamp);
            line = new HostTimelineLine(
                Schema: 1,
                Pid: Environment.ProcessId,
                HostSequence: _hostSequence,
                Profile: _profile,
                Environment: _environment,
                CreatedAtUtc: _createdAtUtc,
                ApplicationStartedMs: _applicationStartedMs,
                StopTotalMs: stopping != 0 && stopped != 0 ? Milliseconds(stopping, stopped) : null,
                DisposeMs: stopped != 0 ? Milliseconds(stopped, disposed) : null,
                LifetimeMs: Milliseconds(_entryTimestamp, disposed),
                Phases: [.. _phases],
                Sweeps: [.. _sweeps],
                Starts: [.. _starts],
                Stops: [.. _stops],
                CountersAtStart: _countersAtStart,
                CountersAtStop: _captureCounters(Interlocked.Decrement(ref s_liveHosts)));
        }
        Write(line);
    }

    private void Add(List<HostTimelineEntry> target, string name, long startedTimestamp)
    {
        if (!IsEnabled) return;
        var elapsed = Milliseconds(startedTimestamp, _time.GetTimestamp());
        lock (_gate) target.Add(new HostTimelineEntry(name, elapsed));
    }

    private void Write(HostTimelineLine line)
    {
        var json = JsonSerializer.Serialize(line, JsonOptions);
        try
        {
            Directory.CreateDirectory(_directory!);
            var path = Path.Combine(_directory!, $"host-timeline-{Environment.ProcessId}.jsonl");
            lock (WriteGate) File.AppendAllText(path, json + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagnostics write must never fail a host's disposal.
            SilentCatch.Note(ex, "HostTimelineRecorder: timeline line could not be written.");
        }
    }

    private double Milliseconds(long from, long to)
        => Math.Round(_time.GetElapsedTime(from, to).TotalMilliseconds, 1);
}

public sealed record HostTimelineEntry(string Name, double Ms);

public sealed record HostTimelineLine(
    int Schema,
    int Pid,
    int HostSequence,
    string Profile,
    string Environment,
    DateTimeOffset CreatedAtUtc,
    double? ApplicationStartedMs,
    double? StopTotalMs,
    double? DisposeMs,
    double LifetimeMs,
    IReadOnlyList<HostTimelineEntry> Phases,
    IReadOnlyList<HostTimelineEntry> Sweeps,
    IReadOnlyList<HostTimelineEntry> Starts,
    IReadOnlyList<HostTimelineEntry> Stops,
    HostProcessCounters? CountersAtStart,
    HostProcessCounters CountersAtStop);

/// <summary>Process-wide counters that grow when timers, threads or hosts outlive a stopped host.</summary>
public sealed record HostProcessCounters(
    long ActiveTimers,
    int ThreadPoolThreads,
    long PendingWorkItems,
    long GcHeapBytes,
    int HandleCount,
    int LiveHosts)
{
    public static HostProcessCounters Capture(int liveHosts)
    {
        using var process = Process.GetCurrentProcess();
        return new HostProcessCounters(
            Timer.ActiveCount,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            GC.GetTotalMemory(forceFullCollection: false),
            process.HandleCount,
            liveHosts);
    }
}

/// <summary>
/// Singleton resolved right after <c>Build()</c>. The provider disposes
/// singletons in reverse creation order, so its disposal marks the end of the
/// host's dispose and writes the timeline line.
/// </summary>
public sealed class HostTimelineDisposalProbe(HostTimelineRecorder recorder) : IDisposable
{
    public void Dispose() => recorder.Complete();
}
