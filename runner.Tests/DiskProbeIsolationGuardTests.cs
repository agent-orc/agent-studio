using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The runner daemons' disk admission reads the pinned probe in this test
/// process, never the disk of the machine that runs the suite (AGT-3866).
/// </summary>
[Trait("Category", "Guard")]
public sealed class DiskProbeIsolationGuardTests
{
    [Fact]
    public void This_test_process_pins_the_default_disk_probe()
        => Assert.IsNotType<SystemHostFilesystemProbe>(HostFilesystemProbes.Default);
}

public sealed class RunnerDiskAdmissionTests
{
    private static RunnerOptions Options() => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = Path.Combine(Path.GetTempPath(), "disk-admission-work"),
        BaseBranch = "main",
        ClaudeCliBin = "codex",
    };

    [Fact]
    public void Default_admission_in_a_test_process_is_healthy_whatever_the_host_disk()
        => Assert.Equal(HostDiskPressureLevel.Healthy, new RunnerDiskAdmission(Options()).Observe().Level);

    [Fact]
    public void Admission_pauses_on_an_injected_critical_probe_until_warning_clears()
    {
        var probe = new SwitchableProbe { Current = new FixedHostFilesystemProbe(4L << 30, 246L << 30) };
        var admission = new RunnerDiskAdmission(Options(), probe: probe);

        var paused = admission.Observe();
        Assert.Equal(HostDiskPressureLevel.Critical, paused.Level);
        Assert.Contains("4.0 GiB free (1.6%)", paused.Reason);

        // 20 GiB is above critical but still below the 30 GiB warning level.
        probe.Current = new FixedHostFilesystemProbe(20L << 30, 246L << 30);
        Assert.Equal(HostDiskPressureLevel.Critical, admission.Observe().Level);
        probe.Current = FixedHostFilesystemProbe.Healthy;
        Assert.Equal(HostDiskPressureLevel.Healthy, admission.Observe().Level);
    }

    private sealed class SwitchableProbe : IHostFilesystemProbe
    {
        public IHostFilesystemProbe Current { get; set; } = FixedHostFilesystemProbe.Healthy;

        public IReadOnlyList<HostFilesystemUsageDto> Measure(
            IEnumerable<(string Purpose, string Path)> paths,
            IEnumerable<(string Name, string Path)>? ownedDirectories = null)
            => Current.Measure(paths, ownedDirectories);
    }
}
