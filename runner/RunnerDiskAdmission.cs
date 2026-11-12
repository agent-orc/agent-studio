using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Local admission for claims that do not carry a telemetry poll.</summary>
internal sealed class RunnerDiskAdmission(RunnerOptions options,
    HostDiskPressureThresholds? thresholds = null)
{
    private bool _critical;
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
        var disks = HostFilesystemProbe.Measure(paths);
        var decision = HostDiskPressurePolicy.Decide(disks, limit, _critical);
        _critical = decision.Level == HostDiskPressureLevel.Critical;
        return decision;
    }
}
