using AgentStudio.Management;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ManagementStatisticsCacheTests
{
    [Fact]
    public void ConcurrentStatusReads_StartOneRefresh_AndReturnPending()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scans = 0;
        var cache = new ManagementStatisticsCache(() =>
        {
            Interlocked.Increment(ref scans);
            entered.Set();
            release.Wait();
            // clock-independent: The timestamp is opaque cache data and is never compared with now.
            return new ManagementStatistics(7, 1, 2, 3, 4, 5, null, [], "2026-11-01T00:00:00Z");
        });

        Assert.Equal("pending", cache.Current().State);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        Parallel.For(0, 32, _ =>
        {
            var current = cache.Current();
            Assert.Null(current.Snapshot);
            Assert.Equal("pending", current.State);
        });
        Assert.Equal(1, Volatile.Read(ref scans));

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => cache.Current().State == "ready", TimeSpan.FromSeconds(30)));
        Assert.Equal(7, cache.Current().Snapshot!.SizeBytes);
        Assert.Equal(1, Volatile.Read(ref scans));
    }

    [Fact]
    public void Inventory_SkipsGit_AndDoesNotOpenEventContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "management-statistics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "results"));
            File.WriteAllBytes(Path.Combine(root, ".git", "large.jsonl"), new byte[128]);
            var evidence = Path.Combine(root, "events.jsonl");
            File.WriteAllText(evidence, "one\ntwo\n");
            File.WriteAllBytes(Path.Combine(root, "results", "artifact.txt"), new byte[3]);
            using (var held = new FileStream(evidence, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var snapshot = ManagementStatisticsCache.ScanFiles(root);

                Assert.Equal(new FileInfo(evidence).Length + 3, snapshot.SizeBytes);
                Assert.Equal(1, snapshot.EventFiles);
                Assert.Equal(1, snapshot.ArtifactFiles);
                Assert.NotNull(snapshot.LastWriteAt);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
