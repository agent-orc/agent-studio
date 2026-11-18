using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// AGT-3954: detects once per process where this host runs (container, VM or
/// physical machine) so the operations structure can nest it without a
/// manifest. The kind cannot change while the process lives, so the result is
/// cached; the capability advertisement only copies it.
/// </summary>
public static class HostMachineProbe
{
    private static Task<HostMachineDto>? _current;
    private static readonly object Gate = new();

    /// <summary>The detection runs once, bounded by its own deadlines; a caller's cancellation only stops its wait.</summary>
    public static Task<HostMachineDto> CurrentAsync(RunnerOptions options, CancellationToken ct)
    {
        lock (Gate) return (_current ??= DetectAsync(options, CancellationToken.None)).WaitAsync(ct);
    }

    private static async Task<HostMachineDto> DetectAsync(RunnerOptions options, CancellationToken ct)
    {
        var evidence = new HostMachineEvidence(
            ConfiguredKind: options.MachineKind,
            ConfiguredContainerName: options.ContainerName,
            ConfiguredParentMachineId: options.ParentMachineId,
            Hostname: Environment.MachineName);
        if (!OperatingSystem.IsLinux()) return HostMachineClassifier.Classify(evidence);
        evidence = evidence with
        {
            DockerEnvFile = File.Exists("/.dockerenv"),
            ContainerEnvFile = Read("/run/.containerenv"),
            ContainerVariable = Environment.GetEnvironmentVariable("container"),
            InitCgroup = Read("/proc/1/cgroup"),
            DmiVendor = Read("/sys/class/dmi/id/sys_vendor")?.Trim(),
            DmiProduct = Read("/sys/class/dmi/id/product_name")?.Trim(),
            CpuHypervisorFlag = Read("/proc/cpuinfo")?.Split('\n')
                .Any(line => line.StartsWith("flags", StringComparison.Ordinal)
                             && line.Contains(" hypervisor", StringComparison.Ordinal)) == true,
            BootId = Read("/proc/sys/kernel/random/boot_id")?.Trim(),
            DetectedVirtualization = await DetectVirtAsync("--vm", ct),
            DetectedContainer = await DetectVirtAsync("--container", ct),
        };
        return HostMachineClassifier.Classify(evidence);
    }

    /// <summary><c>systemd-detect-virt</c> prints <c>none</c> and exits 1 when nothing is detected; a missing binary yields null.</summary>
    private static async Task<string?> DetectVirtAsync(string scope, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var result = await ProcessRunner.RunAsync("systemd-detect-virt", [scope], ct: deadline.Token);
            var value = result.StdOut.Trim();
            return value.Length is > 0 and < 64 ? value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                   || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { return null; }
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
