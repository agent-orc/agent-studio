using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostQuotaIngestionTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "host-quota-ingestion-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;

    public HostQuotaIngestionTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
            ["Quota:TtlSeconds"] = "600"
        }).Build();
    }

    [Fact]
    public async Task NetworkedProfileNeverStartsProviderProbeAndAcceptsHostObservationTime()
    {
        var service = NewService();
        var before = service.GetWithBackgroundRefresh();
        Assert.Contains("reporting host", Assert.Single(before.Snapshots).Error);
        await service.RefreshAsync("codex");
        var observed = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        service.IngestHostReading(Reading('a', 25, observed));
        var snapshot = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(observed, snapshot.FetchedAt);
        Assert.Equal("host /status", snapshot.Source);
        Assert.Null(snapshot.RawSample);
        Assert.Equal(25, Assert.Single(snapshot.Windows).UsedPct);
        Assert.NotNull(service.GetCachedFor("codex"));
    }

    [Fact]
    public void SeparateAccountsAreDisplayedSeparatelyAndNotMergedForAdmission()
    {
        var service = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        service.IngestHostReading(Reading('a', 18, now));
        service.IngestHostReading(Reading('b', 80, now));
        Assert.Equal(2, service.GetCached().Snapshots.Count);
        Assert.Null(service.GetCachedFor("codex"));
        Assert.Equal(new[] { 18d, 80d }, service.GetCached().Snapshots
            .SelectMany(snapshot => snapshot.Windows)
            .Select(window => window.UsedPct!.Value).Order().ToArray());
    }

    [Fact]
    public void ExpiredHostReadingIsExcludedFromAdmissionButRetainedForDisplay()
    {
        var service = NewService();
        service.IngestHostReading(Reading('a', 18, _clock.GetUtcNow().UtcDateTime.AddMinutes(-11)));
        Assert.Null(service.GetCachedFor("codex"));
        Assert.Single(service.GetCached().Snapshots);
    }

    [Fact]
    public void RepeatedSyncOfOneReadingCannotConfirmAnImplausibleDrop()
    {
        var service = NewService();
        var first = _clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        service.IngestHostReading(Reading('a', 90, first));
        var drop = Reading('a', 5, first.AddMinutes(1));
        service.IngestHostReading(drop);
        service.IngestHostReading(drop);
        Assert.True(Assert.Single(service.GetCached().Snapshots).Suspicious);
    }

    [Fact]
    public void FailedHostReportUpdatesFailureTimeWithoutChangingLastObservation()
    {
        var service = NewService();
        var observed = _clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        var failed = observed.AddMinutes(1);
        var reading = Reading('a', 25, observed);
        service.IngestHostReading(reading);
        service.IngestHostReading(reading with
        {
            Error = "No healthy host holds a codex sign-in.", ProbeFailedAt = failed
        });
        var snapshot = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(observed, snapshot.FetchedAt);
        Assert.Equal(failed, snapshot.ProbeFailedAt);
        Assert.Contains("sign-in", snapshot.Error);
    }

    [Fact]
    public void HistoryStoresTwoSubscriptionsAtTheSameObservationTime()
    {
        var history = new QuotaHistoryStore(_config, NullLogger<QuotaHistoryStore>.Instance, _clock);
        var service = new QuotaService(NullLogger<QuotaService>.Instance, [new NeverProbe()],
            _config, new QuotaCacheStore(_config, NullLogger<QuotaCacheStore>.Instance),
            history: history, clock: _clock);
        var now = _clock.GetUtcNow().UtcDateTime;
        service.IngestHostReading(Reading('a', 18, now));
        service.IngestHostReading(Reading('b', 80, now));
        Assert.Equal(2, history.Read("codex", now.AddMinutes(-1)).Count);
        var report = QuotaHistoryReport.Build("codex", 1,
            history.Read("codex", now.AddMinutes(-1)), now);
        Assert.Equal(2, report.Windows.Count);
        Assert.NotEqual(report.Windows[0].Label, report.Windows[1].Label);
    }

    private QuotaService NewService()
        => new(NullLogger<QuotaService>.Instance, [new NeverProbe()], _config,
            new QuotaCacheStore(_config, NullLogger<QuotaCacheStore>.Instance), clock: _clock);

    private static HostQuotaReadingDto Reading(char key, double used, DateTime at)
        => new("codex", new string(key, 64), "host-a", at,
            "codex 0.150", "Plus", [new HostQuotaWindowDto("Weekly", used, null, null)], null);

    private sealed class NeverProbe : IQuotaProbe
    {
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => throw new InvalidOperationException("Networked Studio started a provider CLI.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }
}
