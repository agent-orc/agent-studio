using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// This test process loads the backend and the runner; their disk admission
/// reads the pinned probe, never the disk of the machine that runs the suite
/// (AGT-3866).
/// </summary>
[Trait("Category", "Guard")]
public sealed class DiskProbeIsolationGuardTests
{
    [Fact]
    public void This_test_process_pins_the_default_disk_probe()
        => Assert.IsNotType<SystemHostFilesystemProbe>(HostFilesystemProbes.Default);
}
