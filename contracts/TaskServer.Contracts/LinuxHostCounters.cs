using System.Globalization;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Kernel-wide counters read from <c>/proc</c>. A container reads the counters of the machine that runs it.</summary>
public sealed record LinuxHostCounterReading(
    DateTime At,
    ulong CpuTotal,
    ulong CpuIdle,
    int CpuCores,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    (long Receive, long Transmit)? Network);

/// <summary>
/// AGT-3954: the Studio server samples its own machine with the same
/// counters an agent host reports, so the structure shows its CPU, memory and
/// load next to the hosts. Pure parsers plus one bounded read.
/// </summary>
public static class LinuxHostCounters
{
    public static LinuxHostCounterReading? Read(DateTime at)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var stat = File.ReadAllLines("/proc/stat");
            return Parse(at, stat, File.ReadAllText("/proc/loadavg"), File.ReadAllLines("/proc/meminfo"),
                File.Exists("/proc/net/dev") ? File.ReadAllLines("/proc/net/dev") : []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    public static LinuxHostCounterReading Parse(DateTime at, IReadOnlyList<string> stat, string loadavg,
        IEnumerable<string> meminfo, IEnumerable<string> netDev)
    {
        var cpu = stat.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal))?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray() ?? [];
        var cores = Math.Max(1, stat.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal)
                                                   && char.IsAsciiDigit(line[3])));
        var loads = loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double? Load(int index) => loads.Length > index
            && double.TryParse(loads[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        var memory = meminfo.Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Kilobytes(group.First()[1]), StringComparer.Ordinal);
        return new(at, cpu.Aggregate(0UL, (sum, value) => sum + value),
            cpu.ElementAtOrDefault(3) + cpu.ElementAtOrDefault(4), cores, Load(0), Load(1), Load(2),
            memory.GetValueOrDefault("MemTotal"),
            memory.GetValueOrDefault("MemAvailable") ?? memory.GetValueOrDefault("MemFree"),
            HostNetworkCounters.Parse(netDev));
    }

    /// <summary>Busy share of all CPUs between two readings; null without a previous reading.</summary>
    public static double? CpuPercent(LinuxHostCounterReading? previous, LinuxHostCounterReading current)
    {
        if (previous is null || current.CpuTotal <= previous.CpuTotal) return null;
        var total = current.CpuTotal - previous.CpuTotal;
        var idle = current.CpuIdle >= previous.CpuIdle ? current.CpuIdle - previous.CpuIdle : 0;
        return Math.Round(100d - idle * 100d / total, 2);
    }

    private static long? Kilobytes(string value)
        => long.TryParse(value.Trim().Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
            ? kb * 1024 : null;
}
