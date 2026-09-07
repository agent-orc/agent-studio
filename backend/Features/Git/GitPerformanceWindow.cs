namespace AgentStudio.Git;

/// <summary>
/// The rolling one-hour view of what <see cref="GitProcessTelemetry"/> already
/// measures, so the Admin page can render per-endpoint p50/p95 and a
/// spawns-per-minute rate without a second set of counters (AGT-2726
/// requirement 4). Every sample here is fed by the existing telemetry scope
/// and the existing spawn recorder; nothing instruments git twice.
/// </summary>
internal sealed class GitPerformanceWindow
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <summary>
    /// Per-label sample ceiling. An hour of the measured worst case
    /// (tasks/grouped at ~6 polls/minute) is ~360 samples; the cap only exists
    /// so a pathological caller cannot grow this without bound.
    /// </summary>
    private const int MaxSamplesPerLabel = 4096;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Sample>> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _spawnsPerMinute = [];

    internal void RecordRequest(string label, DateTimeOffset at, long wallMs, int spawns)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(label, out var list))
            {
                list = [];
                _samples[label] = list;
            }
            list.Add(new Sample(at, wallMs, spawns));
            if (list.Count > MaxSamplesPerLabel) list.RemoveRange(0, list.Count - MaxSamplesPerLabel);
        }
    }

    internal void RecordSpawn(DateTimeOffset at)
    {
        var minute = at.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            _spawnsPerMinute[minute] = _spawnsPerMinute.GetValueOrDefault(minute) + 1;
        }
    }

    /// <summary>
    /// Percentiles over the retained window. Trimming happens here rather than
    /// on every record so the hot path stays a single list append.
    /// </summary>
    internal GitPerformanceSnapshot Snapshot(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        var cutoffMinute = cutoff.ToUnixTimeSeconds() / 60;
        var endpoints = new List<GitEndpointPerformance>();
        int spawnsInWindow;
        int spawnsLastMinute;

        lock (_gate)
        {
            foreach (var label in _samples.Keys.ToList())
            {
                var list = _samples[label];
                list.RemoveAll(sample => sample.At < cutoff);
                if (list.Count == 0)
                {
                    _samples.Remove(label);
                    continue;
                }
                var durations = list.Select(sample => sample.WallMs).Order().ToArray();
                endpoints.Add(new GitEndpointPerformance(
                    label,
                    list.Count,
                    Percentile(durations, 0.50),
                    Percentile(durations, 0.95),
                    durations[^1],
                    list.Sum(sample => sample.Spawns)));
            }

            foreach (var minute in _spawnsPerMinute.Keys.ToList())
                if (minute < cutoffMinute) _spawnsPerMinute.Remove(minute);

            spawnsInWindow = _spawnsPerMinute.Values.Sum();
            spawnsLastMinute = _spawnsPerMinute.GetValueOrDefault(now.ToUnixTimeSeconds() / 60)
                + _spawnsPerMinute.GetValueOrDefault(now.ToUnixTimeSeconds() / 60 - 1);
        }

        // The measured minutes, not the wall hour: a backend that started five
        // minutes ago must not report a twelfth of its real spawn rate.
        var minutes = Math.Max(1d, Math.Min(Retention.TotalMinutes, ObservedMinutes(now)));
        return new GitPerformanceSnapshot(
            endpoints.OrderByDescending(endpoint => endpoint.P95Ms).ToList(),
            spawnsInWindow,
            Math.Round(spawnsInWindow / minutes, 2),
            // "Spawns per minute" as an alarm needs the recent rate, not the
            // hour average that a single burst disappears into.
            spawnsLastMinute);
    }

    private double ObservedMinutes(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_spawnsPerMinute.Count == 0) return 1;
            var oldest = _spawnsPerMinute.Keys.Min();
            return Math.Max(1, now.ToUnixTimeSeconds() / 60 - oldest + 1);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
            _spawnsPerMinute.Clear();
        }
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending array. Nearest-rank (rather
    /// than an interpolated variant) keeps every reported value a latency that
    /// actually happened, which is what an SLO reader needs.
    /// </summary>
    internal static long Percentile(long[] ascending, double percentile)
    {
        if (ascending.Length == 0) return 0;
        var rank = (int)Math.Ceiling(percentile * ascending.Length);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Length - 1)];
    }

    private readonly record struct Sample(DateTimeOffset At, long WallMs, int Spawns);
}

/// <summary>One endpoint's latency profile over the retained window.</summary>
public sealed record GitEndpointPerformance(
    string Endpoint,
    int Calls,
    long P50Ms,
    long P95Ms,
    long MaxMs,
    int Spawns);

/// <summary>The whole git performance picture the Admin panel renders.</summary>
public sealed record GitPerformanceSnapshot(
    IReadOnlyList<GitEndpointPerformance> Endpoints,
    int SpawnsInWindow,
    double SpawnsPerMinute,
    int SpawnsLastMinute);
