using AgentStudio.Clients;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

/// <summary>Projects the host disk monitor's current pressure into Needs you.</summary>
public sealed class OperatorHostDiskFactSource(V1ReviewExecutorRegistry registry, HostTelemetryStore telemetry, AgentStudio.Pipeline.SshGateHostProbe? gateProbe = null)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.HostDiskPressure];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
        => registry.ListCapabilitySnapshots().Select(host => host.RunnerId).Append("api-host")
            .Concat(gateProbe?.LatestDisk is { } gate ? [gate.Id] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(id => Project(id, telemetry.Query(id, "1h"), nowUtc)).ToArray();

    internal IReadOnlyList<OperatorDecisionFact> Project(string id, HostTelemetryResponse series, DateTime nowUtc)
    {
        var latest = series.Points.LastOrDefault();
        if (latest is null || nowUtc - latest.Timestamp > TimeSpan.FromMinutes(15)) return [];
        var pressure = telemetry.DiskPressure(id, latest.Filesystems);
        if (pressure.Level == HostDiskPressureLevel.Healthy) return [];
        return [new(OperatorDecisionTypes.HostDiskPressure,
            $"{OperatorDecisionTypes.HostDiskPressure}|{id}", null, null,
            $"Host {id}: {pressure.Reason} "
                + (pressure.Level == HostDiskPressureLevel.Critical ? "New work is paused. " : "")
                + "Remove unused review images and inspect review workspace retention.",
            latest.Timestamp, [new("Host", "/workspace/settings/environment")],
            Title: $"Host {id} disk space low")];
    }
}
