using System.Globalization;
using System.Runtime.InteropServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Stateful, dependency-free host sampler. Linux reads /proc; Windows uses kernel counters.</summary>
public sealed class HostTelemetrySampler
{
    private readonly RunnerOptions? _options;
    private readonly IHostFilesystemProbe _probe;
    private readonly string _proc;
    private readonly Func<long?>? _preparationCopyBytes;

    public HostTelemetrySampler(RunnerOptions? options = null, IHostFilesystemProbe? probe = null,
        Func<long?>? preparationCopyBytes = null, string proc = "/proc")
    {
        _options = options;
        _proc = proc;
        _preparationCopyBytes = preparationCopyBytes;
        _probe = probe ?? HostFilesystemProbes.Default;
    }
    private CpuCounters? _previousCpu;
    private (long InPages, long OutPages, DateTime At)? _previousSwap;
    private (HostNetworkCounters Counters, DateTime At)? _previousNetwork;
    private DateTime _nextAt = DateTime.MinValue;

    public HostTelemetrySample? SampleIfDue(
        int activeSlots,
        TaskServerConnectivitySnapshot? connectivity = null,
        bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < _nextAt) return null;
        return SampleNow(activeSlots, connectivity, now);
    }

    /// <summary>Capture a fresh sample for a host-local admission decision.</summary>
    public HostTelemetrySample SampleNow(
        int activeSlots,
        TaskServerConnectivitySnapshot? connectivity = null)
        => SampleNow(activeSlots, connectivity, DateTime.UtcNow);

    private HostTelemetrySample SampleNow(
        int activeSlots,
        TaskServerConnectivitySnapshot? connectivity,
        DateTime now)
    {
        _nextAt = now.AddSeconds(30);
        var sample = OperatingSystem.IsLinux()
            ? SampleLinux(now, activeSlots)
            : SampleWindows(now, activeSlots);
        var paths = new List<(string Purpose, string Path)>
        {
            ("runner work", _options?.WorkDir ?? Environment.CurrentDirectory),
            ("runner state", _options?.StateDir ?? Environment.CurrentDirectory),
            ("review work", _options?.ReviewWorkDir ?? Environment.CurrentDirectory),
        };
        if (!string.IsNullOrWhiteSpace(_options?.SalvageDir))
            paths.Add(("salvage", _options.SalvageDir));
        foreach (var containerPath in new[] { "/var/lib/docker", "/var/lib/containers" }.Where(Directory.Exists))
            paths.Add(("container storage", containerPath));
        var filesystems = _probe.Measure(paths);
        if (HostDiskPressurePolicy.Decide(filesystems, new HostDiskPressureThresholds()).Level != HostDiskPressureLevel.Healthy)
            filesystems = _probe.Measure(paths,
                [("runner worktrees", _options?.WorkDir ?? Environment.CurrentDirectory),
                 ("logs", _options?.StateDir ?? Environment.CurrentDirectory),
                 ("result archives", _options?.SalvageDir ?? Environment.CurrentDirectory)]);
        if (_preparationCopyBytes?.Invoke() is { } bytes)
            filesystems = filesystems.Select(disk => disk.Purpose == "runner work"
                ? disk with { OwnedBytes = new Dictionary<string, long>(disk.OwnedBytes ?? new Dictionary<string, long>())
                    { [HostDiskPressurePolicy.PreparationCopies] = bytes } }
                : disk).ToArray();
        var network = OperatingSystem.IsLinux() ? NetworkRate(now) : null;
        return WithConnectivity(sample with
        {
            Filesystems = filesystems,
            Placement = HostPlacementLocator.Current(_options),
            NetworkReceiveBytesPerSecond = network?.Receive,
            NetworkTransmitBytesPerSecond = network?.Transmit,
        }, connectivity);
    }

    /// <summary>Bytes per second over every interface except loopback since the previous sample.</summary>
    private (long Receive, long Transmit)? NetworkRate(DateTime now)
    {
        HostNetworkCounters? current;
        try { current = HostNetworkCounters.Parse(File.ReadLines(Path.Combine(_proc, "net/dev"))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { current = null; }
        if (current is null) { _previousNetwork = null; return null; }
        var rate = _previousNetwork is { } previous
            ? HostNetworkCounters.Rate(previous.Counters, previous.At, current.Value, now) : null;
        _previousNetwork = (current.Value, now);
        return rate;
    }

    private HostTelemetrySample SampleLinux(DateTime now, int activeSlots)
    {
        var stat = ProcLines("stat");
        var current = stat.Length == 0 ? null : ReadLinuxCpu(stat[0]);
        var (cpu, steal, ioWait) = Percentages(_previousCpu, current);
        _previousCpu = current;

        var loads = (ProcLines("loadavg").FirstOrDefault() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var memory = ProcLines("meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => ParseKb(parts[1]), StringComparer.Ordinal);
        var total = memory.GetValueOrDefault("MemTotal");
        long? available = memory.TryGetValue("MemAvailable", out var availableBytes) || memory.TryGetValue("MemFree", out availableBytes)
            ? availableBytes : null;
        long? swapTotal = memory.TryGetValue("SwapTotal", out var swapBytes) ? swapBytes : null;
        var swapUsed = memory.TryGetValue("SwapFree", out var swapFree) ? swapTotal - swapFree : null;

        var vm = ProcLines("vmstat")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && (parts[0] == "pswpin" || parts[0] == "pswpout"))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
        var currentSwap = (vm.GetValueOrDefault("pswpin"), vm.GetValueOrDefault("pswpout"), now);
        long? swapIn = null, swapOut = null;
        if (vm.Count == 2 && _previousSwap is { } previous && now > previous.At)
        {
            var seconds = (now - previous.At).TotalSeconds;
            swapIn = (long)(Math.Max(0, currentSwap.Item1 - previous.InPages) * Environment.SystemPageSize / seconds);
            swapOut = (long)(Math.Max(0, currentSwap.Item2 - previous.OutPages) * Environment.SystemPageSize / seconds);
        }
        _previousSwap = vm.Count == 2 ? currentSwap : null;

        return new HostTelemetrySample(now, cpu, ParseDouble(loads.ElementAtOrDefault(0)), ParseDouble(loads.ElementAtOrDefault(1)), ParseDouble(loads.ElementAtOrDefault(2)),
            total > 0 && available is >= 0 && available <= total ? total - available : null,
            total > 0 ? total : null,
            swapIn, swapOut, steal, ioWait, stat.Length == 0 ? 0 : ParseLinuxHostCpuCount(stat), activeSlots,
            SwapUsedBytes: swapUsed is >= 0 ? swapUsed : null, SwapTotalBytes: swapTotal);
    }

    private HostTelemetrySample SampleWindows(DateTime now, int activeSlots)
    {
        double? cpu = null;
        var current = ReadWindowsCpu();
        (cpu, _, _) = Percentages(_previousCpu, current);
        _previousCpu = current;

        long? used = null, total = null;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            total = (long)memory.TotalPhysical;
            used = total - (long)memory.AvailablePhysical;
        }
        return new HostTelemetrySample(now, cpu, null, null, null, used, total, null, null, null, null,
            Environment.ProcessorCount, activeSlots);
    }

    private string[] ProcLines(string file)
    {
        try { return File.ReadAllLines(Path.Combine(_proc, file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static CpuCounters ReadLinuxCpu(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new CpuCounters(fields.Aggregate(0UL, (sum, value) => sum + value), fields.ElementAtOrDefault(3) + fields.ElementAtOrDefault(4),
            fields.ElementAtOrDefault(7), fields.ElementAtOrDefault(4));
    }

    // /proc/loadavg counts runnable work across the host. ProcessorCount may
    // be reduced by this process's cgroup quota, so it cannot normalize loadavg.
    internal static int ParseLinuxHostCpuCount(IEnumerable<string> lines)
        => Math.Max(1, lines.Count(line =>
            line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal)
            && char.IsAsciiDigit(line[3])));

    private static CpuCounters? ReadWindowsCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var idleTicks = idle.Value; var kernelTicks = kernel.Value; var userTicks = user.Value;
        return new CpuCounters(kernelTicks + userTicks, idleTicks, 0, 0);
    }

    private static (double? Cpu, double? Steal, double? IoWait) Percentages(CpuCounters? previous, CpuCounters? current)
    {
        if (previous is null || current is null || current.Total <= previous.Total) return (null, null, null);
        var delta = current.Total - previous.Total;
        double Pct(ulong value, ulong before) => Math.Round((value - before) * 100d / delta, 2);
        return (Math.Round(100 - Pct(current.Idle, previous.Idle), 2), Pct(current.Steal, previous.Steal), Pct(current.IoWait, previous.IoWait));
    }

    private static long ParseKb(string value) => long.Parse(value.Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024;
    private static double? ParseDouble(string? value) =>
        double.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static HostTelemetrySample WithConnectivity(
        HostTelemetrySample sample,
        TaskServerConnectivitySnapshot? connectivity)
        => connectivity is null
            ? sample
            : sample with
            {
                TaskServerConnectionStatus = connectivity.Status,
                TaskServerConnectionObservedAt = connectivity.ObservedAt,
                TaskServerConnectionFailureStartedAt = connectivity.FailureStartedAt,
                TaskServerConnectionConsecutiveFailures = connectivity.ConsecutiveFailures,
                TaskServerConnectionEscalatedAt = connectivity.EscalatedAt,
                TaskServerConnectionLastError = connectivity.LastError,
                TaskServerConnectionLastRecoveredAt = connectivity.LastRecoveredAt,
            };

    private sealed record CpuCounters(ulong Total, ulong Idle, ulong Steal, ulong IoWait);

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
