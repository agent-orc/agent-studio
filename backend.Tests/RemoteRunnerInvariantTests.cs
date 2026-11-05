extern alias Runner;

using RInventory = Runner::AgentRunner.RunnerProcessInventoryTracker;
using RLoadGate = Runner::AgentRunner.RunnerLoadGate;
using RAction = Runner::AgentRunner.RunnerReconciliationAction;
using RTelemetry = Runner::AgentRunner.HostTelemetrySample;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteRunnerInvariantTests
{
    [Fact]
    public void Deleted_cwd_is_terminated_and_reported_once()
    {
        var killed = new List<int>();
        var now = new DateTime(2026, 7, 25, 0, 40, 0, DateTimeKind.Utc);
        var inventory = new RInventory(
            cwdResolver: pid => pid == 4242
                ? "/worktrees/AGT-2321 (deleted)"
                : null,
            kill: pid => killed.Add(pid),
            utcNow: () => now);
        using var registration = inventory.Track(
            "run-1", "AGT-2321", "/worktrees/AGT-2321");
        inventory.AttachProcess("run-1", 4242);

        var snapshot = inventory.Snapshot();

        Assert.Equal([4242], killed);
        Assert.Empty(snapshot.Processes);
        var report = Assert.Single(snapshot.Reports!);
        Assert.Equal("worktree-hygiene", report.Category);
        Assert.Equal("terminated-deleted-cwd", report.Action);
        inventory.AcknowledgeReports(snapshot);
        Assert.Empty(inventory.Snapshot().Reports!);
    }

    [Fact]
    public void Load_gate_stops_only_new_claims_after_sustained_normalized_load()
    {
        var gate = new RLoadGate(
            threshold: 1.5,
            sustainedFor: TimeSpan.FromMinutes(2));
        var started = new DateTime(
            2026, 7, 25, 0, 40, 0, DateTimeKind.Utc);
        var sample = new RTelemetry(
            started,
            CpuPercent: 95,
            Load1: 16,
            Load5: 14,
            Load15: 12,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: null,
            IoWaitPercent: null,
            CpuCores: 8,
            ActiveSlots: 2);

        Assert.False(gate.Observe(sample, started).Throttle);
        Assert.False(gate.Observe(
            null,
            started.AddSeconds(30)).Throttle);
        var tripped = gate.Observe(
            sample,
            started.AddMinutes(2).AddSeconds(1));
        Assert.True(tripped.Throttle);
        Assert.True(tripped.EmitEvent);
        Assert.Equal(2, tripped.LoadPerCore);
        Assert.Equal(started.AddMinutes(2).AddSeconds(1), tripped.Since);
        Assert.False(gate.Observe(
            sample,
            started.AddMinutes(3)).EmitEvent);

        var cooled = gate.Observe(
            sample with { Load1 = 4 },
            started.AddMinutes(4));
        Assert.False(cooled.Throttle);
        Assert.True(cooled.EmitOpenEvent);
        Assert.Equal(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(59)), cooled.SustainedFor);
        Assert.Equal(started.AddMinutes(4), cooled.Since);
    }

    [Fact]
    public void Orphan_action_kills_only_the_tracked_pid_for_the_same_run()
    {
        var killed = new List<int>();
        var inventory = new RInventory(
            cwdResolver: _ => "/worktrees/active",
            kill: pid => killed.Add(pid));
        using var registration = inventory.Track(
            "run-current", "AGT-2321", "/worktrees/active");
        inventory.AttachProcess("run-current", 5002);

        inventory.Apply([
            new RAction(
                "action-stale",
                "run-inventory",
                "terminate-process",
                "stale pid",
                Pid: 5001,
                RunId: "run-current",
                TaskKey: "AGT-2321"),
            new RAction(
                "action-matching",
                "run-inventory",
                "terminate-process",
                "orphan pid",
                Pid: 5002,
                RunId: "run-current",
                TaskKey: "AGT-2321")
        ]);

        Assert.Equal([5002], killed);
        var snapshot = inventory.Snapshot();
        Assert.Empty(snapshot.Processes);
        Assert.Equal(
            ["action-matching", "action-stale"],
            snapshot.AcknowledgedActionIds);
        var report = Assert.Single(snapshot.Reports!);
        Assert.Equal("terminated-orphan-process", report.Action);
    }
}
