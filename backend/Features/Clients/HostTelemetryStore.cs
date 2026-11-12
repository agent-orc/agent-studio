using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Clients;

public sealed record HostTelemetrySample(DateTime Timestamp, double? CpuPercent, double? Load1, double? Load5,
    double? Load15, long? MemoryUsedBytes, long? MemoryTotalBytes, long? SwapInBytesPerSecond,
    long? SwapOutBytesPerSecond, double? CpuStealPercent, double? IoWaitPercent, int CpuCores, int ActiveSlots,
    string TaskServerConnectionStatus = "unknown", DateTime? TaskServerConnectionObservedAt = null,
    DateTime? TaskServerConnectionFailureStartedAt = null, int TaskServerConnectionConsecutiveFailures = 0,
    DateTime? TaskServerConnectionEscalatedAt = null, string? TaskServerConnectionLastError = null,
    DateTime? TaskServerConnectionLastRecoveredAt = null,
    IReadOnlyList<AgentStudio.TaskServer.Contracts.HostFilesystemUsageDto>? Filesystems = null);

public sealed record HostTelemetryFinding(
    string Kind,
    string Label,
    DateTime Since,
    DateTime Until,
    int Occurrences,
    bool IsActive,
    string? Detail = null);
public sealed record HostTelemetryResponse(string ClientId, string Window, IReadOnlyList<HostTelemetrySample> Points,
    IReadOnlyList<HostTelemetryFinding> Findings);

/// <summary>Durable per-client telemetry series with 48h raw and 14d five-minute retention.</summary>
public sealed class HostTelemetryStore(IConfiguration config, ILogger<HostTelemetryStore> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _diskPaused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostDiskPressureLevel> _diskLevels = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private string Root => Path.Combine(config["TaskRepository"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"), "telemetry");

    public void Append(string clientId, HostTelemetrySample sample)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (sample.Timestamp == default || sample.Timestamp > now.AddMinutes(5)) return;
        lock (_gate)
        {
            var points = Read(clientId);
            if (points.Count > 0 && sample.Timestamp <= points[^1].Timestamp) return;
            points.Add(sample);
            points = Compact(points, now);
            var disk = DiskPressure(clientId, sample.Filesystems);
            if (_diskLevels.GetValueOrDefault(clientId) != disk.Level)
            {
                logger.LogWarning("host-disk-pressure-event clientId={ClientId} level={Level} reason={Reason}",
                    clientId, disk.Level, disk.Reason);
                _diskLevels[clientId] = disk.Level;
            }
            Directory.CreateDirectory(Root);
            var path = PathFor(clientId); var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(points, _json));
            File.Move(temporary, path, true);
            logger.LogDebug("host-telemetry-recorded clientId={ClientId} timestamp={Timestamp} points={PointCount}", clientId, sample.Timestamp, points.Count);
        }
    }

    public AgentStudio.TaskServer.Contracts.HostDiskPressureDecision DiskPressure(string clientId,
        IReadOnlyList<AgentStudio.TaskServer.Contracts.HostFilesystemUsageDto>? filesystems)
    {
        lock (_gate)
        {
            var thresholds = new AgentStudio.TaskServer.Contracts.HostDiskPressureThresholds(
                config.GetValue("DiskPressure:WarningFreeBytes", 30L * 1024 * 1024 * 1024),
                config.GetValue("DiskPressure:WarningFreePercent", 15d),
                config.GetValue("DiskPressure:CriticalFreeBytes", 8L * 1024 * 1024 * 1024),
                config.GetValue("DiskPressure:CriticalFreePercent", 5d));
            var previous = _diskPaused.GetValueOrDefault(clientId);
            if (filesystems is null && previous)
                return new(HostDiskPressureLevel.Critical,
                    "Disk readings are unavailable after a critical report; claims remain paused.", null);
            var decision = AgentStudio.TaskServer.Contracts.HostDiskPressurePolicy.Decide(
                filesystems, thresholds, previous);
            if (filesystems is { Count: > 0 }) _diskPaused[clientId] =
                decision.Level == AgentStudio.TaskServer.Contracts.HostDiskPressureLevel.Critical;
            return decision;
        }
    }

    public HostTelemetryResponse Query(string clientId, string window, DateTime? nowUtc = null)
    {
        var duration = window switch { "1h" => TimeSpan.FromHours(1), "6h" => TimeSpan.FromHours(6), "14d" => TimeSpan.FromDays(14), _ => TimeSpan.FromHours(48) };
        List<HostTelemetrySample> points;
        lock (_gate) points = Read(clientId).Where(point => point.Timestamp >=
            (nowUtc ?? _time.GetUtcNow().UtcDateTime) - duration).ToList();
        var findings = Findings(points).ToList();
        if (points.LastOrDefault() is { Filesystems: { Count: > 0 } filesystems } latest)
        {
            var disk = DiskPressure(clientId, filesystems);
            if (disk.Level != HostDiskPressureLevel.Healthy)
                findings.Add(new HostTelemetryFinding("disk-pressure",
                    disk.Level == HostDiskPressureLevel.Critical ? "Disk critical" : "Disk low",
                    latest.Timestamp, latest.Timestamp, 1, true, disk.Reason));
        }
        return new HostTelemetryResponse(clientId, window, points, findings);
    }

    private List<HostTelemetrySample> Read(string clientId)
    {
        var path = PathFor(clientId);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<HostTelemetrySample>>(File.ReadAllText(path), _json) ?? []; }
        catch (Exception ex) { logger.LogWarning(ex, "host-telemetry-read-failed clientId={ClientId} path={Path}", clientId, path); return []; }
    }

    private string PathFor(string clientId) => Path.Combine(Root, string.Concat(clientId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");

    internal static List<HostTelemetrySample> Compact(List<HostTelemetrySample> source, DateTime now)
    {
        var retention = now.AddDays(-14); var rawBoundary = now.AddHours(-48);
        var recent = source.Where(p => p.Timestamp >= rawBoundary).ToList();
        var historic = source.Where(p => p.Timestamp >= retention && p.Timestamp < rawBoundary)
            .GroupBy(p => new DateTime(p.Timestamp.Ticks - p.Timestamp.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc))
            .Select(group => Average(group.Key, group)).ToList();
        return historic.Concat(recent).OrderBy(p => p.Timestamp).ToList();
    }

    private static HostTelemetrySample Average(DateTime timestamp, IEnumerable<HostTelemetrySample> values)
    {
        var list = values.ToList();
        var latest = list.MaxBy(value => value.Timestamp)!;
        double? D(Func<HostTelemetrySample, double?> get) { var v = list.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : Math.Round(v.Average(), 2); }
        long? L(Func<HostTelemetrySample, long?> get) { var v = list.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : (long)v.Average(); }
        return new(timestamp, D(x => x.CpuPercent), D(x => x.Load1), D(x => x.Load5), D(x => x.Load15), L(x => x.MemoryUsedBytes),
            L(x => x.MemoryTotalBytes), L(x => x.SwapInBytesPerSecond), L(x => x.SwapOutBytesPerSecond), D(x => x.CpuStealPercent),
            D(x => x.IoWaitPercent), (int)Math.Round(list.Average(x => x.CpuCores)), (int)Math.Round(list.Average(x => x.ActiveSlots)),
            latest.TaskServerConnectionStatus, latest.TaskServerConnectionObservedAt,
            latest.TaskServerConnectionFailureStartedAt, latest.TaskServerConnectionConsecutiveFailures,
            latest.TaskServerConnectionEscalatedAt, latest.TaskServerConnectionLastError,
            latest.TaskServerConnectionLastRecoveredAt, latest.Filesystems);
    }

    internal static IReadOnlyList<HostTelemetryFinding> Findings(List<HostTelemetrySample> points)
    {
        if (points.Count < 3) return [];
        const double oversubscriptionLoadFactor = 1.5;
        const double displacementStealPercent = 5;
        const double displacementIoWaitPercent = 10;
        var findings = new List<HostTelemetryFinding>();
        Add("vm-throttled", "VM throttled", p => p.CpuStealPercent > displacementStealPercent);
        // Review work is cgroup-capped and may remain runnable without harming Coding.
        // Require both substantial load headroom consumption and a measured damage signal.
        Add("oversubscribed", "Oversubscribed", p =>
            p.Load1 > p.CpuCores * oversubscriptionLoadFactor
            && (p.CpuStealPercent > displacementStealPercent || p.IoWaitPercent > displacementIoWaitPercent));
        Add("memory-pressure", "Memory pressure", p => (p.SwapInBytesPerSecond ?? 0) + (p.SwapOutBytesPerSecond ?? 0) > 64 * 1024);
        return findings;

        void Add(string kind, string label, Func<HostTelemetrySample, bool> predicate)
        {
            const int minimumQualifyingSamples = 3;
            const int maximumBridgeGapSamples = 2;
            var phases = new List<(DateTime Since, DateTime Until, bool IsActive)>();
            DateTime? since = null;
            DateTime? until = null;
            var qualifyingSamples = 0;
            var gapSamples = 0;

            foreach (var point in points)
            {
                if (predicate(point))
                {
                    since ??= point.Timestamp;
                    until = point.Timestamp;
                    qualifyingSamples++;
                    gapSamples = 0;
                    continue;
                }

                if (since is null) continue;
                gapSamples++;
                if (gapSamples <= maximumBridgeGapSamples) continue;
                CompletePhase(isActive: false);
            }

            if (since is not null) CompletePhase(isActive: gapSamples <= maximumBridgeGapSamples);

            var ended = phases.Where(phase => !phase.IsActive).ToList();
            if (ended.Count > 0)
                findings.Add(new(kind, label, ended[0].Since, ended[^1].Until, ended.Count, IsActive: false));

            var active = phases.LastOrDefault(phase => phase.IsActive);
            if (active != default)
                findings.Add(new(kind, label, active.Since, active.Until, Occurrences: 1, IsActive: true));

            void CompletePhase(bool isActive)
            {
                if (qualifyingSamples >= minimumQualifyingSamples)
                    phases.Add((since!.Value, until!.Value, isActive));
                since = null;
                until = null;
                qualifyingSamples = 0;
                gapSamples = 0;
            }
        }
    }
}
