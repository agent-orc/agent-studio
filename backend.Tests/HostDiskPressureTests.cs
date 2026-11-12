using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using AgentStudio.Pipeline;
using AgentStudio.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostDiskPressureTests
{
    private static readonly HostDiskPressureThresholds Thresholds = new(
        WarningFreeBytes: 300, WarningFreePercent: 15,
        CriticalFreeBytes: 100, CriticalFreePercent: 5);

    [Fact]
    public void Warning_and_critical_name_the_filesystem_and_owned_consumers()
    {
        var warning = HostDiskPressurePolicy.Decide(
            [new("runner work", "/work", 200, 1000,
                new Dictionary<string, long> { ["runner worktrees"] = 500 })], Thresholds);
        Assert.Equal(HostDiskPressureLevel.Warning, warning.Level);
        Assert.Contains("/work", warning.Reason);
        Assert.Contains("runner worktrees", warning.Reason);

        var critical = HostDiskPressurePolicy.Decide(
            [new("instance data", "/data", 50, 1000)], Thresholds);
        Assert.Equal(HostDiskPressureLevel.Critical, critical.Level);
        Assert.Contains("/data", critical.Reason);
    }

    [Fact]
    public void Critical_pause_lifts_only_above_warning()
    {
        var stillPaused = HostDiskPressurePolicy.Decide(
            [new("data", "/data", 200, 1000)], Thresholds, previouslyCritical: true);
        Assert.Equal(HostDiskPressureLevel.Critical, stillPaused.Level);
        var recovered = HostDiskPressurePolicy.Decide(
            [new("data", "/data", 400, 1000)], Thresholds, previouslyCritical: true);
        Assert.Equal(HostDiskPressureLevel.Healthy, recovered.Level);
    }

    [Theory]
    [InlineData("No space left on device")]
    [InlineData("index.lock write error. Out of diskspace")]
    public void Disk_full_claim_write_is_infrastructure(string message)
    {
        Assert.True(HostDiskWriteFailure.IsDiskFull(new IOException(message)));
        Assert.False(HostDiskWriteFailure.IsDiskFull(new IOException("permission denied")));
        var response = LeaseEndpoints.DiskFullClaim("runner-01");
        Assert.Equal(RunnerClaimStatus.Empty, response.Status);
        Assert.Equal("infrastructure:disk-full", response.AdmissionReason);
    }

    [Fact]
    public void Retention_keeps_evidence_referenced_by_a_pending_batch()
    {
        var now = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero))
            .GetUtcNow();
        var state = new BatchGateState("batch-1", "digest", BatchPhase.Published,
            "candidate", 1, now.AddDays(-30));
        Assert.False(HostStorageRetention.CanRemoveBatchEvidence(state, now, 14, referenced: true));
        Assert.True(HostStorageRetention.CanRemoveBatchEvidence(state, now, 14, referenced: false));
        Assert.False(HostStorageRetention.CanRemoveBatchEvidence(
            state with { Phase = BatchPhase.Running }, now, 14, referenced: false));
    }

    [Fact]
    public void Telemetry_warning_and_critical_are_visible_and_recover()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk-pressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
                ["DiskPressure:WarningFreeBytes"] = "300",
                ["DiskPressure:CriticalFreeBytes"] = "100",
                ["DiskPressure:WarningFreePercent"] = "15",
                ["DiskPressure:CriticalFreePercent"] = "5",
            }).Build();
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
            var store = new HostTelemetryStore(config, NullLogger<HostTelemetryStore>.Instance, clock);
            var now = clock.GetUtcNow().UtcDateTime;
            HostTelemetrySample Sample(DateTime at, long free) => new(at,
                null, null, null, null, null, null, null, null, null, null, 4, 0,
                Filesystems: [new("runner work", "/work", free, 1000)]);
            store.Append("host", Sample(now.AddSeconds(-2), 200));
            Assert.Contains(store.Query("host", "1h").Findings,
                finding => finding.Kind == "disk-pressure" && finding.Label == "Disk low");
            store.Append("host", Sample(now.AddSeconds(-1), 50));
            Assert.Equal(HostDiskPressureLevel.Critical,
                store.DiskPressure("host", [new("runner work", "/work", 50, 1000)]).Level);
            Assert.Contains(store.Query("host", "1h").Findings,
                finding => finding.Kind == "disk-pressure" && finding.Label == "Disk critical");
            store.Append("host", Sample(now, 400));
            Assert.Equal(HostDiskPressureLevel.Healthy,
                store.DiskPressure("host", [new("runner work", "/work", 400, 1000)]).Level);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Gate_command_disk_full_is_infrastructure_even_when_test_output_reports_failures()
    {
        var process = new BuildTestGateProcessEvidence
        {
            FailedTestsObserved = true,
            StandardError = "write failed: No space left on device",
            ExitCode = 1,
        };
        Assert.Equal(BuildTestGateFailureKind.DiskFull,
            BuildTestGateRunner.ClassifyFailure(process));
    }
}
