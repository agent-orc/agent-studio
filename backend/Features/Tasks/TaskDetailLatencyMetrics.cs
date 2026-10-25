namespace AgentStudio.Tasks;

/// <summary>Recent completed card reads, including response serialization.</summary>
public sealed class TaskDetailLatencyMetrics
{
    private const int Capacity = 512;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly (DateTimeOffset At, double Milliseconds)[] _samples = new (DateTimeOffset, double)[Capacity];
    private int _next;
    private int _count;

    public TaskDetailLatencyMetrics(TimeProvider clock) => _clock = clock;

    public void Observe(double milliseconds)
    {
        lock (_lock)
        {
            _samples[_next] = (_clock.GetUtcNow(), milliseconds);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    public TaskDetailLatencySnapshot Snapshot()
    {
        double[] durations;
        lock (_lock)
        {
            var cutoff = _clock.GetUtcNow() - Window;
            durations = _samples.Take(_count)
                .Where(sample => sample.At >= cutoff)
                .Select(sample => sample.Milliseconds)
                .OrderBy(value => value)
                .ToArray();
        }
        return new TaskDetailLatencySnapshot(durations.Length,
            durations.Length == 0 ? null : durations[(int)Math.Ceiling(durations.Length * 0.5) - 1],
            durations.Length == 0 ? null : durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            durations.Length == 0 ? null : durations[^1]);
    }
}

public sealed record TaskDetailLatencySnapshot(int Count, double? P50Ms, double? P95Ms, double? MaxMs);
