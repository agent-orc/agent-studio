using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Regression coverage for the AGT-2750 incident: a <c>PrivateTmp=true</c>
/// restart unmounts <c>/tmp</c> from underneath a detached worker that
/// <c>KillMode=process</c> deliberately left running. The captured evidence
/// (agent-runner-01, 2026-09-07) showed the worker's own <c>/tmp</c> mount
/// pointing at a deleted bind-mount source in <c>/proc/&lt;pid&gt;/mountinfo</c>.
/// </summary>
public sealed class DetachedWorkerTmpMountGuardTests
{
    [Fact]
    public void Deleted_tmp_bind_mount_source_is_detected()
    {
        var mountInfo = new[]
        {
            "22 25 0:20 / /proc rw,nosuid,nodev,noexec,relatime shared:12 - proc proc rw",
            "656 654 0:59 " +
            "/tmp/systemd-private-dedae28ed8ee49ab9d9a88405a36b280-agent-runner-review.service-WhdhIS/tmp//deleted " +
            "/tmp rw,nosuid,nodev shared:326 - tmpfs tmpfs rw,size=1048576k",
        };

        Assert.True(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(mountInfo, out var detail));
        Assert.Contains("deleted", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Healthy_tmp_mount_is_not_flagged()
    {
        var mountInfo = new[]
        {
            "22 25 0:20 / /proc rw,nosuid,nodev,noexec,relatime shared:12 - proc proc rw",
            "656 654 0:59 " +
            "/tmp/systemd-private-dedae28ed8ee49ab9d9a88405a36b280-agent-runner-review.service-WhdhIS/tmp " +
            "/tmp rw,nosuid,nodev shared:326 - tmpfs tmpfs rw,size=1048576k",
        };

        Assert.False(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(mountInfo, out var detail));
        Assert.Equal(string.Empty, detail);
    }

    [Fact]
    public void A_deleted_mount_elsewhere_than_tmp_is_not_flagged()
    {
        // "deleted" belongs to an unrelated mount point; the review worker's
        // own /tmp is untouched, so this must not misclassify a healthy worker.
        var mountInfo = new[]
        {
            "22 25 0:20 /some/unrelated//deleted /var/lib/foo rw shared:12 - tmpfs tmpfs rw",
            "656 654 0:59 /tmp-real-source /tmp rw,nosuid,nodev shared:326 - tmpfs tmpfs rw",
        };

        Assert.False(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(mountInfo, out _));
    }

    [Fact]
    public void No_mount_lines_is_not_flagged()
    {
        Assert.False(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(Array.Empty<string>(), out var detail));
        Assert.Equal(string.Empty, detail);
    }

    [Fact]
    public void Process_backed_overload_returns_false_off_linux_without_reading_proc()
    {
        // The process-id overload gates on OperatingSystem.IsLinux() before
        // touching the filesystem, so a bogus pid never throws on Windows/macOS.
        if (OperatingSystem.IsLinux()) return;
        Assert.False(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(int.MaxValue, out var detail));
        Assert.Equal(string.Empty, detail);
    }
}
