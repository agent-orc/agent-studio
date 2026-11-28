using System.Globalization;

namespace AgentRunner;

/// <summary>Charge elapsed time only while the worker cgroup could make CPU progress.</summary>
internal sealed class MechanicalRoundDeadline(TimeSpan limit, long? initialStallMicroseconds)
{
    private TimeSpan _charged;
    private TimeSpan _lastWall;
    private long? _lastStall = initialStallMicroseconds;

    internal TimeSpan ExcludedCpuWait { get; private set; }

    internal TimeSpan Remaining(TimeSpan wall, long? stallMicroseconds)
    {
        var interval = wall > _lastWall ? wall - _lastWall : TimeSpan.Zero;
        var excluded = _lastStall is { } previous && stallMicroseconds is { } current && current >= previous
            ? TimeSpan.FromTicks(Math.Min(current - previous, interval.Ticks / 10) * 10) : TimeSpan.Zero;
        _charged += interval - excluded;
        ExcludedCpuWait += excluded;
        _lastWall = wall;
        _lastStall = stallMicroseconds;
        return limit > _charged ? limit - _charged : TimeSpan.Zero;
    }

    internal static long? ReadCpuStallMicroseconds(string workerDirectory)
    {
        try
        {
            // Use only this worker's cgroup. Host-wide pressure would grant
            // extra time for unrelated jobs. "full" counts intervals in which
            // all runnable tasks in this group wait for CPU, including tools.
            var directory = File.ReadAllText(Path.Combine(workerDirectory, WorkerCgroup.MarkerFileName)).Trim();
            return ParseCpuStallMicroseconds(File.ReadAllText(Path.Combine(directory, "cpu.pressure")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static long? ParseCpuStallMicroseconds(string pressure)
    {
        foreach (var line in pressure.Split('\n'))
            if (line.StartsWith("full ", StringComparison.Ordinal))
                foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (field.StartsWith("total=", StringComparison.Ordinal)
                        && long.TryParse(field.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
                        return total;
        return null;
    }

    internal static async Task WaitAsync(int seconds, string workerDirectory, Action<string, string> append,
        CancellationToken ct)
    {
        var initial = ReadCpuStallMicroseconds(workerDirectory);
        var budget = new MechanicalRoundDeadline(TimeSpan.FromSeconds(seconds), initial);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        append("system", $"[runner] mechanical duration measure={(initial is null ? "wall-clock (CPU pressure unavailable)" : "elapsed minus worker cgroup CPU full stall")} limitSeconds={seconds}");
        try
        {
            while (true)
            {
                var remaining = budget.Remaining(watch.Elapsed, ReadCpuStallMicroseconds(workerDirectory));
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct);
            }
        }
        finally
        {
            append("system", FormattableString.Invariant($"[runner] mechanical duration wallSeconds={watch.Elapsed.TotalSeconds:0.000} excludedCpuWaitSeconds={budget.ExcludedCpuWait.TotalSeconds:0.000}"));
        }
    }
}
