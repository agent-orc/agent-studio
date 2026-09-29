using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RefMutationLeaseServiceTests
{
    [Fact]
    public async Task DevelopAndMainPublishPhasesShareOneFencedLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "ref-lease-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RefMutationLeaseService(root);
            using var first = await service.AcquireAsync("project", "repo", "develop", CancellationToken.None);
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.AcquireAsync("project", "repo", "main", wait.Token));
            Assert.True(first.Fence > 0);
            first.Dispose();
            using var second = await new RefMutationLeaseService(root)
                .AcquireAsync("project", "repo", "main", CancellationToken.None);
            Assert.True(second.Fence > first.Fence);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeaseIsCurrentOnlyWhileHeldAndItsFenceIsTheLatest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ref-lease-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RefMutationLeaseService(root);
            var held = await service.AcquireAsync("project", "repo", "develop", CancellationToken.None);
            Assert.True(RefMutationLeaseService.IsCurrent(held));
            // A later grant elsewhere advanced the durable fence.
            var fence = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            File.WriteAllText(fence, (held.Fence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(RefMutationLeaseService.IsCurrent(held));
            File.WriteAllText(fence, held.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(RefMutationLeaseService.IsCurrent(held));
            held.Dispose();
            Assert.False(RefMutationLeaseService.IsCurrent(held));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
