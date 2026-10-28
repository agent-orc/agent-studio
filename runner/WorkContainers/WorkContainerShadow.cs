using System.Text.Json;

namespace AgentRunner.WorkContainers;

/// <summary>
/// Shadow evidence for the daemon worker path. In shadow or canary mode every
/// detached coding or review worker records which executor the selection
/// policy would choose and whether its provisional grant would be admitted.
/// The detached worker itself always keeps the process and cgroup executor in
/// this slice: provider credentials reach a container only as tmpfs files,
/// which DP-S5 delivers. Recording never throws into the launch path.
/// </summary>
public static class WorkContainerShadow
{
    public const string FileName = "executor-shadow.json";

    /// <summary>DP-S1 records two half-cores as the provisional grant of one claim.</summary>
    public const int ProvisionalGrantHalfCores = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record ShadowRecord(
        string Role,
        string Mode,
        string SelectedExecutor,
        string SelectionReason,
        string? Image,
        bool Admitted,
        string AdmissionReason,
        WorkContainerLimits? Limits,
        string WorkerExecutor,
        DateTime RecordedAtUtc);

    public static void Record(
        string workerDirectory, string role, string defaultStateDirectory, Action<string>? log,
        Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        try
        {
            if (string.IsNullOrWhiteSpace(read(WorkContainerSettings.ModeVariable))) return;
            var memory = HostMemory.Read();
            var settings = WorkContainerSettings.FromEnvironment(read, WorkContainerSettings.HostCores(), memory.TotalBytes,
                Path.Combine(defaultStateDirectory, "work-containers"));
            if (settings.Mode == WorkContainerMode.Off) return;
            string? image = null;
            try { image = RoleImageManifest.TryLoad(settings.ImageManifestPath)?.Resolve(role); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                log?.Invoke($"[work-container] shadow: role image manifest unreadable: {ex.Message}");
            }
            var selection = WorkExecutorSelectionPolicy.Select(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(),
                settings.Mode, RuntimeSocketPresent(settings.Runtime), RoleImageManifest.IsPinned(image));
            var admission = WorkContainerAdmissionPolicy.Decide(
                new WorkContainerGrant(1, 1, ProvisionalGrantHalfCores),
                new WorkContainerHostBudget(0, memory.AvailableBytes, memory.TotalBytes),
                settings);
            var record = new ShadowRecord(role, settings.Mode.ToString().ToLowerInvariant(), selection.Kind.ToString().ToLowerInvariant(),
                selection.Reason, image, admission.Admitted, admission.Reason, admission.Limits,
                "process", DateTime.UtcNow);
            Directory.CreateDirectory(workerDirectory);
            File.WriteAllText(Path.Combine(workerDirectory, FileName), JsonSerializer.Serialize(record, Json));
            log?.Invoke($"[work-container] shadow role={role} selected={record.SelectedExecutor} admitted={admission.Admitted} worker=process");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[work-container] shadow skipped: {ex.Message}");
        }
    }

    // A cheap hint, not a probe: shadow recording must not spawn a process per
    // launch. Podman is daemonless, so its CLI on PATH is the available runtime.
    private static bool RuntimeSocketPresent(string runtime)
        => runtime == "podman"
            ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, "podman")))
            : File.Exists("/var/run/docker.sock");
}
