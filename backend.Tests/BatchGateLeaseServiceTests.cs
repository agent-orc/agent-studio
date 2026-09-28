using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateLeaseServiceTests
{
    [Fact]
    public void ExpiredCoordinatorCannotPublishAfterSuccessorTakesFence()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-lease-test-" + Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var scope = new BatchGateScope("p", "r", "develop", "full", "digest", "v1");
        try
        {
            var leases = new BatchGateLeaseService(root, () => now);
            var first = leases.TryAcquire(scope, "host-a")!;
            Assert.True(leases.IsCurrent(first));
            Assert.Null(leases.TryAcquire(scope, "host-b"));
            now += TimeSpan.FromMinutes(3);
            var second = leases.TryAcquire(scope, "host-b")!;
            Assert.True(second.Fence > first.Fence);
            Assert.False(leases.IsCurrent(first));
            Assert.Null(leases.Renew(first));
            Assert.True(leases.IsCurrent(second));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ContendedGuardFailsClosedWithoutThrowingFromHeartbeatOrRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-lease-test-" + Guid.NewGuid().ToString("N"));
        var scope = new BatchGateScope("p", "r", "develop", "full", "digest", "v1");
        try
        {
            var leases = new BatchGateLeaseService(root);
            var lease = leases.TryAcquire(scope, "host-a")!;
            var folder = Assert.Single(Directory.GetDirectories(root));
            using (var guard = new FileStream(Path.Combine(folder, "lock"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(leases.Renew(lease));
                Assert.False(leases.IsCurrent(lease));
                Assert.False(leases.Release(lease));
            }
            Assert.True(leases.IsCurrent(lease));
            Assert.True(leases.Release(lease));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
