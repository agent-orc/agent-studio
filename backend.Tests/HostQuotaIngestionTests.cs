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

    [Fact]
    public void FailureRetainsOnlyItsOwnSubscriptionsLastGoodValuesAndZone()
    {
        var service = NewService();
        var observed = _clock.GetUtcNow().UtcDateTime;
        service.IngestHostReading(Reading('a', 30, observed) with { HostTimeZone = "Europe/Berlin" });
        service.IngestHostReading(Reading('b', 70, observed));
        _clock.Advance(TimeSpan.FromMinutes(2));
        service.IngestHostReading(Reading('a', 0, observed.AddMinutes(1)) with
        {
            Windows = [], Error = "A codex host quota probe failed.", ProbeFailedAt = observed.AddMinutes(1)
        });
        var report = service.GetCached();
        var failed = Assert.Single(report.Snapshots, s => s.SubscriptionKey == new string('a', 64));
        Assert.Equal(30, Assert.Single(failed.Windows).UsedPct);
        Assert.Equal("Europe/Berlin", failed.HostTimeZone);
        Assert.Equal(TimeSpan.FromMinutes(2), report.At - failed.FetchedAt);
        Assert.NotNull(failed.Error);
        Assert.NotNull(failed.ProbeFailedAt);
        Assert.Equal(2, report.Snapshots.Count);
    }

    [Theory]
    [InlineData(-84, 84, 50)]
    [InlineData(-168, 0, 100)]
    [InlineData(1, 169, 0)]
    public void ReportCarriesElapsedWindowPercentage(double startHours, double resetHours, double expected)
    {
        var service = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        service.IngestHostReading(Reading('a', 30, now) with
        {
            Windows = [new("Weekly", 30, now.AddHours(resetHours), null)]
        });
        Assert.Equal(expected, Assert.Single(Assert.Single(service.GetCached().Snapshots).Windows).ElapsedPct);
        // Explicit anchors and unknown windows use the same response-time clock.
        var report = QuotaWindowProjection.WithElapsedPercent(new QuotaReport { At = now, Snapshots = [new() { Windows = [
            new() { Label = "custom", ResetAt = now.AddHours(resetHours), ObservedStartAt = now.AddHours(startHours) },
            new() { Label = "unknown" }
        ] }] });
        Assert.Equal(expected, report.Snapshots[0].Windows[0].ElapsedPct);
        Assert.Null(report.Snapshots[0].Windows[1].ElapsedPct);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(report,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(expected, json.GetProperty("snapshots")[0].GetProperty("windows")[0].GetProperty("elapsedPct").GetDouble());
    }

    [Theory]
    [InlineData(0, SelectionOutcome.Refused)]
    [InlineData(1, SelectionOutcome.Primary)]
    [InlineData(2, SelectionOutcome.Primary)]
    public void YesterdayFailureCannotBecomeProviderUnavailability(int successes, SelectionOutcome expected)
    {
        var quota = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        quota.IngestHostReading(Reading('c', 0, now.AddDays(-1)) with
        {
            Windows = [], Error = "A codex host quota probe failed.", ProbeFailedAt = now.AddDays(-1)
        });
        for (var i = 0; i < successes; i++)
            quota.IngestHostReading(Reading((char)('a' + i), 20 + i, now));

        var result = Select(quota);

        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain("probe failed", result.Reason);
        Assert.Equal(successes + 1, quota.GetCached().Snapshots.Count);
        if (successes == 0)
        {
            Assert.Null(result.RetryAt);
            Assert.Contains("sign in", result.Reason);
            Assert.Contains("refresh", result.Reason);
        }
        else Assert.Null(result.RetryAt);
    }

    [Fact]
    public void RetryingAnExpiredFailureDoesNotRenewItsAuthority()
    {
        var quota = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        var failed = Reading('c', 0, now) with
        {
            Windows = [], Error = "A codex host quota probe failed.", ProbeFailedAt = now
        };
        quota.IngestHostReading(failed);
        _clock.Advance(TimeSpan.FromSeconds(600));
        var first = Select(quota);
        _clock.Advance(TimeSpan.FromMinutes(2));
        quota.IngestHostReading(failed);
        var retry = Select(quota);

        Assert.Equal(SelectionOutcome.Refused, first.Outcome);
        Assert.Equal(first.Outcome, retry.Outcome);
        Assert.Null(first.RetryAt);
        Assert.Null(retry.RetryAt);
        Assert.Equal(now, Assert.Single(quota.GetCached().Snapshots).ProbeFailedAt);
        quota.IngestHostReading(Reading('a', 20, _clock.GetUtcNow().UtcDateTime));
        Assert.Equal(SelectionOutcome.Primary, Select(quota).Outcome);
    }

    [Fact]
    public void ClaimingAnAbandonedSubscriptionDoesNotRefreshItsReading()
    {
        var coordinator = new HostQuotaCoordinator();
        var now = _clock.GetUtcNow().UtcDateTime;
        var key = new string('a', 64);
        var hosts = new HashSet<string> { "host-a" };
        var claim = coordinator.Claim("host-a", new("instance", "codex", key), now, true, hosts);
        Assert.True(coordinator.Report("host-a", new("instance", "codex", key, claim.ProbeId!,
            now, null, null, [new("Weekly", 20, null, null)], null), now, true));
        _clock.Advance(TimeSpan.FromSeconds(600));
        var later = _clock.GetUtcNow().UtcDateTime;
        Assert.True(coordinator.Claim("host-a", new("instance", "codex", key), later, true, hosts).Granted);
        var quota = NewService();
        quota.IngestHostReading(Assert.Single(coordinator.Readings()));

        Assert.Null(quota.GetCachedFor("codex"));
        Assert.Equal(SelectionOutcome.Refused, Select(quota).Outcome);
        Assert.Single(quota.GetCached().Snapshots);
    }

    [Fact]
    public void RoutingEvidenceExpiresAtTheOriginalObservationDeadline()
    {
        var quota = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        quota.IngestHostReading(Reading('a', 20, now));
        var evidence = quota.GetRoutingEvidence("codex");
        Assert.Equal(QuotaRoutingEvidenceKind.CurrentAccount, evidence.Kind);
        Assert.Equal(new string('a', 64), Assert.Single(evidence.Accounts).SubscriptionKey);
        _clock.Advance(TimeSpan.FromSeconds(599));
        Assert.Equal(QuotaRoutingEvidenceKind.CurrentAccount, quota.GetRoutingEvidence("codex").Kind);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(QuotaRoutingEvidenceKind.Missing, quota.GetRoutingEvidence("codex").Kind);
        quota.IngestHostReading(Reading('a', 20, now));
        Assert.Equal(QuotaRoutingEvidenceKind.Missing, quota.GetRoutingEvidence("codex").Kind);
        Assert.Single(quota.GetCached().Snapshots);
        var restored = NewService();
        Assert.Equal(QuotaRoutingEvidenceKind.Missing, restored.GetRoutingEvidence("codex").Kind);
        Assert.Single(restored.GetCached().Snapshots);
    }

    [Fact]
    public void FreshProbeFailureRemainsObservableWithoutProviderWideAuthority()
    {
        var quota = NewService();
        var now = _clock.GetUtcNow().UtcDateTime;
        quota.IngestHostReading(Reading('c', 0, now) with
        {
            Windows = [], Error = "A codex host quota probe failed.", ProbeFailedAt = now
        });
        quota.IngestHostReading(Reading('a', 20, now));
        quota.IngestHostReading(Reading('b', 30, now));
        var evidence = quota.GetRoutingEvidence("codex");
        Assert.Equal(QuotaRoutingEvidenceKind.MultipleAccounts, evidence.Kind);
        Assert.Equal(2, evidence.Accounts.Count);
        Assert.All(evidence.Accounts, account => Assert.Null(account.Error));
        Assert.Null(evidence.Snapshot);
        Assert.Equal(SelectionOutcome.Primary, Select(quota).Outcome);
        Assert.NotNull(Assert.Single(quota.GetCached().Snapshots, account => account.Error is not null));
    }

    [Fact]
    public void MissingAccountEvidenceHasTheSameRecoveryForAHardPin()
    {
        var result = Select(NewService(), SelectionPinMode.Hard);
        Assert.Equal(SelectionOutcome.Refused, result.Outcome);
        Assert.Null(result.RetryAt);
        Assert.Contains("refresh", result.Reason);
    }

    [Fact]
    public void ExplicitAccountSnapshotOverrideDoesNotUseProviderCacheEvidence()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var result = Select(NewService(), snapshotOverride: _ => new QuotaSnapshot
        {
            CliType = "codex", FetchedAt = now,
            SubscriptionKey = new string('a', 64),
            Windows = [new() { Label = "Weekly", UsedPct = 20 }]
        });
        Assert.Equal(SelectionOutcome.Primary, result.Outcome);
        Assert.Null(result.RetryAt);
    }

    [Fact]
    public void ProvidersWithoutHostSubscriptionQuotaDoNotRequireAccountEvidence()
    {
        var evidence = NewService().GetRoutingEvidence(CliTypes.Gemini);
        Assert.Equal(QuotaRoutingEvidenceKind.Unscoped, evidence.Kind);
        Assert.Null(evidence.Snapshot);
        Assert.Empty(evidence.Accounts);
    }

    private SelectionResult Select(QuotaService quota, SelectionPinMode pinMode = SelectionPinMode.Preference,
        Func<string?, QuotaSnapshot?>? snapshotOverride = null)
    {
        var service = new ModelSelectionService(quota,
            new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, _config),
            new CliQuotaFallbackService(_config, NullLogger<CliQuotaFallbackService>.Instance),
            new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, _config),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config),
            timeProvider: _clock);
        return service.Select(new ModelSelectionCall(new SelectionRequest
        {
            Consumer = SelectionConsumers.OneShot("title"),
            IsExplicitPin = true,
            Pins = new SelectionPins("codex", ModelIds.Gpt6Sol, "low", pinMode)
        }, "card-creation", Preview: true, SnapshotOverride: snapshotOverride)).Result;
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
