using AgentStudio.Watcher;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperationalStallDetectorTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-16T20:00:00Z"));
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void Fleet_alarm_names_dominant_refusal_after_claim_gap_and_host_poll()
    {
        var baseSnapshot = new OperationalStallSnapshot([], [], [], [], [], [], [],
            LegacyModelHosts: [new("agent-runner-01", "coding", "runner-1", Now.AddDays(-8))],
            FleetClaims: new FleetClaimStallFact(["AGT-1", "AGT-2", "AGT-3"],
                Now.AddMinutes(-25), Now.AddMinutes(-15),
                [new("host-capability-missing", Now.AddMinutes(-14), "AGT-1"),
                 new("host-capability-missing", Now.AddMinutes(-13), "AGT-2"),
                 new("project-concurrency-full", Now.AddMinutes(-12), "AGT-3")], true));

        var findings = OperationalStallDetector.Detect(baseSnapshot, new(), Now);
        var warning = Assert.Single(findings, x => x.FingerprintKey.Contains("host-model-upgrade-required"));
        Assert.Contains("upgrade required for model acceptance", warning.Summary);
        var alarm = Assert.Single(findings, x => x.FingerprintKey.Contains("fleet-claim-stall"));
        Assert.Equal("critical", alarm.Severity);
        Assert.Equal("host-capability-missing", alarm.Details["dominantReason"]);
        Assert.Equal(3, alarm.BlockedCount);
        Assert.DoesNotContain(OperationalStallDetector.Detect(baseSnapshot with
        {
            FleetClaims = baseSnapshot.FleetClaims with { LastClaimUtc = Now.AddMinutes(-2) },
        }, new(), Now), x => x.FingerprintKey.Contains("fleet-claim-stall"));
        var unclassified = Assert.Single(OperationalStallDetector.Detect(baseSnapshot with
        {
            LegacyModelHosts = [],
            FleetClaims = baseSnapshot.FleetClaims with { Refusals = [] },
        }, new(), Now));
        Assert.Equal("no refusal recorded", unclassified.Details["dominantReason"]);
    }


    [Fact]
    public void EveningIncident_FindsEachConditionWithImpactAndStableSubjects()
    {
        var facts = new OperationalStallSnapshot(
            [new("agent-runner-01", Now.AddMinutes(-20), 46.7 / 12, 95, 75, 0, 8, "8 builds, 26 timeouts")],
            [new("KORI", "kori-review-01", "review", 3, 15, Now.AddHours(-2), ["AGT-3194"])],
            [new("KORI", "toolchain:compose-render", Now.AddHours(-1), ["AGT-3194"], "unclaimable-plan-requirements")],
            [new("kori-review-01", "codex", "auth-invalid", Now.AddHours(-2), ["AGT-3194"], "CredentialInvalid"),
             new("agent-runner-01", "codex", "rate-limited", Now.AddHours(-11), ["AGT-3176", "AGT-3177"], "rate limit 08:12Z")],
            [new("KORI", "plan", true, 19, Now.AddHours(-79), ["AGT-3097", "AGT-3044"], ["AGT-3097"])],
            [new("KORI", "review", "AGT-3194", Now.AddHours(-3), "stale pipeline gate")],
            [new("engine-host", "engine", "sha-new", "sha-old", Now.AddHours(-1), "ENGINE_SERVER_URL missing")]);

        var findings = OperationalStallDetector.Detect(facts, new OperationalStallThresholds(LaneBudgetHours: 2), Now);

        Assert.Equal(8, findings.Count);
        Assert.Equal(8, findings.Select(x => x.FingerprintKey).Distinct().Count());
        Assert.All(findings, x =>
        {
            Assert.NotNull(x.Subject);
            Assert.NotNull(x.SinceUtc);
            Assert.NotNull(x.SuggestedAction);
            Assert.Contains(x.Severity, new[] { "warning", "critical" });
        });
        Assert.Contains(findings, x => x.FingerprintKey.Contains("host-overload") && x.BlockedCount == 8);
        Assert.Contains(findings, x => x.FingerprintKey.Contains("batch-gate-stuck")
            && x.BlockedCount == 19 && x.Details["completedCount"] == "1");
        Assert.Contains(findings, x => x.FingerprintKey.Contains("deploy-drift")
            && x.Details["logExcerpt"] == "ENGINE_SERVER_URL missing");
    }

    [Fact]
    public void Thresholds_UseTheInjectedClockAndDoNotReportShortOrClearedPressure()
    {
        var facts = new OperationalStallSnapshot(
            [new("host", Now.AddMinutes(-9), 1.2, 90, 50, 0)],
            [new("P", "review-host", "review", 1, 1, Now.AddMinutes(-14), ["AGT-1"])],
            [], [], [], [new("P", "review", "AGT-1", Now.AddHours(-23))], []);
        Assert.Empty(OperationalStallDetector.Detect(facts, new(), Now));
        _clock.Advance(TimeSpan.FromMinutes(2));
        var later = OperationalStallDetector.Detect(facts, new(), Now);
        Assert.Contains(later, x => x.FingerprintKey.Contains("host-overload"));
        Assert.Contains(later, x => x.FingerprintKey.Contains("idle-capacity"));
        Assert.DoesNotContain(later, x => x.FingerprintKey.Contains("lane-stall"));
    }

    [Fact]
    public void SilentReviewContainer_RaisesOneHostEventAndClearsAfterHeartbeat()
    {
        var stale = new HostHeartbeatFact("kori-review-03", "review", Now.AddMinutes(-11), ["AGT-3194"]);
        var snapshot = new OperationalStallSnapshot([], [], [], [], [], [], [], [stale]);
        var eventForHost = Assert.Single(OperationalStallDetector.Detect(snapshot, new(), Now));
        Assert.Contains("host-offline", eventForHost.FingerprintKey);
        Assert.Equal(1, eventForHost.BlockedCount);
        Assert.Equal(WatcherDetectorClasses.Silence, eventForHost.DetectorClass);
        Assert.Empty(OperationalStallDetector.Detect(snapshot with
        {
            Heartbeats = [stale with { LastSeenUtc = Now.AddMinutes(-1) }],
        }, new(), Now));
    }

    [Fact]
    public void ExplicitlyStoppedRunner_RaisesHostEventBeforeHeartbeatTimeout()
    {
        var stopped = new HostHeartbeatFact("agent-runner-01", "coding", Now.AddMinutes(-1), [], Stopped: true);
        var issue = Assert.Single(OperationalStallDetector.Detect(
            new OperationalStallSnapshot([], [], [], [], [], [], [], [stopped]), new(), Now));
        Assert.Contains("is stopped", issue.Summary);
    }

    [Fact]
    public void MemoryPressure_UsesTheConfiguredThreshold()
    {
        var snapshot = new OperationalStallSnapshot(
            [new("agent-runner-01", Now.AddMinutes(-12), null, null, 91, 70000)],
            [], [], [], [], [], []);
        var issue = Assert.Single(OperationalStallDetector.Detect(snapshot, new(), Now));
        Assert.Contains("memory-pressure", issue.FingerprintKey);
        Assert.Equal("91", issue.Details["memoryUsedPercent"]);
        Assert.Empty(OperationalStallDetector.Detect(snapshot,
            new OperationalStallThresholds(MemoryUsedPercent: 95, SwapBytesPerSecond: 80000), Now));
    }

    [Fact]
    public void FingerprintIsDeduplicatedAndClearsOnHealthySweep()
    {
        var root = Path.Combine(Path.GetTempPath(), "operational-stall-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WatcherCaseStore(NullLogger<WatcherCaseStore>.Instance);
            var engine = new WatcherCaseEngine(store,
                new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance));
            var facts = new OperationalStallSnapshot(
                [new("host", Now.AddMinutes(-20), 1.2, 95, 50, 0)], [], [], [], [], [], []);
            var first = OperationalStallDetector.Detect(facts, new(), Now);
            engine.Ingest(root, first, "sweep-1", new WatcherOptions(), Now);
            _clock.Advance(TimeSpan.FromMinutes(5));
            var second = OperationalStallDetector.Detect(facts, new(), Now);
            engine.Ingest(root, second, "sweep-2", new WatcherOptions(), Now);
            Assert.Equal(2, Assert.Single(store.All(root)).SweepCount);
            var changedImpact = second.Select(x => x with { AffectedCards = ["AGT-2"], BlockedCount = 1 }).ToArray();
            engine.Ingest(root, changedImpact, "sweep-2b", new WatcherOptions(), Now);
            Assert.Equal(["AGT-2"], Assert.Single(store.All(root)).AffectedCards);
            Assert.Single(engine.ResolveClearedOperationalCases(root, [], Now));
            Assert.Equal(WatcherCaseStates.Resolved, Assert.Single(store.All(root)).State);
            Assert.Empty(engine.ResolveClearedOperationalCases(root, [], Now));
            engine.Ingest(root, second, "sweep-3", new WatcherOptions(), Now);
            Assert.Equal(WatcherCaseStates.Open, Assert.Single(store.All(root)).State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
