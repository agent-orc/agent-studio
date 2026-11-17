using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3857: a formed batch starts once its lane is idle, a per-task gate
/// never holds the batch reservation, a restarted server starts a formed
/// manifest within one planner tick, and every interval in which a formed
/// batch waited with no batch gate running names its cause.
/// </summary>
public sealed class BatchGatePilotLaneIdleTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Formed_batch_starts_when_the_per_task_holder_releases_the_lane_and_the_wait_names_its_cause()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var reservations = factory.Services.GetRequiredService<BatchIntegrationReservationService>();
        _gate.Batch = request => Green(request.ExpectedSha);
        // A direct per-task integration holds the project reservation through its gate.
        var held = await reservations.AcquireAsync(ProjectName, CancellationToken.None);
        try
        {
            SeedMember(factory, "CODE-IDLE-A1", "src/idle-a1.cs");
            SeedMember(factory, "CODE-IDLE-A2", "src/idle-a2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            var batch = Assert.Single(store.ListRecoverableManifests());
            Assert.Equal(BatchFlightStage.Reserving, store.ReadFlightClaim(ProjectName)!.Stage);

            _clock = _clock.AddMinutes(5);
            await pilot.TickAsync(CancellationToken.None);
            var waiting = pilot.LaneIdle(ProjectName);
            var current = Assert.IsType<BatchLaneIdleCurrent>(waiting.Current);
            Assert.Equal(BatchLaneIdleCauses.PerTaskHolder, current.Cause);
            Assert.Contains("holds the project reservation", current.Detail);
            Assert.Equal(batch.BatchId, current.BatchId);
            Assert.Equal(5, current.Minutes, 3);
            Assert.Contains("\"laneIdle\"", System.Text.Json.JsonSerializer.Serialize(pilot.State(ProjectName)));
            Assert.Equal(0, _gate.BatchRuns);

            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(BatchPhase.Published, store.LatestState(batch.BatchId).Phase);
            Assert.Equal(1, _gate.BatchRuns);
            await pilot.TickAsync(CancellationToken.None);

            var measured = pilot.LaneIdle(ProjectName);
            Assert.Null(measured.Current);
            var interval = Assert.Single(measured.Intervals);
            Assert.Equal(BatchLaneIdleCauses.PerTaskHolder, interval.Cause);
            Assert.Equal(5, interval.Minutes, 3);
            Assert.Equal(5, measured.Last24Hours.PerTaskHolderMinutes, 3);
            Assert.Equal(0, measured.Last24Hours.RestartRecoveryMinutes + measured.Last24Hours.PlannerMinutes);
        }
        finally
        {
            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Per_task_fallback_gate_never_holds_the_batch_reservation_while_it_runs()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var reservations = factory.Services.GetRequiredService<BatchIntegrationReservationService>();
        var releaseFallback = new TaskCompletionSource<BuildTestGateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.FallbackAsync = (_, _) => releaseFallback.Task;

        SeedMember(factory, "CODE-SOLO-RES", "backend/solo-reservation.cs");
        _clock = _clock.AddMinutes(16);
        try
        {
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstFallbackStarted.WaitAsync(TimeSpan.FromSeconds(60));

            using (var grant = reservations.TryAcquire(ProjectName))
                Assert.NotNull(grant);
        }
        finally
        {
            releaseFallback.TrySetResult(Green(_gate.Requests.First(request =>
                request.Executor == "batch-fallback").ExpectedSha));
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Formed_manifest_starts_within_one_tick_after_each_of_two_restarts_and_publishes_once()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var other = new BatchGateStore(Path.Combine(_workspace, "batch-gates"));
        _gate.Batch = request => Green(request.ExpectedSha);
        var starts = 0;
        // Each restart: the server process dies while its flight waits for the
        // reservation and comes back under the same process id, as PID 1 in a
        // container does. Only its start time differs.
        pilot.BeforeReservationForTesting = manifest =>
        {
            if (Interlocked.Increment(ref starts) > 2) return Task.CompletedTask;
            var previous = PreviousInstanceOwner(starts);
            var claim = other.ReadFlightClaim(ProjectName)!;
            Assert.NotNull(other.ExpireFlightClaim(claim, BatchFlightExpiryOutcome.Recover, "process stopped",
                settled: true, _clock, ownerGone: true));
            Assert.NotNull(other.TryClaimFlight(ProjectName, manifest.BatchId, previous, 40, _clock).Claim);
            return Task.CompletedTask;
        };
        SeedMember(factory, "CODE-RESTART-A", "src/restart-a.cs");
        SeedMember(factory, "CODE-RESTART-B", "src/restart-b.cs");
        pilot.RequestCloseNow(ProjectName);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        var batch = Assert.Single(store.ListManifests());
        Assert.Equal(1, starts);

        for (var restart = 1; restart <= 2; restart++)
        {
            Assert.Equal(BatchPhase.Formed, store.LatestState(batch.BatchId).Phase);
            Assert.Equal(PreviousInstanceOwner(restart), store.ReadFlightClaim(ProjectName)!.Owner);
            Assert.Equal(0, _gate.BatchRuns);
            // The stopped process was the last to observe the idle lane.
            other.WriteOpenLaneIdle(new BatchLaneIdleInterval(ProjectName, BatchLaneIdleCauses.PerTaskHolder,
                "seen by the stopped process", batch.BatchId, _clock, _clock, null, PreviousInstanceOwner(restart)));
            // Down for one minute: shorter than the two-minute claim heartbeat.
            _clock = _clock.AddMinutes(1);
            Assert.Equal(BatchLaneIdleCauses.RestartRecovery, pilot.LaneIdle(ProjectName).Current!.Cause);

            await pilot.TickAsync(CancellationToken.None);
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(restart + 1, starts);
        }

        Assert.Equal(BatchPhase.Published, store.LatestState(batch.BatchId).Phase);
        Assert.Equal(pilot.FlightClaimOwner, store.ReadFlightClaim(ProjectName)!.Owner);
        Assert.Equal(1, _gate.BatchRuns);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        Assert.Equal(1, _gate.BatchRuns);
        // One publication: origin carries each member's change exactly once.
        Assert.NotNull(store.ReadPublication(batch.BatchId));
        foreach (var path in new[] { "src/restart-a.cs", "src/restart-b.cs" })
            Assert.Single(Git(_origin, "log", "--format=%H", "develop", "--", path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var measured = pilot.LaneIdle(ProjectName);
        Assert.Null(measured.Current);
        Assert.All(measured.Intervals, interval => Assert.Contains(interval.Cause, BatchLaneIdleCauses.All));
        var restarts = measured.Intervals.Where(interval =>
            interval.Cause == BatchLaneIdleCauses.RestartRecovery).ToArray();
        Assert.True(restarts.Length == 2, System.Text.Json.JsonSerializer.Serialize(measured));
        Assert.All(restarts, interval => Assert.Equal(1, interval.Minutes, 3));
        Assert.Equal(2, measured.Last24Hours.RestartRecoveryMinutes, 3);
    }

    [Fact]
    public void Every_idle_observation_names_one_cause()
    {
        BatchLaneIdleFacts Facts() => new(FormedBatchWaiting: true, BatchGateRunning: false,
            QueuedForGateSlot: false, ReservationWait: false, PreviousProcessClaim: false,
            RecoveryPending: false, PushPending: false, PerTaskGatesRunning: 0);

        Assert.Null(BatchLaneIdlePolicy.Classify(Facts() with { FormedBatchWaiting = false }));
        Assert.Null(BatchLaneIdlePolicy.Classify(Facts() with { BatchGateRunning = true }));
        Assert.Null(BatchLaneIdlePolicy.Classify(Facts() with { BatchGateRunning = true, QueuedForGateSlot = true }));
        Assert.Equal(BatchLaneIdleCauses.PerTaskHolder, BatchLaneIdlePolicy.Classify(Facts() with
        {
            BatchGateRunning = true, QueuedForGateSlot = true, PerTaskGatesRunning = 2,
        })!.Cause);
        Assert.Equal(BatchLaneIdleCauses.PerTaskHolder,
            BatchLaneIdlePolicy.Classify(Facts() with { ReservationWait = true })!.Cause);
        Assert.Contains("push", BatchLaneIdlePolicy.Classify(Facts() with
        {
            ReservationWait = true, PushPending = true,
        })!.Detail);
        Assert.Equal(BatchLaneIdleCauses.PerTaskHolder,
            BatchLaneIdlePolicy.Classify(Facts() with { PerTaskGatesRunning = 1 })!.Cause);
        Assert.Equal(BatchLaneIdleCauses.RestartRecovery, BatchLaneIdlePolicy.Classify(Facts() with
        {
            PreviousProcessClaim = true, ReservationWait = true,
        })!.Cause);
        Assert.Equal(BatchLaneIdleCauses.RestartRecovery,
            BatchLaneIdlePolicy.Classify(Facts() with { RecoveryPending = true })!.Cause);
        Assert.Equal(BatchLaneIdleCauses.Planner, BatchLaneIdlePolicy.Classify(Facts())!.Cause);
        Assert.Equal("the gate host outage holds batch runs", BatchLaneIdlePolicy.Classify(Facts() with
        {
            PlannerHold = "the gate host outage holds batch runs",
        })!.Detail);
    }

    [Fact]
    public void Open_interval_of_a_previous_process_reads_as_restart_recovery_since_its_last_observation()
    {
        var now = _clock;
        var open = new BatchLaneIdleInterval(ProjectName, BatchLaneIdleCauses.PerTaskHolder, "held",
            "batch-1", now.AddMinutes(-30), now.AddMinutes(-10), null, "host/1/1/old");
        var closed = new BatchLaneIdleInterval(ProjectName, BatchLaneIdleCauses.Planner, "tick",
            "batch-0", now.AddDays(-2), now.AddDays(-2).AddMinutes(4), now.AddDays(-2).AddMinutes(4), "host/1/1/old");

        var summary = BatchLaneIdleSummary.From([closed], open, "host/1/2/new", now);

        Assert.Equal(BatchLaneIdleCauses.RestartRecovery, summary.Current!.Cause);
        Assert.Equal(now.AddMinutes(-10), summary.Current.SinceUtc);
        Assert.Equal(20, summary.Last24Hours.PerTaskHolderMinutes, 3);
        Assert.Equal(10, summary.Last24Hours.RestartRecoveryMinutes, 3);
        Assert.Equal(0, summary.Last24Hours.PlannerMinutes);
        Assert.Equal(4, summary.Last7Days.PlannerMinutes, 3);
        Assert.Equal(3, summary.Intervals.Count);
        Assert.All(summary.Intervals, interval => Assert.Contains(interval.Cause, BatchLaneIdleCauses.All));
    }

    private static string PreviousInstanceOwner(int restart)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var started = process.StartTime.ToUniversalTime().AddMinutes(-restart).Ticks;
        return $"{Environment.MachineName}/{Environment.ProcessId}/{started}/previous-instance-{restart}";
    }

    private static void ConfigureFlights(WebApplicationFactory<Program> factory)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: "code", Planner: "threshold", CloseSize: 2,
            MinimumSize: 2, MaximumSize: 2, DeadlineMinutes: 45, PressureSize: 2,
            FlightLeaseDeadlineMinutes: 40));
    }
}
