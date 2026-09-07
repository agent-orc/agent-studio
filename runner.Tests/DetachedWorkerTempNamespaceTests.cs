using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Regression cover for AGT-2750: a detached worker that survived a daemon
/// restart with the unit's private /tmp unmounted must not be re-adopted.
/// </summary>
public sealed class DetachedWorkerTempNamespaceTests
{
    // Captured on agent-runner-01 from /proc/2593715/mountinfo while a review
    // worker ran dotnet test after agent-runner-review.service had restarted.
    private const string DeletedPrivateTmp =
        "24 30 0:22 / /proc rw,nosuid,nodev,noexec,relatime shared:14 - proc proc rw\n"
        + "512 30 0:31 /tmp/systemd-private-b6b1e18b1a8f4a2f9a3c50e2f2f0d1c7"
        + "-agent-runner-review.service-WhdhIS/tmp//deleted /tmp "
        + "rw,nosuid,nodev,relatime shared:298 - tmpfs tmpfs rw,size=8192000k\n"
        + "513 30 0:31 /var/tmp/systemd-private-b6b1e18b1a8f4a2f9a3c50e2f2f0d1c7"
        + "-agent-runner-review.service-WhdhIS/tmp /var/tmp "
        + "rw,nosuid,nodev,relatime shared:299 - tmpfs tmpfs rw\n";

    private const string HealthySharedTmp =
        "24 30 0:22 / /proc rw,nosuid,nodev,noexec,relatime shared:14 - proc proc rw\n"
        + "31 30 0:25 / /tmp rw,nosuid,nodev shared:16 - tmpfs tmpfs rw,size=8192000k\n";

    [Fact]
    public void Deleted_private_tmp_mount_is_detected()
        => Assert.True(DetachedWorkerTempNamespace.HasDeletedTempMount(DeletedPrivateTmp));

    [Fact]
    public void Shared_host_tmp_mount_is_adoptable()
        => Assert.False(DetachedWorkerTempNamespace.HasDeletedTempMount(HealthySharedTmp));

    [Fact]
    public void A_deleted_mount_that_is_not_tmp_is_ignored()
    {
        const string deletedElsewhere =
            "77 30 0:44 /srv/build//deleted /srv/build rw,relatime shared:9 - tmpfs tmpfs rw\n"
            + "31 30 0:25 / /tmp rw,nosuid,nodev shared:16 - tmpfs tmpfs rw\n";

        Assert.False(DetachedWorkerTempNamespace.HasDeletedTempMount(deletedElsewhere));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("truncated line")]
    public void Unreadable_mount_information_never_rejects_a_worker(string? mountInfo)
        => Assert.False(DetachedWorkerTempNamespace.HasDeletedTempMount(mountInfo));

    /// <summary>
    /// A healthy worker must survive the check untouched. This also proves the
    /// real <c>/proc</c> read path, since the probe is a live process.
    /// </summary>
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void A_worker_on_a_shared_tmp_is_neither_rejected_nor_terminated()
    {
        PlatformGate.LinuxOnly("the proof reads /proc/<pid>/mountinfo");
        using var probe = Process.Start(new ProcessStartInfo(
            PosixShell.RequirePath(),
            "-c \"sleep 30\"")
        {
            RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.False(DetachedWorkerTempNamespace.IsLost(probe, out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.False(probe.HasExited);
        }
        finally
        {
            probe.Kill(entireProcessTree: true);
        }
    }
}
