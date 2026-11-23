using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Local admission for claims that do not carry a telemetry poll.</summary>
internal sealed class RunnerDiskAdmission(RunnerOptions options,
    HostDiskPressureThresholds? thresholds = null, IHostFilesystemProbe? probe = null,
    bool initiallyCritical = false)
{
    // AGT-3866: a test process pins the default; production measures the host.
    private readonly IHostFilesystemProbe _probe = probe ?? HostFilesystemProbes.Default;

    private bool _critical = initiallyCritical;
    internal static async Task WaitForRecoveryAsync(RunnerOptions options, Action<string> log, CancellationToken ct)
    {
        var admission = new RunnerDiskAdmission(options, initiallyCritical: true);
        var announced = false;
        while (admission.Observe() is { Level: HostDiskPressureLevel.Critical } disk)
        {
            if (!announced) log($"DiskFull recovery waiting on {options.Hostname}: {disk.Reason}");
            announced = true;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), ct);
        }
        if (announced) log("DiskFull recovery: disk space is above warning; retry can resume.");
    }

    public HostDiskPressureDecision Observe()
    {
        var paths = new List<(string Purpose, string Path)>
        {
            ("runner work", options.WorkDir), ("runner state", options.StateDir),
            ("review work", options.ReviewWorkDir),
        };
        if (!string.IsNullOrWhiteSpace(options.SalvageDir)) paths.Add(("salvage", options.SalvageDir));
        foreach (var path in new[] { "/var/lib/docker", "/var/lib/containers" }.Where(Directory.Exists))
            paths.Add(("container storage", path));
        var limit = thresholds ?? new HostDiskPressureThresholds();
        var disks = _probe.Measure(paths);
        var decision = HostDiskPressurePolicy.Decide(disks, limit, _critical);
        _critical = decision.Level == HostDiskPressureLevel.Critical;
        return decision;
    }
}
