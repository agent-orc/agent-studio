using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>Opt-in, bounded diagnostics for a single task-detail read.</summary>
internal sealed class TaskSwitchTrace
{
    private static readonly AsyncLocal<TaskSwitchTrace?> Ambient = new();
    private static readonly string[] StageNames =
    [
        "index.lookup", "index.wait", "index.refresh", "task.json", "sidecar.status",
        "sidecar.prompt", "sidecar.history", "sidecar.log", "sidecar.evidence",
        "sidecar.other", "commits", "integration", "merge", "publish", "tests",
        "review", "tokens", "runtime", "dependencies", "serialize.write"
    ];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly double[] _elapsed = new double[StageNames.Length];
    private readonly int[] _counts = new int[StageNames.Length];
    private readonly int[] _fileReads = new int[StageNames.Length];
    private readonly object _gate = new();
    private readonly AsyncLocal<SpanHandle?> _activeSpan = new();
    private readonly TaskSwitchTrace? _previous;
    private int _spanCount;

    private TaskSwitchTrace(string requestId, string switchId)
    {
        RequestId = requestId;
        SwitchId = switchId;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public string RequestId { get; }
    public string SwitchId { get; }
    public static TaskSwitchTrace? Current => Ambient.Value;
    public static TaskSwitchTrace Begin(string? requestId, string? switchId) =>
        new(ParseId(requestId), ParseId(switchId));

    internal static string ParseId(string? candidate) =>
        Guid.TryParse(candidate, out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");

    public static IDisposable Span(string name)
    {
        var trace = Ambient.Value;
        if (trace == null || Array.IndexOf(StageNames, name) < 0 ||
            Interlocked.Increment(ref trace._spanCount) > 128)
            return Noop.Instance;
        var handle = new SpanHandle(trace, name, Stopwatch.GetTimestamp(), trace._activeSpan.Value);
        trace._activeSpan.Value = handle;
        return handle;
    }

    public static T Run<T>(string name, Func<T> operation)
    {
        using var span = Span(name);
        return operation();
    }

    public static void FileRead()
    {
        var trace = Ambient.Value;
        var name = trace?._activeSpan.Value?.Name;
        if (name == null) return;
        var slot = Array.IndexOf(StageNames, name);
        if (slot >= 0) lock (trace!._gate) trace._fileReads[slot]++;
    }

    private void Record(string name, double elapsedMs)
    {
        var slot = Array.IndexOf(StageNames, name);
        if (slot < 0) return;
        lock (_gate)
        {
            _elapsed[slot] += Math.Max(0, elapsedMs);
            _counts[slot]++;
        }
    }

    public string Summary(string outcome, int status, long bytes, (int Spawns, long GitMs, int FileReads)? git, int gitTimeouts = 0)
    {
        var stages = new Dictionary<string, object>(StringComparer.Ordinal);
        lock (_gate)
        {
            for (var i = 0; i < StageNames.Length; i++)
                if (_counts[i] > 0) stages[StageNames[i]] = new
                {
                    ms = Math.Round(_elapsed[i], 3), count = _counts[i], files = _fileReads[i]
                };
        }
        return JsonSerializer.Serialize(new
        {
            requestId = RequestId, switchId = SwitchId, outcome, status,
            wallMs = Math.Round(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, 3),
            bytes, gitSpawns = git?.Spawns ?? 0, gitMs = git?.GitMs ?? 0, gitTimeouts,
            stages
        });
    }

    public void RecordWrite(long started) => Record("serialize.write", Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    public void Restore() => Ambient.Value = _previous;

    private sealed class SpanHandle(TaskSwitchTrace trace, string name, long started, SpanHandle? parent) : IDisposable
    {
        public string Name => name;
        private int _disposed;
        private double _childMs;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double childMs;
            lock (this) childMs = _childMs;
            trace.Record(name, wallMs - childMs);
            if (parent != null)
                lock (parent) parent._childMs += wallMs;
            trace._activeSpan.Value = parent;
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
