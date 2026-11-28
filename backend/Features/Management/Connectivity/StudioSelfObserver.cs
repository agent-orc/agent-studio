using System.Globalization;
using AgentStudio.Clients;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>
/// What the API observes about the machine it runs on (AGT-3954): the kind of
/// place, the processor, memory, load and network of that machine, the file
/// systems <see cref="ApiHostDiskService"/> already measures, and whether
/// requests arrive through a forwarding proxy. It reads a few small
/// <c>/proc</c> files per sample and starts no process.
/// </summary>
public sealed class StudioSelfObserver
{
    public const string MachineIdKey = "Structure:MachineId";
    public const string MachineNameKey = "Structure:MachineName";
    public const string MachineKindKey = "Structure:MachineKind";
    public const string DefaultMachineId = "studio-server";

    private readonly IConfiguration _configuration;
    private readonly StableReleaseIdentity? _release;
    private readonly ApiHostDiskService? _disks;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _read;
    private readonly Lazy<HostPlacementDto?> _placement;
    private readonly object _gate = new();
    private (ulong Total, ulong Idle)? _previousCpu;
    private (HostNetworkCounters Counters, DateTime At)? _previousNetwork;
    private long _proxiedTicks;
    private long _requests;
    private int _inFlight;
    private (DateTime At, long Count)? _lastRequests;

    public void FinishRequest() => Interlocked.Decrement(ref _inFlight);

    public (double? Rate, int InFlight) RequestLoad(DateTime now)
    {
        lock (_gate)
        {
            var count = Interlocked.Read(ref _requests);
            double? rate = _lastRequests is { } previous && now > previous.At
                ? (count - previous.Count) / (now - previous.At).TotalSeconds : null;
            _lastRequests = (now, count);
            return (rate, Volatile.Read(ref _inFlight));
        }
    }

    public StudioSelfObserver(IConfiguration configuration, TimeProvider clock, StableReleaseIdentity? release = null,
        ApiHostDiskService? disks = null, Func<string, string?>? read = null,
        Func<HostPlacementDto?>? placement = null)
    {
        _configuration = configuration;
        _clock = clock;
        _release = release;
        _disks = disks;
        _read = read ?? ReadProc;
        _placement = new(placement ?? (() => OperatingSystem.IsLinux() ? HostPlacementProbe.DetectLocal() : null));
    }

    internal string? KernelId => _placement.Value?.KernelId;

    public string MachineId => _configuration[MachineIdKey]?.Trim() is { Length: > 0 } id ? id : DefaultMachineId;

    /// <summary>Called once per request before forwarded headers are consumed. Two header lookups.</summary>
    public void ObserveRequest(IHeaderDictionary headers)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _inFlight);
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded"))
            Interlocked.Exchange(ref _proxiedTicks, _clock.GetUtcNow().UtcTicks);
    }

    public StudioSelfObservation Observe()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var kind = _configuration[MachineKindKey]?.Trim() is { Length: > 0 } configured ? configured : null;
        var proxied = Interlocked.Read(ref _proxiedTicks);
        return new(MachineId, _configuration[MachineNameKey]?.Trim() is { Length: > 0 } name ? name : "Studio server",
            kind, _placement.Value, Sample(now), _release?.Version, now,
            proxied > 0 ? new DateTime(proxied, DateTimeKind.Utc) : null);
    }

    private StructurePerformanceFacts? Sample(DateTime now)
    {
        lock (_gate)
        {
            double? cpu = null;
            int? cores = null;
            if (_read("/proc/stat") is { } stat)
            {
                var lines = stat.Split('\n');
                var fields = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                    .Select(value => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0UL)
                    .ToArray();
                if (fields.Length >= 5)
                {
                    var current = (Total: fields.Aggregate(0UL, (sum, value) => sum + value), Idle: fields[3] + fields[4]);
                    if (_previousCpu is { } previous && current.Total > previous.Total)
                        cpu = Math.Round(100 - (current.Idle - previous.Idle) * 100d / (current.Total - previous.Total), 2);
                    _previousCpu = current;
                }
                var counted = lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal)
                    && char.IsAsciiDigit(line[3]));
                if (counted > 0) cores = counted;
            }
            double? Load(string[] parts, int position) => parts.Length > position
                && double.TryParse(parts[position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            var loads = _read("/proc/loadavg")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            long? total = null, used = null;
            if (_read("/proc/meminfo") is { } meminfo)
            {
                long? Kb(string key) => meminfo.Split('\n').FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.Ordinal))
                    is { } line && long.TryParse(line[(key.Length + 1)..].Trim().Split(' ')[0], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var value) ? value * 1024 : null;
                total = Kb("MemTotal");
                if (total is not null && (Kb("MemAvailable") ?? Kb("MemFree")) is { } available) used = Math.Max(0, total.Value - available);
            }
            (long Receive, long Transmit)? rate = null;
            if (_read("/proc/net/dev") is { } network && HostNetworkCounters.Parse(network.Split('\n')) is { } counters)
            {
                if (_previousNetwork is { } previous) rate = HostNetworkCounters.Rate(previous.Counters, previous.At, counters, now);
                _previousNetwork = (counters, now);
            }
            var disks = _disks?.Latest?.Where(item => item.TotalBytes > 0).Select(item => new StructureDiskFact(item.Path,
                item.Purpose, Math.Max(0, item.TotalBytes - item.FreeBytes), item.FreeBytes, item.TotalBytes)).ToArray();
            if (cores is null && total is null && loads.Length == 0 && disks is not { Length: > 0 }) return null;
            return new(now, cpu, cores ?? Environment.ProcessorCount, Load(loads, 0), Load(loads, 1), Load(loads, 2),
                used, total, disks is { Length: > 0 } ? disks : null, rate?.Receive, rate?.Transmit);
        }
    }

    private static string? ReadProc(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
