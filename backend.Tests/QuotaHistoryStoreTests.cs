using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3001: the durable per-CLI quota series. Each trusted snapshot adds one
/// reading per window, survives a restart, and is kept for 14 days.
/// </summary>
public sealed class QuotaHistoryStoreTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 28, 20, 27, 0, DateTimeKind.Utc);
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "atp-quota-history-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Start));

    public QuotaHistoryStoreTests()
    {
        Directory.CreateDirectory(_repoDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _repoDir })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    private QuotaHistoryStore NewStore() => new(_config, NullLogger<QuotaHistoryStore>.Instance, _clock);

    private string HistoryFile(string cli) => Path.Combine(_repoDir, ".runtime", "quota-history", cli + ".jsonl");

    private static QuotaSnapshot Snap(DateTime at, double session, double weekly, string cli = "claude") => new()
    {
        CliType = cli,
        FetchedAt = at,
        Windows =
        [
            new QuotaWindow { Label = "Current session", UsedPct = session, ResetAt = at.AddHours(3) },
            new QuotaWindow { Label = "Current week (all models)", UsedPct = weekly, ResetAt = at.AddDays(4) },
            new QuotaWindow { Label = "Extra usage", UsedPct = null },
        ],
    };

    [Fact]
    public void Record_StoresOneReadingPerWindowWithUsage()
    {
        var store = NewStore();

        var stored = store.Record(Snap(Start, 12, 67));

        Assert.Equal(2, stored);
        var points = store.Read("claude", Start.AddDays(-1));
        Assert.Equal(["Current session", "Current week (all models)"], points.Select(point => point.Window));
        var weekly = points[1];
        Assert.Equal(Start, weekly.At);
        Assert.Equal(67, weekly.UsedPct);
        Assert.Equal(Start.AddDays(4), weekly.ResetAt);
    }

    [Fact]
    public void Record_IsDurableAcrossStoreInstances()
    {
        NewStore().Record(Snap(Start, 12, 67));
        NewStore().Record(Snap(Start.AddMinutes(30), 14, 68));

        var points = NewStore().Read("claude", Start.AddDays(-1));

        Assert.Equal(4, points.Count);
        Assert.Equal([67d, 68d], points.Where(point => point.Window.Contains("week")).Select(point => point.UsedPct));
        Assert.All(points, point => Assert.Equal(DateTimeKind.Utc, point.At.Kind));
    }

    [Fact]
    public void Record_SkipsFailedSuspiciousAndAlreadyRecordedSnapshots()
    {
        var store = NewStore();
        store.Record(Snap(Start, 12, 67));

        Assert.Equal(0, store.Record(Snap(Start, 12, 67)));                                    // same probe time
        Assert.Equal(0, store.Record(Snap(Start.AddMinutes(-5), 11, 66)));                    // older probe
        Assert.Equal(0, store.Record(Snap(Start.AddMinutes(10), 13, 68) with { Error = "timeout" }));
        Assert.Equal(0, store.Record(Snap(Start.AddMinutes(20), 13, 5) with { Suspicious = true }));
        Assert.Equal(0, store.Record(new QuotaSnapshot { CliType = "claude", FetchedAt = Start.AddMinutes(30) }));
        Assert.Equal(0, store.Record(Snap(Start.AddMinutes(40), 1, 1, cli: "../escape")));

        Assert.Equal(2, store.Read("claude", Start.AddDays(-1)).Count);
    }

    [Fact]
    public void Read_KeepsSeriesPerCliAndFiltersByTime()
    {
        var store = NewStore();
        store.Record(Snap(Start, 10, 60));
        store.Record(Snap(Start.AddHours(2), 20, 64));
        store.Record(Snap(Start.AddHours(1), 5, 30, cli: "codex"));

        Assert.Equal(2, store.Read("claude", Start.AddHours(1)).Count);
        Assert.Equal(2, store.Read("CODEX", Start).Count);
        Assert.Empty(store.Read("gemini", Start));
    }

    [Fact]
    public void Retention_DropsReadingsOlderThanFourteenDays()
    {
        var store = NewStore();
        store.Record(Snap(Start, 10, 20));
        _clock.SetUtcNow(new DateTimeOffset(Start.AddDays(10)));
        store.Record(Snap(Start.AddDays(10), 30, 40));

        _clock.SetUtcNow(new DateTimeOffset(Start.AddDays(14).AddHours(1)));

        var points = store.Read("claude", DateTime.MinValue);
        Assert.Equal(2, points.Count);
        Assert.All(points, point => Assert.Equal(Start.AddDays(10), point.At));
    }

    [Fact]
    public void Retention_CompactsTheFileOnceADayOfExpiredReadingsAccumulates()
    {
        var store = NewStore();
        store.Record(Snap(Start, 10, 20));
        store.Record(Snap(Start.AddDays(10), 30, 40));
        var file = HistoryFile("claude");
        Assert.Equal(4, File.ReadAllLines(file).Length);

        // Just past retention: dropped from reads, still on disk (no rewrite per append).
        _clock.SetUtcNow(new DateTimeOffset(Start.AddDays(14).AddHours(1)));
        store.Read("claude", DateTime.MinValue);
        Assert.Equal(4, File.ReadAllLines(file).Length);

        // More than a day past retention: the file is rewritten without them.
        _clock.SetUtcNow(new DateTimeOffset(Start.AddDays(15).AddHours(1)));
        store.Read("claude", DateTime.MinValue);
        Assert.Equal(2, File.ReadAllLines(file).Length);
        Assert.Equal(2, NewStore().Read("claude", DateTime.MinValue).Count);
    }

    [Fact]
    public void Load_SkipsAMalformedLineAndRewritesTheFile()
    {
        NewStore().Record(Snap(Start, 10, 20));
        File.AppendAllText(HistoryFile("claude"), "{not json\n");

        var points = NewStore().Read("claude", DateTime.MinValue);

        Assert.Equal(2, points.Count);
        Assert.DoesNotContain(File.ReadAllLines(HistoryFile("claude")), line => line.Contains("not json"));
    }

    [Fact]
    public async Task QuotaService_RecordsEveryTrustedProbeIntoTheHistory()
    {
        var history = NewStore();
        var cache = new QuotaCacheStore(_config, NullLogger<QuotaCacheStore>.Instance);
        var call = 0;
        var probe = new DelegateProbe(() =>
        {
            call++;
            return call == 2
                ? new QuotaSnapshot { CliType = "claude", Error = "probe timed out" }
                : Snap(DateTime.UtcNow.AddSeconds(call), 10 + call, 60 + call);
        });
        var service = new QuotaService(
            NullLogger<QuotaService>.Instance, [probe], _config, cache, versionTracker: null, history: history);

        await service.RefreshAsync("claude");
        await service.RefreshAsync("claude");   // failed probe keeps the last good value; not re-recorded
        await service.RefreshAsync("claude");

        var weekly = history.Read("claude", DateTime.MinValue)
            .Where(point => point.Window.Contains("week"))
            .Select(point => point.UsedPct);
        Assert.Equal([61d, 63d], weekly);
    }

    private sealed class DelegateProbe(Func<QuotaSnapshot> next) : IQuotaProbe
    {
        public string CliType => "claude";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) => Task.FromResult(next());
    }
}
