using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>Last structure observations for a restarted API or a disconnected workspace copy.
/// This cache belongs to the read model. Dispatch and admission never consult it.</summary>
public sealed class StructureReportCache(IConfiguration configuration, ILogger<StructureReportCache> logger)
{
    private readonly Dictionary<string, RunnerCapabilitySnapshotDto> _last = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private IReadOnlyList<RunnerCapabilitySnapshotDto> _lastLive = [];
    private bool _loaded;
    private bool _dirty;

    internal IReadOnlyList<RunnerCapabilitySnapshotDto> Merge(IReadOnlyList<RunnerCapabilitySnapshotDto>? live, DateTime now, string studioId, string? studioKernel)
    {
        // Null means the authority was unavailable. Keep the last successful read (including
        // older agents without structure telemetry) only within the same retention window.
        var cutoff = now - Retention;
        _lastLive = live ?? _lastLive.Where(host => host.LastSeenAt >= cutoff).ToArray();
        if (configuration["TaskRepository"] is not { Length: > 0 } workspace) return _lastLive;
        var directory = Path.Combine(workspace, "telemetry", "structure");
        var file = Path.Combine(directory, "last-reports.json");
        // Called under the structure sampler's gate, including the first on-demand read.
        if (!_loaded)
        {
            try
            {
                if (File.Exists(file))
                    foreach (var stored in JsonSerializer.Deserialize<RunnerCapabilitySnapshotDto[]>(File.ReadAllText(file), _json) ?? [])
                        _last[stored.RunnerId] = stored;
                _loaded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { logger.LogWarning(ex, "structure-host-cache-read-failed"); _loaded = true; }
        }
        foreach (var host in live ?? [])
        {
            if (host.LastSeenAt < cutoff || host.Telemetry?.Structure is not { } reading ||
                _last.GetValueOrDefault(host.RunnerId)?.Telemetry?.Structure?.ObservedAt >= reading.ObservedAt) continue;
            var place = host.Telemetry.Placement;
            if (place is { ParentMachineId: null, KernelId: { } kernel } && kernel == studioKernel && host.HostId != studioId)
                place = place with { ParentMachineId = studioId };
            // Persist no capability evidence excerpts, credential metadata, tool paths, or failure messages.
            _last[host.RunnerId] = new(host.RunnerId, reading.DisplayName, host.HostId, host.InstanceId,
                host.RunnerVersion, host.ProtocolVersion, host.Status, host.RegisteredAt, host.LastSeenAt,
                host.HostAdmission with { AutomaticDrainReason = null, OperatorDrainReason = null }, [],
                host.Telemetry with { Placement = place, TaskServerConnectionLastError = null, ClaimLoadGate = null, OneShotClaimRoute = null,
                    Containers = null, SalvageStore = null }, RoleMaxParallelism: host.RoleMaxParallelism,
                EffectiveMaxParallelism: host.EffectiveMaxParallelism, Release: host.Release);
            _dirty = true;
        }
        foreach (var id in _last.Where(p => p.Value.LastSeenAt < cutoff).Select(p => p.Key).ToArray())
            _dirty |= _last.Remove(id);
        if (_dirty)
        {
            var temporary = file + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(_last.Values, _json));
                File.Move(temporary, file, true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "structure-host-cache-write-failed"); }
        }
        var ids = _lastLive.Select(host => host.RunnerId).ToHashSet(StringComparer.Ordinal);
        return [.. _lastLive, .. _last.Values.Where(host => !ids.Contains(host.RunnerId))];
    }
}
