using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record CpuMaxLimit(string Raw, double? QuotaCores, double? QuotaPercent)
{
    public bool IsUnlimited => QuotaCores is null;
}

/// <summary>
/// Reads the aggregate review-role cgroup budget and pressure. Stateful only
/// for counter deltas and bounded rolling review durations; parsing and policy
/// inputs remain directly testable.
/// </summary>
public sealed class ReviewPlaneBudgetProbe
{
    public const double SustainedThrottleShareThreshold = 0.10;
    private const int DurationWindow = 20;
    private const string OnlineCpuPath = "/sys/devices/system/cpu/online";

    private readonly Queue<double> _durations = new();
    private long? _previousThrottledUsec;
    private DateTime? _previousObservedAt;
    private int _highThrottleSamples;

    public void RecordReviewDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        _durations.Enqueue(duration.TotalSeconds);
        while (_durations.Count > DurationWindow) _durations.Dequeue();
    }

    public ReviewPlaneBudgetDto Sample(
        RunnerOptions options,
        int currentCeiling,
        DateTime? observedAtUtc = null)
    {
        var now = (observedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var hostCores = HostCpuCount();
        var envelope = WorkerResourceEnvelope.FromOptions(options);
        var unitCgroup = ResolveUnitCgroupDirectory();
        var cpuMaxRaw = ReadOrDefault(unitCgroup is null ? null : Path.Combine(unitCgroup, "cpu.max"), "max 100000");
        var cpuMax = ParseCpuMax(cpuMaxRaw);
        var planeCores = Math.Min(hostCores, cpuMax.QuotaCores ?? hostCores);
        var throttledUsec = ParseCpuStatThrottledUsec(
            ReadOrDefault(unitCgroup is null ? null : Path.Combine(unitCgroup, "cpu.stat"), string.Empty));
        var share = ThrottledShare(
            _previousThrottledUsec,
            _previousObservedAt,
            throttledUsec,
            now);
        _previousThrottledUsec = throttledUsec;
        _previousObservedAt = now;
        _highThrottleSamples = share >= SustainedThrottleShareThreshold
            ? _highThrottleSamples + 1
            : 0;
        var sustained = _highThrottleSamples >= 2;

        return new ReviewPlaneBudgetDto(
            now,
            cpuMax.Raw,
            planeCores,
            cpuMax.QuotaPercent,
            hostCores,
            envelope.CoresPerSlot,
            envelope.CpuQuotaPercent,
            Math.Max(0, currentCeiling),
            _durations.Count == 0 ? null : _durations.Average(),
            share,
            sustained,
            sustained
                ? "Review CPU throttling is sustained. Raise the review role quota or lower the review ceiling."
                : null);
    }

    public static CpuMaxLimit ParseCpuMax(string value)
    {
        var normalized = value.Trim();
        var fields = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period)
            || period <= 0)
            throw new FormatException($"Invalid cgroup cpu.max value '{value}'.");
        if (string.Equals(fields[0], "max", StringComparison.Ordinal))
            return new CpuMaxLimit($"max {period}", null, null);
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            || quota <= 0)
            throw new FormatException($"Invalid cgroup cpu.max value '{value}'.");
        var cores = quota / (double)period;
        return new CpuMaxLimit(
            $"{quota} {period}",
            cores,
            cores * 100);
    }

    public static long ParseCpuStatThrottledUsec(string value)
    {
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2
                && fields[0] == "throttled_usec"
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
                return Math.Max(0, counter);
        }
        return 0;
    }

    public static int ParseOnlineCpuCount(string value)
    {
        var count = 0;
        foreach (var segment in value.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = segment.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2
                || !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                || first < 0)
                throw new FormatException($"Invalid online CPU list '{value}'.");
            var last = first;
            if (range.Length == 2
                && (!int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out last)
                    || last < first))
                throw new FormatException($"Invalid online CPU list '{value}'.");
            count = checked(count + last - first + 1);
        }
        return count > 0 ? count : throw new FormatException($"Invalid online CPU list '{value}'.");
    }

    public static double? ThrottledShare(
        long? previousUsec,
        DateTime? previousAt,
        long currentUsec,
        DateTime currentAt)
    {
        if (previousUsec is null || previousAt is null || currentAt <= previousAt || currentUsec < previousUsec)
            return null;
        var wallUsec = (currentAt - previousAt.Value).TotalMilliseconds * 1000;
        return wallUsec <= 0 ? null : Math.Clamp((currentUsec - previousUsec.Value) / wallUsec, 0, 1);
    }

    private static string? ResolveUnitCgroupDirectory()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var line = File.ReadLines("/proc/self/cgroup")
            .FirstOrDefault(item => item.StartsWith("0::", StringComparison.Ordinal));
        if (line is null) return null;
        var relative = line[3..].TrimStart('/');
        var directory = Path.Combine(WorkerCgroup.DefaultMountRoot, relative);
        return string.Equals(Path.GetFileName(directory), WorkerCgroup.DaemonLeafName, StringComparison.Ordinal)
            ? Path.GetDirectoryName(directory)
            : directory;
    }

    private static int HostCpuCount()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists(OnlineCpuPath))
                    return ParseOnlineCpuCount(File.ReadAllText(OnlineCpuPath));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        return Math.Max(1, Environment.ProcessorCount);
    }

    private static string ReadOrDefault(string? path, string fallback)
    {
        try { return path is not null && File.Exists(path) ? File.ReadAllText(path) : fallback; }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }
}
