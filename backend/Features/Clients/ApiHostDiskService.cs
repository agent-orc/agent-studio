using AgentStudio.TaskServer.Contracts;
using AgentStudio.Pipeline;

namespace AgentStudio.Clients;

/// <summary>Samples the API host even when no Operations browser is open.</summary>
public sealed class ApiHostDiskService(IConfiguration configuration, HostTelemetryStore telemetry,
    BatchGateStore batches,
    ILogger<ApiHostDiskService> logger, IHostFilesystemProbe? probe = null, SshGateHostProbe? gateProbe = null) : BackgroundService
{
    // AGT-3866: lease admission reads this; a test host pins the probe.
    private readonly IHostFilesystemProbe _probe = probe ?? HostFilesystemProbes.Default;

    public HostDiskPressureDecision Admission()
        => telemetry.DiskPressure("api-host", Measure(includeConsumers: false));

    /// <summary>The file systems of the last sample; readers never trigger a measurement (AGT-3954).</summary>
    public IReadOnlyList<HostFilesystemUsageDto>? Latest { get; private set; }

    public object Snapshot(bool record = false)
    {
        var disks = Measure(includeConsumers: true);
        Latest = disks;
        var sample = new HostTelemetrySample(DateTime.UtcNow,
            null, null, null, null, null, null, null, null, null, null,
            Environment.ProcessorCount, 0, Filesystems: disks);
        if (record)
        {
            try { telemetry.Append("api-host", sample); }
            catch (IOException error) { logger.LogWarning(error, "api-host-disk-telemetry-write-failed"); }
        }
        var pressure = telemetry.DiskPressure("api-host", disks);
        return new { host = Environment.MachineName, observedAt = sample.Timestamp,
            filesystems = disks, level = pressure.Level.ToString().ToLowerInvariant(), pressure.Reason,
            remoteHosts = gateProbe?.LatestDisk is { } gate ? new[] { gate } : [] };
    }

    private IReadOnlyList<HostFilesystemUsageDto> Measure(bool includeConsumers)
    {
        var data = configuration["TaskRepository"]
            ?? Path.Combine(AppContext.BaseDirectory, "workspace");
        var gateEvidence = batches.StorageRoot;
        var paths = new List<(string Purpose, string Path)>
            { ("instance data", data), ("gate evidence", gateEvidence) };
        var work = configuration["DiskPressure:RunnerWorkRoot"];
        if (!string.IsNullOrWhiteSpace(work)) paths.Add(("runner work", work));
        var container = configuration["DiskPressure:ContainerStoragePath"];
        if (!string.IsNullOrWhiteSpace(container)) paths.Add(("container storage", container));
        else foreach (var candidate in new[] { "/var/lib/docker", "/var/lib/containers" }.Where(Directory.Exists))
            paths.Add(("container storage", candidate));
        var disks = _probe.Measure(paths);
        var thresholds = new HostDiskPressureThresholds(
            configuration.GetValue("DiskPressure:WarningFreeBytes", 20L * 1024 * 1024 * 1024),
            configuration.GetValue("DiskPressure:WarningFreePercent", 15d),
            configuration.GetValue("DiskPressure:CriticalFreeBytes", 8L * 1024 * 1024 * 1024),
            configuration.GetValue("DiskPressure:CriticalFreePercent", 5d));
        if (includeConsumers && HostDiskPressurePolicy.Decide(disks, thresholds).Level
            != HostDiskPressureLevel.Healthy)
            disks = _probe.Measure(paths,
                [("runner worktrees", work ?? ""), ("gate evidence", gateEvidence),
                 ("logs", Path.Combine(data, "logs")), ("result archives", Path.Combine(data, "tasks"))]);
        return disks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Snapshot(record: true); if (gateProbe is not null) await gateProbe.ReadDiskAsync(stoppingToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "api-host-disk-sample-failed"); }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { Snapshot(record: true); if (gateProbe is not null) await gateProbe.ReadDiskAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "api-host-disk-sample-failed"); }
        }
    }
}
