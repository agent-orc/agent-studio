using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3593: a demo profile or an isolated worktree test backend shares the
/// runner host, so its quota reads must come from the pinned demo snapshot and
/// never from the host's installed CLIs or a cache file on disk.
/// </summary>
public sealed class QuotaServiceDemoSnapshotTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quota-demo-" + Guid.NewGuid().ToString("N"));

    public QuotaServiceDemoSnapshotTests() => Directory.CreateDirectory(_repoDir);

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    public static TheoryData<string, string> PinnedConfigurations => new()
    {
        { "ATP_WORKTREE_TEST_BACKEND", "1" },
        { "Security:Profile", "public-demo-readonly" },
        { "Quota:Mode", "demo" },
    };

    [Theory]
    [MemberData(nameof(PinnedConfigurations))]
    public async Task PinnedBackend_NeverProbesTheHostCli(string key, string value)
    {
        var probe = new CountingProbe();
        var service = NewService(probe, key, value);

        var background = service.GetWithBackgroundRefresh();
        var refreshed = await service.RefreshAllAsync();
        var single = await service.RefreshAsync("codex");
        await service.InvalidateForGroundTruthLimit("codex", "usage limit");
        await Task.Delay(50);

        Assert.Equal(0, probe.Calls);
        foreach (var report in new[] { background, refreshed })
        {
            var snap = Assert.Single(report.Snapshots);
            AssertPinnedCodex(snap);
        }
        AssertPinnedCodex(single!);
        AssertPinnedCodex(service.GetCachedFor("codex")!);
    }

    [Fact]
    public void PinnedBackend_IgnoresHostQuotaCacheOnDisk()
    {
        new QuotaCacheStore(Configuration("Quota:TtlSeconds", "600"), NullLogger<QuotaCacheStore>.Instance)
            .Write([new QuotaSnapshot
            {
                CliType = "codex", Plan = "Pro", Source = "/status",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 91 }]
            }]);
        var local = NewService(new CountingProbe(), "Quota:TtlSeconds", "600");
        Assert.Equal(91, Assert.Single(local.GetCached().Snapshots).Windows[0].UsedPct);

        var demo = NewService(new CountingProbe(), "ATP_WORKTREE_TEST_BACKEND", "1");

        AssertPinnedCodex(Assert.Single(demo.GetCached().Snapshots));
    }

    [Fact]
    public async Task LocalBackend_StillProbes()
    {
        var probe = new CountingProbe();
        var service = NewService(probe, "ATP_WORKTREE_TEST_BACKEND", "0");

        var snap = await service.RefreshAsync("codex");

        Assert.Equal(1, probe.Calls);
        Assert.Equal("/status", snap!.Source);
    }

    [Fact]
    public void PinnedSnapshot_IsFreshAndCarriesFixedPercentages()
    {
        // clock-independent: the pinned snapshot is built from this explicit instant, never from "now".
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        var codex = DemoQuotaSnapshots.For("codex", now);
        var claude = DemoQuotaSnapshots.For("claude", now);

        Assert.Equal(now, codex.FetchedAt);
        Assert.Null(codex.Error);
        Assert.Equal([18d, 42d], codex.Windows.Select(w => w.UsedPct!.Value));
        Assert.Equal([24d, 35d], claude.Windows.Select(w => w.UsedPct!.Value));
        Assert.All(codex.Windows.Concat(claude.Windows), w => Assert.True(w.ResetAt > now));
        Assert.Empty(DemoQuotaSnapshots.For("gemini", now).Windows);
    }

    private static void AssertPinnedCodex(QuotaSnapshot snap)
    {
        Assert.Equal("codex", snap.CliType);
        Assert.Equal(DemoQuotaSnapshots.Source, snap.Source);
        Assert.Null(snap.Error);
        Assert.Equal([18d, 42d], snap.Windows.Select(w => w.UsedPct!.Value));
    }

    private IConfiguration Configuration(string key, string value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                [key] = value,
            })
            .Build();

    private QuotaService NewService(IQuotaProbe probe, string key, string value)
    {
        var configuration = Configuration(key, value);
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            [probe],
            configuration,
            new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance));
    }

    private sealed class CountingProbe : IQuotaProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new QuotaSnapshot
            {
                CliType = "codex", Plan = "Pro", Source = "/status",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 91 }]
            });
        }
    }
}
