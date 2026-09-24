using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2858: the published preparation cache had no age and no size policy. The
/// M1 pilot cache reached 36 GB in the host's shared temp root, and the only
/// reset an operator had was <c>rm -rf</c> plus a hand-made backup copy.
///
/// <see cref="ProjectPreparationCachePolicy"/> is the pure decision and is
/// covered as a matrix; <see cref="ProjectPreparationCacheSweep"/> is the
/// bounded side effect and is covered against a real cache root.
/// </summary>
public sealed class ProjectPreparationCacheRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempWorkspace _workspace = new("atp-prepare-cache-retention");

    public void Dispose() => _workspace.Dispose();

    [Theory]
    // Inside both bounds: nothing goes.
    [InlineData(5, 1, 30, 10, "")]
    // Past the age bound, regardless of how small it is.
    [InlineData(31, 1, 30, 10, "npm/a")]
    // Exactly at the age bound still counts as used within the window.
    [InlineData(30, 1, 30, 10, "")]
    // Inside the age bound but over the size bound: the least recently used goes.
    [InlineData(5, 11, 30, 10, "npm/a")]
    public void Age_expires_first_and_size_evicts_least_recently_used(
        double ageDays,
        long gibibytes,
        double maximumAgeDays,
        long maximumGibibytes,
        string expectedEvictions)
    {
        const long Gibibyte = 1024L * 1024 * 1024;
        var entries = new[]
        {
            Entry("npm/a", Now.AddDays(-ageDays), gibibytes * Gibibyte),
            Entry("npm/b", Now.AddDays(-1), Gibibyte),
        };
        var policy = new PreparationCacheRetentionPolicy(
            TimeSpan.FromDays(maximumAgeDays),
            maximumGibibytes * Gibibyte);

        var evictions = ProjectPreparationCachePolicy.Evictions(entries, policy, Now);

        Assert.Equal(
            expectedEvictions.Length == 0 ? [] : expectedEvictions.Split(','),
            evictions.Select(entry => entry.Key).ToArray());
    }

    [Fact]
    public void The_entries_of_the_running_preparation_are_never_evicted()
    {
        var live = Entry("npm/live", Now.AddYears(-1), 100);
        var stale = Entry("npm/stale", Now.AddYears(-1), 100);

        var evictions = ProjectPreparationCachePolicy.Evictions(
            [live, stale],
            PreparationCacheRetentionPolicy.Default,
            Now,
            [live.Path]);

        Assert.Equal(["npm/stale"], evictions.Select(entry => entry.Key).ToArray());
    }

    [Fact]
    public void Size_eviction_stops_as_soon_as_the_cache_fits_again()
    {
        var entries = new[]
        {
            Entry("npm/oldest", Now.AddDays(-4), 40),
            Entry("npm/older", Now.AddDays(-3), 40),
            Entry("npm/newer", Now.AddDays(-2), 40),
        };
        var policy = new PreparationCacheRetentionPolicy(TimeSpan.FromDays(365), 100);

        var evictions = ProjectPreparationCachePolicy.Evictions(entries, policy, Now);

        Assert.Equal(["npm/oldest"], evictions.Select(entry => entry.Key).ToArray());
    }

    [Fact]
    public void A_protected_entry_still_counts_towards_the_size_bound()
    {
        // Otherwise a single huge live entry would look free and the sweep would
        // leave the cache permanently over its ceiling.
        var live = Entry("npm/live", Now, 90);
        var other = Entry("npm/other", Now.AddDays(-1), 40);

        var evictions = ProjectPreparationCachePolicy.Evictions(
            [live, other],
            new PreparationCacheRetentionPolicy(TimeSpan.FromDays(365), 100),
            Now,
            [live.Path]);

        Assert.Equal(["npm/other"], evictions.Select(entry => entry.Key).ToArray());
    }

    [Theory]
    [InlineData(null, null, 30, 20)]
    [InlineData("7", "4", 7, 4)]
    // Junk and non-positive values fall back to the default: a cache without a
    // ceiling is exactly what this policy exists to prevent.
    [InlineData("0", "-1", 30, 20)]
    [InlineData("soon", "big", 30, 20)]
    public void Operator_overrides_apply_and_nonsense_falls_back_to_the_default(
        string? days,
        string? gibibytes,
        double expectedDays,
        long expectedGibibytes)
    {
        var policy = PreparationCacheRetentionPolicy.FromEnvironment(name => name switch
        {
            PreparationCacheRetentionPolicy.MaximumAgeVariable => days,
            PreparationCacheRetentionPolicy.MaximumSizeVariable => gibibytes,
            _ => null,
        });

        Assert.Equal(expectedDays, policy.MaximumAge.TotalDays);
        Assert.Equal(expectedGibibytes * 1024L * 1024 * 1024, policy.MaximumBytes);
    }

    [Fact]
    public void Sweep_removes_the_expired_entry_and_leaves_the_live_one_usable()
    {
        var root = _workspace.Directory("product-cache");
        var expired = PublishEntry(root, "npm", "expired", Now.AddDays(-90), 4096);
        var live = PublishEntry(root, "npm", "live", Now.AddDays(-1), 4096);

        var result = ProjectPreparationCacheSweep.Run(
            root,
            PreparationCacheRetentionPolicy.Default,
            Now);

        Assert.Equal(2, result.Inspected);
        Assert.Equal(1, result.Removed);
        Assert.Equal(4096, result.FreedBytes);
        Assert.False(Directory.Exists(expired));
        Assert.True(File.Exists(Path.Combine(live, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(live, "content", "payload.bin")));
    }

    [Fact]
    public void Inventory_reads_the_published_size_and_skips_publications_in_flight()
    {
        var root = _workspace.Directory("product-cache");
        PublishEntry(root, "nuget", "published", Now, 2048);
        var staging = Path.Combine(root, "entries", "nuget", "inflight.staging-abcdef");
        Directory.CreateDirectory(Path.Combine(staging, "content"));
        File.WriteAllText(Path.Combine(staging, "manifest.json"), "{\"sizeBytes\":1}");

        var inventory = ProjectPreparationCacheSweep.Inventory(root);

        var entry = Assert.Single(inventory);
        Assert.Equal("published", entry.Key);
        Assert.Equal("nuget", entry.Block);
        Assert.Equal(2048, entry.Bytes);
    }

    [Fact]
    public void Inventory_measures_an_entry_published_before_the_size_was_recorded()
    {
        var root = _workspace.Directory("product-cache");
        var legacy = PublishEntry(root, "npm", "legacy", Now, 1500);
        File.WriteAllText(
            Path.Combine(legacy, "manifest.json"),
            "{\"schemaVersion\":1,\"block\":\"npm\",\"key\":\"legacy\"}");
        Directory.SetLastWriteTimeUtc(legacy, Now);

        var entry = Assert.Single(ProjectPreparationCacheSweep.Inventory(root));

        Assert.Equal(1500, entry.Bytes);
    }

    [Fact]
    public void Touch_turns_the_entry_stamp_into_a_last_use_date()
    {
        var root = _workspace.Directory("product-cache");
        var entry = PublishEntry(root, "npm", "hit", Now.AddDays(-90), 16);

        ProjectPreparationCacheSweep.Touch(entry, Now);

        var result = ProjectPreparationCacheSweep.Run(root, PreparationCacheRetentionPolicy.Default, Now);

        Assert.Equal(0, result.Removed);
        Assert.True(Directory.Exists(entry), "an entry used just now must not expire on its publication date");
    }

    [Fact]
    public void Run_roots_of_dead_processes_are_reclaimed_and_a_live_one_is_kept()
    {
        // The dominant cost of a real cache: each run root is a full working copy
        // of every block, and a killed gate or coding run never releases its own.
        // This repository's runner cache held 1.3 GB of published entries under
        // 16 GB of them.
        var root = _workspace.Directory("product-cache");
        var abandoned = RunRoot(root, "abandoned", Now - ProjectPreparationCacheSweep.DefaultRunRootRetention - TimeSpan.FromMinutes(1));
        var live = RunRoot(root, "live", Now - TimeSpan.FromMinutes(5));

        var result = ProjectPreparationCacheSweep.PruneRunRoots(
            root, ProjectPreparationCacheSweep.DefaultRunRootRetention, Now);

        Assert.Equal(2, result.Inspected);
        Assert.Equal(1, result.Removed);
        Assert.Equal(2048, result.FreedBytes);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public void The_sweep_reclaims_entries_and_run_roots_in_one_pass()
    {
        var root = _workspace.Directory("product-cache");
        PublishEntry(root, "npm", "expired", Now.AddDays(-90), 4096);
        var abandoned = RunRoot(root, "abandoned", Now.AddDays(-2));

        var result = ProjectPreparationCacheSweep.Run(
            root, PreparationCacheRetentionPolicy.Default, Now);

        Assert.Equal(2, result.Removed);
        Assert.Equal(4096 + 2048, result.FreedBytes);
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void The_existing_sweep_removes_quarantined_incomplete_entries()
    {
        var root = _workspace.Directory("product-cache");
        var quarantined = Path.Combine(
            root,
            ProjectPreparationCacheSweep.QuarantineDirectoryName,
            "nuget",
            "broken.incomplete-123");
        Directory.CreateDirectory(Path.Combine(quarantined, "content"));
        File.WriteAllText(Path.Combine(quarantined, "manifest.json"), "{}");

        ProjectPreparationCacheSweep.Run(root, PreparationCacheRetentionPolicy.Default, Now);

        Assert.False(Directory.Exists(quarantined));
    }

    [Fact]
    public void Sweep_of_a_cache_root_that_was_never_written_is_a_no_op()
    {
        var result = ProjectPreparationCacheSweep.Run(
            _workspace.Combine("never-used"),
            PreparationCacheRetentionPolicy.Default,
            Now);

        Assert.Equal(PreparationCacheSweepResult.Empty, result);
    }

    [Fact]
    public void The_cache_root_is_product_owned_and_never_the_temp_root()
    {
        var configured = ProjectPreparationPaths.DefaultCacheRoot(
            name => name == ProjectPreparationPaths.CacheRootVariable ? _workspace.Path : null);
        Assert.Equal(Path.GetFullPath(_workspace.Path), configured);

        var resolved = ProjectPreparationPaths.DefaultCacheRoot(_ => null);
        Assert.False(
            resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal),
            $"'{resolved}' is inside the temp root; a cache there has no owner and no bound.");

        // The project segment keeps one project's eviction away from another's.
        var project = ProjectPreparationPaths.ProjectCacheRoot("agent studio/m1 pilot", _ => null);
        Assert.Equal(Path.Combine(resolved, "agent-studio-m1-pilot"), project);
    }

    private static string RunRoot(string cacheRoot, string runId, DateTime lastWriteUtc)
    {
        var path = Path.Combine(cacheRoot, ProjectPreparationCacheSweep.RunsDirectoryName, runId);
        Directory.CreateDirectory(Path.Combine(path, "nuget"));
        File.WriteAllBytes(Path.Combine(path, "nuget", "restored.bin"), new byte[2048]);
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static PreparationCacheEntry Entry(string key, DateTime lastUsedUtc, long bytes)
        => new($"/cache/entries/{key}", key.Split('/')[0], key, lastUsedUtc, bytes);

    private static string PublishEntry(
        string cacheRoot,
        string block,
        string key,
        DateTime lastUsedUtc,
        long bytes)
    {
        var entry = Path.Combine(cacheRoot, "entries", block, key);
        Directory.CreateDirectory(Path.Combine(entry, "content"));
        File.WriteAllBytes(Path.Combine(entry, "content", "payload.bin"), new byte[bytes]);
        File.WriteAllText(
            Path.Combine(entry, "manifest.json"),
            $"{{\"schemaVersion\":2,\"block\":\"{block}\",\"key\":\"{key}\",\"sizeBytes\":{bytes}}}");
        Directory.SetLastWriteTimeUtc(entry, lastUsedUtc);
        return entry;
    }
}
