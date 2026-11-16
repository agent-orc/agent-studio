using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3593: a demo profile or an isolated worktree test backend shares the
/// runner host, so its quota reads must come from the pinned demo snapshot and
/// never from the host's installed CLIs or a cache file on disk.
/// </summary>
public sealed class QuotaServiceDemoSnapshotTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 2, 30, DateTimeKind.Utc);

    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quota-demo-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));

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
    public void PinnedSnapshot_IsFreshAndFollowsTheFixedSchedule()
    {
        // clock-independent: the pinned snapshot is built from this explicit instant, never from "now".
        var codex = DemoQuotaSnapshots.For("codex", Now);
        var claude = DemoQuotaSnapshots.For("claude", Now);

        // Read on the 5-minute probe tick, well inside the 10-minute cache TTL.
        Assert.Equal(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc), codex.FetchedAt);
        Assert.Null(codex.Error);
        Assert.Equal(["5-hour", "Weekly"], codex.Windows.Select(w => w.Label));
        Assert.Equal(["Current session (5h)", "Weekly (all models)"], claude.Windows.Select(w => w.Label));
        Assert.All(codex.Windows.Concat(claude.Windows), w =>
        {
            Assert.True(w.ResetAt > Now);
            Assert.True(w.ObservedStartAt <= codex.FetchedAt);
            Assert.InRange(w.UsedPct!.Value, 0, 100);
        });
        Assert.Empty(DemoQuotaSnapshots.For("gemini", Now).Windows);
    }

    [Fact]
    public void PinnedSnapshot_KeepsItsResetInstantAndClimbsUntilTheReset()
    {
        var earlier = DemoQuotaSnapshots.For("codex", Now);
        var later = DemoQuotaSnapshots.For("codex", Now.AddMinutes(40));

        var weeklyBefore = earlier.Windows.Single(w => w.Label == "Weekly");
        var weeklyAfter = later.Windows.Single(w => w.Label == "Weekly");
        Assert.Equal(weeklyBefore.ResetAt, weeklyAfter.ResetAt);
        Assert.True(weeklyAfter.UsedPct > weeklyBefore.UsedPct);

        var afterReset = DemoQuotaSnapshots.For("codex", weeklyBefore.ResetAt!.Value.AddMinutes(10))
            .Windows.Single(w => w.Label == "Weekly");
        Assert.Equal(weeklyBefore.ResetAt!.Value.AddDays(7), afterReset.ResetAt);
        Assert.True(afterReset.UsedPct < weeklyBefore.UsedPct);
    }

    [Fact]
    public void PinnedSnapshot_ScheduleIsSeedable()
    {
        var anchor = new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc);
        var configuration = Configuration(DemoQuotaSnapshots.AnchorKey, "2026-03-02T06:00:00Z");

        Assert.Equal(anchor, DemoQuotaSnapshots.Anchor(configuration));
        Assert.Equal(DemoQuotaSnapshots.DefaultAnchorUtc, DemoQuotaSnapshots.Anchor(Configuration("x", "y")));
        var seeded = DemoQuotaSnapshots.For("claude", Now, anchor);
        Assert.NotEqual(
            DemoQuotaSnapshots.For("claude", Now).Windows.Select(w => w.ResetAt),
            seeded.Windows.Select(w => w.ResetAt));
        Assert.Equal(seeded, DemoQuotaSnapshots.For("claude", Now, anchor), SnapshotComparer.Instance);
    }

    [Fact]
    public void PinnedBackend_BackfillsAndAppendsQuotaHistoryOncePerProbeTick()
    {
        var configuration = Configuration("ATP_WORKTREE_TEST_BACKEND", "1");
        var history = new QuotaHistoryStore(configuration, NullLogger<QuotaHistoryStore>.Instance, _clock);
        var service = NewService(new CountingProbe(), configuration, history);

        var backfilled = history.Read("codex", Now.AddDays(-30));
        // One hourly reading per window over the backfill span.
        Assert.Equal(2 * (int)DemoQuotaSnapshots.BackfillSpan.TotalHours, backfilled.Count);
        Assert.Contains(backfilled, p => p.Window == "Weekly" && p.ResetAt is not null);

        service.GetCached();
        service.GetCachedFor("codex");
        Assert.Equal(backfilled.Count + 2, history.Read("codex", Now.AddDays(-30)).Count);

        _clock.Advance(TimeSpan.FromMinutes(5));
        service.GetCached();
        var points = history.Read("codex", Now.AddDays(-30));
        Assert.Equal(backfilled.Count + 4, points.Count);
        var weekly = points.Where(p => p.Window == "Weekly").ToList();
        Assert.True(weekly[^1].UsedPct > weekly[0].UsedPct || weekly[^1].ResetAt != weekly[0].ResetAt);

        // A restart only fills the gap: nothing is recorded twice.
        var restarted = new QuotaHistoryStore(configuration, NullLogger<QuotaHistoryStore>.Instance, _clock);
        NewService(new CountingProbe(), configuration, restarted);
        Assert.Equal(points.Count, restarted.Read("codex", Now.AddDays(-30)).Count);
    }

    [Fact]
    public void EmptyFixtureBackend_WritesNoQuotaHistory()
    {
        var configuration = Configuration("Quota:Mode", DemoQuotaSnapshots.EmptyFixtureMode);
        var history = new QuotaHistoryStore(configuration, NullLogger<QuotaHistoryStore>.Instance, _clock);
        var service = NewService(new CountingProbe(), configuration, history);

        service.GetCached();

        Assert.Empty(history.Read("codex", Now.AddDays(-30)));
    }

    [Fact]
    public async Task EmptyFixtureSnapshot_NeverProbesAndLeavesAdmissionUncapped()
    {
        var probe = new CountingProbe();
        var service = NewService(probe, "Quota:Mode", DemoQuotaSnapshots.EmptyFixtureMode);

        var snapshot = await service.RefreshAsync("codex");

        Assert.Equal(0, probe.Calls);
        Assert.Equal("codex", snapshot!.CliType);
        Assert.Equal(DemoQuotaSnapshots.Source, snapshot.Source);
        Assert.Empty(snapshot.Windows);
    }

    private static void AssertPinnedCodex(QuotaSnapshot snap)
    {
        Assert.Equal("codex", snap.CliType);
        Assert.Equal(DemoQuotaSnapshots.Source, snap.Source);
        Assert.Null(snap.Error);
        Assert.Equal(
            DemoQuotaSnapshots.For("codex", Now).Windows.Select(w => (w.Label, w.UsedPct, w.ResetAt)),
            snap.Windows.Select(w => (w.Label, w.UsedPct, w.ResetAt)));
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
        => NewService(probe, Configuration(key, value), history: null);

    private QuotaService NewService(IQuotaProbe probe, IConfiguration configuration, QuotaHistoryStore? history)
        => new(
            NullLogger<QuotaService>.Instance,
            [probe],
            configuration,
            new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance),
            history: history,
            clock: _clock);

    private sealed class SnapshotComparer : IEqualityComparer<QuotaSnapshot>
    {
        public static readonly SnapshotComparer Instance = new();
        public bool Equals(QuotaSnapshot? x, QuotaSnapshot? y)
            => x is not null && y is not null && x.FetchedAt == y.FetchedAt
                && x.Windows.SequenceEqual(y.Windows);
        public int GetHashCode(QuotaSnapshot obj) => obj.FetchedAt.GetHashCode();
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
