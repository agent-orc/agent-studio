using AgentStudio.Cli;
using AgentStudio.Operator;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorPressureTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 11, 40, 0, DateTimeKind.Utc);
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Full_swap_and_io_wait_are_visible_beside_nominal_slots()
    {
        var reading = Sample() with
        {
            Load1 = 311, MemoryUsedBytes = 48 * GiB, MemoryTotalBytes = 48 * GiB,
            SwapUsedBytes = 16 * GiB, SwapTotalBytes = 16 * GiB,
            SwapInBytesPerSecond = 524288, SwapOutBytesPerSecond = 262144, IoWaitPercent = 38,
        };
        var row = Host("coding-a", "coding", reading, 8);

        Assert.Equal(311d / 12, row.LoadPerCore);
        Assert.Equal(row.MemoryTotalBytes, row.MemoryUsedBytes);
        Assert.Equal(row.SwapTotalBytes, row.SwapUsedBytes);
        Assert.Equal(16 * GiB, row.SwapUsedBytes);
        Assert.Equal(38, row.IoWaitPercent);
        Assert.Equal(8, row.EffectiveSlots);
        Assert.Equal(2, row.OccupiedSlots);
        Assert.Equal(reading.ObservedAt, row.ObservedAt);
        var ready = Assert.Single(row.ReadyWork);
        Assert.Equal(180, ready.OldestAgeSeconds);
    }

    [Fact]
    public void Busy_coding_and_idle_review_have_separate_role_observations()
    {
        var coding = Host("coding-a", "coding", Sample() with
        {
            Load1 = 36, CpuPercent = 99, IoWaitPercent = 1, SwapInBytesPerSecond = 0,
            ClaimLoadGate = new("closed", 3, 2, 12, Now.AddSeconds(-12), 2),
        }, 8);
        var review = Host("review-b", "review", Sample() with { Load1 = 0.6, CpuPercent = 2, ActiveSlots = 0 }, 3);

        Assert.Equal(3d, coding.LoadPerCore);
        Assert.Equal("host-load-gate", coding.ClaimHold?.Code);
        Assert.Equal(0, coding.SwapInBytesPerSecond);
        Assert.Null(coding.SwapUsedBytes);
        Assert.Equal("review-b", review.HostId);
        Assert.Equal(0, review.OccupiedSlots);
        Assert.Equal(0.05, review.LoadPerCore!.Value, 8);
        Assert.Null(review.ClaimHold);
        Assert.Null(review.ClaimLoadGate);
    }

    [Fact]
    public void Disk_admission_explains_an_empty_refusal_and_missing_telemetry_has_no_health_claim()
    {
        var reading = Sample() with { Filesystems = [new("runner work", "/work", 5 * GiB, 100 * GiB)] };
        var disk = Host("coding-a", "coding", reading, 8,
            new("host-disk-pressure", "", Now, "last-claim-refusal"));
        var missing = Host("gate-c", "gate", null, null);
        var stale = Host("review-b", "review", Sample() with { ObservedAt = Now.AddMinutes(-16) }, 3);

        Assert.Equal("critical", disk.DiskLevel);
        var filesystem = Assert.Single(disk.Disk!);
        Assert.Equal((5 * GiB, 100 * GiB, 5d), (filesystem.FreeBytes, filesystem.TotalBytes, filesystem.FreePercent));
        Assert.Equal("last-claim-refusal", disk.ClaimHold?.Source);
        Assert.Contains("file system", disk.ClaimHold!.Reason);
        Assert.Equal("unreported", missing.TelemetryState);
        Assert.Equal("unknown", missing.DiskLevel);
        Assert.Null(missing.OccupiedSlots);
        Assert.Null(missing.ClaimHold);
        Assert.Equal("stale", stale.TelemetryState);
        Assert.Equal("unknown", stale.DiskLevel);
        Assert.Null(stale.LoadPerCore);
        Assert.Null(stale.OccupiedSlots);
    }

    [Fact]
    public void Weekly_pacing_keeps_failed_subscriptions_and_their_receipts_separate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"operator-pressure-{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
            caps.SetCap("codex", "Weekly", 99);
            var reset = Now.AddHours(12);
            var first = new QuotaSnapshot { CliType = "codex", SubscriptionKey = "a", FetchedAt = Now,
                Source = "host /status", Windows = [new() { Label = "Weekly", UsedPct = 94, ResetAt = reset }] };
            var failed = first with { SubscriptionKey = "b", FetchedAt = Now.AddHours(-2), Error = "failed",
                ProbeFailedAt = Now.AddMinutes(-2) };
            var labelOnly = first with { SubscriptionKey = "c", Windows =
                [new() { Label = "Weekly", UsedPct = 91, ResetLabel = "Dec 4" }] };
            var plan = QuotaAdmissionPlanner.Plan("codex", "gpt-6-sol", "medium", null, caps,
                cli => cli == "codex" ? first : null, Now, occupiedSlots: 2);
            Assert.Equal(QuotaAdmissionOutcome.Throttle, plan.Outcome);
            var receipt = new ModelSelectionEvent { At = Now.AddMinutes(-1), Kind = "waiting", Consumer = "coding",
                AdmissionOutcome = "throttle", Quota = new("codex", "Weekly", 94, 99, null, reset, null)
                { SubscriptionKey = "a" } };
            var fallback = receipt with { Id = "fallback-fixture", Kind = "switched", AdmissionOutcome = "fallback",
                At = Now.AddMinutes(-4), To = new("claude", "claude-sonnet-5", "medium") };
            var history = new QuotaHistoryPoint[]
            {
                new(Now.AddHours(-1), "Weekly", 90, reset, "a"), new(Now, "Weekly", 94, reset, "a"),
                new(Now.AddMinutes(-45), "Weekly", 0, reset, "b"),
            };
            var rows = OperatorPressureProjection.Quota(new() { TtlSeconds = 600, Snapshots = [failed, first, labelOnly] }, caps,
                new ModelSelectionPolicy { ShortStepReservePoints = 0 }, (_, _) => history, [receipt, fallback], [fallback], Now);

            var current = Assert.Single(rows, row => row.SubscriptionKey == "a");
            Assert.Equal("fresh", current.State);
            Assert.Equal(94, current.UsedPercent);
            Assert.Equal(99, current.CapPercent);
            Assert.Equal(0, current.ReservePoints);
            Assert.True(current.PacingTargetPercent < current.UsedPercent);
            Assert.Equal(4, current.ObservedRatePercentPerHour);
            Assert.Equal(reset, current.ResetAt);
            Assert.Equal("host /status", current.ResetSource);
            Assert.Equal("throttle", current.LastAdmission?.Outcome);
            Assert.Equal("claude-sonnet-5", current.AcceptedFallback?.Model);
            var held = Assert.Single(rows, row => row.SubscriptionKey == "b");
            Assert.Equal("failed", held.State);
            Assert.Equal(failed.ProbeFailedAt, held.ProbeFailedAt);
            Assert.Null(held.UsedPercent);
            Assert.Null(held.ObservedRatePercentPerHour);
            Assert.Null(held.LastAdmission);
            Assert.Null(held.ResetAt);
            var label = Assert.Single(rows, row => row.SubscriptionKey == "c");
            Assert.Equal("Dec 4", label.ResetLabel);
            Assert.Null(label.ResetAt);
            Assert.Null(label.PaceReleaseAt);
            Assert.Null(label.PacingTargetPercent);
            var card = new TaskInfo { ProjectName = "fixture", Id = "ready", State = TaskStates.Ready, CreatedAt = Now, EnteredLaneAt = Now };
            var route = new SelectionRoute("codex", "gpt-6-sol", "medium");
            var projection = new CapacityForecast(route, plan, QuotaSubscriptionKey: "a");
            var firstLaunch = OperatorPressureReader.CodingAdmission(card, projection, rows, Now, scoped: false);
            Assert.Equal(("first-launch", false, "throttle"), (firstLaunch.Class, firstLaunch.HardPinned, firstLaunch.Outcome));
            Assert.Equal(reset, firstLaunch.RetryAt);
            var labelLaunch = OperatorPressureReader.CodingAdmission(card,
                projection with { QuotaSubscriptionKey = "c" }, rows, Now, scoped: false);
            Assert.Null(labelLaunch.RetryAt);
            var recovery = OperatorPressureReader.CodingAdmission(card with { SessionName = "saved-session" },
                projection with { PinMode = SelectionPinMode.Hard }, rows, Now, scoped: true);
            Assert.Equal("recovery", recovery.Class);
            Assert.True(recovery.HardPinned);
            Assert.Null(recovery.RetryAt);
            Assert.Null(recovery.Reason);

            var timedPace = new QuotaPace("useBy", Now.AddHours(11));
            var timed = OperatorPressureProjection.Quota(new() { TtlSeconds = 600, Snapshots = [first] }, caps,
                new ModelSelectionPolicy { ShortStepReservePoints = 0, WindowPaces = [new("codex", "Weekly", timedPace)] },
                (_, _) => history, [], [], Now);
            var release = Assert.Single(timed).PaceReleaseAt;
            Assert.Equal(SelectionQuotaMath.Project(first.Windows[0], Now, 99, timedPace)?.FreeAt, release);
            Assert.True(release > Now && release < reset);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HostTelemetrySnapshotDto Sample() => new(Now.AddSeconds(-30), 70, 1, null, null,
        32 * GiB, 48 * GiB, null, null, null, null, 12, 2, Filesystems: [new("work", "/work", 40 * GiB, 100 * GiB)]);

    private static CapacityHostPressure Host(string id, string role, HostTelemetrySnapshotDto? reading, int? slots,
        CapacityClaimHold? hold = null)
        => OperatorPressureProjection.Host(id, id, role, "fixture", reading?.ObservedAt, reading, reading?.ObservedAt,
            slots, reading?.ActiveSlots, [OperatorPressureProjection.Queue(role, "fixture", [Now.AddMinutes(-3)], Now)],
            Now, disks => HostDiskPressurePolicy.Decide(disks, new()), hold);
}
