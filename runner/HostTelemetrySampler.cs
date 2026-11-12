using System.Globalization;
using System.Runtime.InteropServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Stateful, dependency-free host sampler. Linux reads /proc; Windows uses kernel counters.</summary>
public sealed class HostTelemetrySampler
{
    private readonly RunnerOptions? _options;
    public HostTelemetrySampler(RunnerOptions? options = null) => _options = options;
    private CpuCounters? _previousCpu;
    private (long InPages, long OutPages, DateTime At)? _previousSwap;
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
        var filesystems = HostFilesystemProbe.Measure(paths);
        if (HostDiskPressurePolicy.Decide(filesystems, new HostDiskPressureThresholds()).Level != HostDiskPressureLevel.Healthy)
            filesystems = HostFilesystemProbe.Measure(paths,
                [("runner worktrees", _options?.WorkDir ?? Environment.CurrentDirectory),
                 ("logs", _options?.StateDir ?? Environment.CurrentDirectory),
                 ("result archives", _options?.SalvageDir ?? Environment.CurrentDirectory)]);
        return WithConnectivity(sample with { Filesystems = filesystems }, connectivity);
    }

    private HostTelemetrySample SampleLinux(DateTime now, int activeSlots)
    {
        var current = ReadLinuxCpu();
        var (cpu, steal, ioWait) = Percentages(_previousCpu, current);
        _previousCpu = current;

        var loads = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var memory = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => ParseKb(parts[1]), StringComparer.Ordinal);
        var total = memory.GetValueOrDefault("MemTotal");
        var available = memory.GetValueOrDefault("MemAvailable", memory.GetValueOrDefault("MemFree"));

        var vm = File.ReadLines("/proc/vmstat")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && (parts[0] == "pswpin" || parts[0] == "pswpout"))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
        var currentSwap = (vm.GetValueOrDefault("pswpin"), vm.GetValueOrDefault("pswpout"), now);
        long? swapIn = null, swapOut = null;
        if (_previousSwap is { } previous && now > previous.At)
        {
            var seconds = (now - previous.At).TotalSeconds;
            swapIn = (long)(Math.Max(0, currentSwap.Item1 - previous.InPages) * Environment.SystemPageSize / seconds);
            swapOut = (long)(Math.Max(0, currentSwap.Item2 - previous.OutPages) * Environment.SystemPageSize / seconds);
        }
        _previousSwap = currentSwap;

        return new HostTelemetrySample(now, cpu, ParseDouble(loads[0]), ParseDouble(loads[1]), ParseDouble(loads[2]),
            Math.Max(0, total - available), total, swapIn, swapOut, steal, ioWait, Environment.ProcessorCount, activeSlots);
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

    private static CpuCounters ReadLinuxCpu()
    {
        var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new CpuCounters(fields.Aggregate(0UL, (sum, value) => sum + value), fields.ElementAtOrDefault(3) + fields.ElementAtOrDefault(4),
            fields.ElementAtOrDefault(7), fields.ElementAtOrDefault(4));
    }

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
    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

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
