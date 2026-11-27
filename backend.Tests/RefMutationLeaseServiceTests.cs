using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RefMutationLeaseServiceTests
{
    [Theory]
    [InlineData("history-created")]
    [InlineData("history-partial")]
    [InlineData("history-written")]
    [InlineData("history-flushed")]
    [InlineData("history-published")]
    [InlineData("fence-created")]
    [InlineData("fence-partial")]
    [InlineData("fence-written")]
    [InlineData("fence-flushed")]
    [InlineData("fence-published")]
    public async Task ReconstructedServiceRecoversEveryWriteBoundary(string boundary)
    {
        foreach (var mode in new[] { "new", "legacy", "history" })
        {
            var root = NewRoot();
            try
            {
                RefMutationLeaseService.Lease? first = null;
                if (mode != "new")
                {
                    first = await Acquire(new RefMutationLeaseService(root));
                    first.Dispose();
                    if (mode == "legacy")
                    {
                        var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
                        File.Delete(path + ".history");
                    }
                }
                long interruptedFence = 0;
                var stopped = new RefMutationLeaseService(root, stage =>
                {
                    if (stage != boundary) return;
                    if (stage == "fence-published")
                    {
                        var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
                        interruptedFence = long.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    throw new SimulatedStopException();
                });
                await Assert.ThrowsAsync<SimulatedStopException>(() => Acquire(stopped));
                using var next = await Acquire(new RefMutationLeaseService(root));
                Assert.True(next.Fence > (first?.Fence ?? 0));
                Assert.True(next.Fence > interruptedFence);
                if (first is not null) Assert.False(RefMutationLeaseService.IsCurrent(first));
                Assert.True(RefMutationLeaseService.IsCurrent(next));
                next.Dispose();
                using var last = await Acquire(new RefMutationLeaseService(root));
                Assert.True(last.Fence > next.Fence);
                Assert.False(RefMutationLeaseService.IsCurrent(next));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("broken")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    public async Task InvalidOrPartialAuthorityRecoversFromReservedGeneration(string damaged)
    {
        var root = NewRoot();
        try
        {
            RefMutationLeaseService.Lease? old = null;
            for (var i = 0; i < 12; i++)
            {
                old = await Acquire(new RefMutationLeaseService(root));
                old.Dispose();
            }
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            File.WriteAllText(path, damaged);
            using var next = await Acquire(new RefMutationLeaseService(root));
            Assert.True(next.Fence > old!.Fence);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("broken")]
    [InlineData("9223372036854775807")]
    public async Task UnsafeLegacyAuthorityHoldsOnlyItsRepositoryAndReleasesLock(string damaged)
    {
        var root = NewRoot();
        try
        {
            using (var seed = await Acquire(new RefMutationLeaseService(root))) { }
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            File.Delete(path + ".history");
            File.WriteAllText(path, damaged);
            for (var i = 0; i < 2; i++)
            {
                var hold = await Assert.ThrowsAsync<RefMutationLeaseService.RepositoryHoldException>(() =>
                    Acquire(new RefMutationLeaseService(root)));
                Assert.Equal("ref-mutation-fence-hold", hold.Code);
                Assert.Equal("repo", hold.Repository);
                Assert.Equal("project", hold.Project);
                Assert.Equal(path, hold.FencePath);
                Assert.Equal(damaged, File.ReadAllText(path));
            }
            using var other = await new RefMutationLeaseService(root)
                .AcquireAsync("project", "another-repo", "develop", CancellationToken.None);
            Assert.True(RefMutationLeaseService.IsCurrent(other));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CorruptHistoryFailsClosedEvenWithAValidAuthority()
    {
        var root = NewRoot();
        try
        {
            using (var seed = await Acquire(new RefMutationLeaseService(root))) { }
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            var history = File.ReadAllText(path + ".history");
            File.WriteAllText(path + ".history", history[..^1] + (history[^1] == '0' ? "1" : "0"));
            await Assert.ThrowsAsync<RefMutationLeaseService.RepositoryHoldException>(() =>
                Acquire(new RefMutationLeaseService(root)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ValidLegacyFenceMigratesWithoutResettingSequence()
    {
        var root = NewRoot();
        try
        {
            using (var seed = await Acquire(new RefMutationLeaseService(root))) { }
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            File.Delete(path + ".history");
            File.WriteAllText(path, "145");
            using var next = await Acquire(new RefMutationLeaseService(root));
            Assert.Equal(146, next.Fence);
            Assert.True(File.Exists(path + ".history"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingAuthorityRecoversAndReservedMaximumHolds()
    {
        var root = NewRoot();
        try
        {
            using (var seed = await Acquire(new RefMutationLeaseService(root))) { }
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            File.Delete(path);
            using (var recovered = await Acquire(new RefMutationLeaseService(root)))
                Assert.Equal(2, recovered.Fence);
            File.Delete(path + ".history");
            File.WriteAllText(path, (long.MaxValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var maximum = await Acquire(new RefMutationLeaseService(root)))
                Assert.Equal(long.MaxValue, maximum.Fence);
            File.WriteAllText(path, "1");
            var service = new RefMutationLeaseService(root);
            await Assert.ThrowsAsync<RefMutationLeaseService.RepositoryHoldException>(() => Acquire(service));
            // Repair the evidence for this test only, then prove the same local
            // semaphore was released by the hold path.
            File.WriteAllText(path, "145");
            File.Delete(path + ".history");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var repaired = await service.AcquireAsync("project", "repo", "develop", deadline.Token);
            Assert.Equal(146, repaired.Fence);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnreadableHistorySurfacesTypedHold()
    {
        var root = NewRoot();
        try
        {
            using (var seed = await Acquire(new RefMutationLeaseService(root))) { }
            var historyPath = Assert.Single(Directory.GetFiles(root, "fence.history", SearchOption.AllDirectories));
            File.Delete(historyPath);
            Directory.CreateDirectory(historyPath);
            var hold = await Assert.ThrowsAsync<RefMutationLeaseService.RepositoryHoldException>(() =>
                Acquire(new RefMutationLeaseService(root)));
            Assert.NotNull(hold.InnerException);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "ref-lease-test-" + Guid.NewGuid().ToString("N"));
    private static Task<RefMutationLeaseService.Lease> Acquire(RefMutationLeaseService service) =>
        service.AcquireAsync("project", "repo", "develop", CancellationToken.None);
    private sealed class SimulatedStopException : Exception { }

    [Fact]
    public async Task RestartAfterTruncationRecoversWithoutReusingGrantedFence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ref-lease-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await new RefMutationLeaseService(root)
                .AcquireAsync("project", "repo", "develop", CancellationToken.None);
            var granted = first.Fence;
            first.Dispose();
            var path = Assert.Single(Directory.GetFiles(root, "fence", SearchOption.AllDirectories));
            // Reproduce the old stop immediately after FileMode.Create truncates the authority.
            using (File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            using var recovered = await new RefMutationLeaseService(root)
                .AcquireAsync("project", "repo", "main", CancellationToken.None);
            Assert.True(recovered.Fence > granted);
            Assert.False(RefMutationLeaseService.IsCurrent(first));
            Assert.True(RefMutationLeaseService.IsCurrent(recovered));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
            using var crossProcessWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new RefMutationLeaseService(root).AcquireAsync("project", "repo", "main", crossProcessWait.Token));
            Assert.True(RefMutationLeaseService.IsCurrent(first));
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
