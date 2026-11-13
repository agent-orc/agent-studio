using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3569: a batch flight runs on a durable claim. The claim is written
/// before the reservation wait, expires to a retry or a visible gate without a
/// restart, a stale holder can neither touch the candidate nor publish, and a
/// second host over the same store never flies a claimed batch.
/// </summary>
public sealed class BatchGatePilotFlightClaimTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Flight_claims_its_batch_durably_before_the_reservation_wait()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 5);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var reservations = factory.Services.GetRequiredService<BatchIntegrationReservationService>();
        _gate.Batch = request => Green(request.ExpectedSha);
        var held = await reservations.AcquireAsync(ProjectName, CancellationToken.None);
        try
        {
            SeedMember(factory, "CODE-CLAIM-A1", "src/claim-a1.cs");
            SeedMember(factory, "CODE-CLAIM-A2", "src/claim-a2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);

            var batch = Assert.Single(store.ListRecoverableManifests());
            var claim = Assert.IsType<BatchFlightClaim>(store.ReadFlightClaim(ProjectName));
            Assert.Equal(batch.BatchId, claim.BatchId);
            Assert.Equal(pilot.FlightClaimOwner, claim.Owner);
            Assert.Equal(BatchFlightStage.Reserving, claim.Stage);
            Assert.Equal(_clock.AddMinutes(5), claim.StageDeadlineUtc);
            Assert.Null(claim.End);
            var journal = store.StateHistory(batch.BatchId)[^1];
            Assert.Equal(BatchPhase.Formed, journal.Phase);
            Assert.Equal(claim.ClaimId, journal.FlightClaimId);
            Assert.Contains("\"flightClaim\"", System.Text.Json.JsonSerializer.Serialize(pilot.State(ProjectName)));

            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

            Assert.Equal(BatchPhase.Published, store.LatestState(batch.BatchId).Phase);
            var released = store.ReadFlightClaim(ProjectName)!;
            Assert.True(released.SameGrant(claim));
            Assert.Equal(new BatchFlightClaimEnd(released.End!.AtUtc, "flight finished",
                SettledAtUtc: released.End.AtUtc), released.End);
        }
        finally
        {
            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hung_reservation_retries_once_then_pauses_to_a_visible_gate_without_a_restart()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 5);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var reservations = factory.Services.GetRequiredService<BatchIntegrationReservationService>();
        _gate.Batch = request => Green(request.ExpectedSha);
        var held = await reservations.AcquireAsync(ProjectName, CancellationToken.None);
        try
        {
            SeedMember(factory, "CODE-RETRY-A1", "src/retry-a1.cs");
            SeedMember(factory, "CODE-RETRY-A2", "src/retry-a2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            var batch = Assert.Single(store.ListRecoverableManifests());

            _clock = _clock.AddMinutes(6);
            await pilot.TickAsync(CancellationToken.None);

            // The batch stays formed; the same tick may already have claimed it again.
            Assert.Equal(BatchPhase.Formed, store.LatestState(batch.BatchId).Phase);
            var retried = Assert.Single(store.StateHistory(batch.BatchId), state => state.FlightClaimExpiry is not null);
            Assert.Equal(BatchPhase.Formed, retried.Phase);
            Assert.Equal(BatchGateStartPolicy.LeaseDeadlineReason(5), retried.Reason);
            Assert.Equal(BatchFlightClaimExpiry.StageDeadlinePassed, retried.FlightClaimExpiry);
            // The same batch flies again on a new claim once its old flight unwound.
            var second = await NextClaimAsync(pilot, store, fence: 2);
            Assert.Equal(batch.BatchId, second.BatchId);

            _clock = _clock.AddMinutes(6);
            await pilot.TickAsync(CancellationToken.None);

            var paused = Assert.Single(store.StateHistory(batch.BatchId), state => state.Phase == BatchPhase.Paused);
            Assert.Equal(BatchFlightClaimExpiry.StageDeadlinePassed, paused.FlightClaimExpiry);
            Assert.Contains("retry budget spent", paused.Reason);
            var expired = store.ReadFlightClaim(ProjectName)!;
            Assert.Equal(BatchFlightExpiryOutcome.VisibleGate, expired.End!.Outcome);
            Assert.True(expired.End.Settled);
            // The pause hands the members to the per-task gate with a visible cause.
            Assert.StartsWith("paused-to-per-task-gate: flight claim expired",
                store.LatestState(batch.BatchId).Reason);
        }
        finally
        {
            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hung_gate_expires_to_a_retry_and_its_stale_flight_writes_nothing()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 40);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        _gate.BatchAsync = async (request, _) =>
        {
            // The first gate hangs and ignores its cancellation.
            if (Interlocked.Increment(ref runs) == 1) await hang.Task;
            return Green(request.ExpectedSha);
        };
        var originTip = Git(_origin, "rev-parse", "develop");
        try
        {
            SeedMember(factory, "CODE-GATEHANG-1", "src/gatehang-1.cs");
            SeedMember(factory, "CODE-GATEHANG-2", "src/gatehang-2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            var hung = Assert.Single(store.ListRecoverableManifests());
            Assert.Equal(BatchFlightStage.Gating, store.ReadFlightClaim(ProjectName)!.Stage);

            // Past the gate deadline: gate timeout 150 minutes plus the grace.
            _clock = _clock.AddMinutes(150 + BatchFlightClaimPolicy.GatingGraceMinutes + 1);
            await pilot.TickAsync(CancellationToken.None);

            var retired = store.LatestState(hung.BatchId);
            Assert.Equal(BatchPhase.Abandoned, retired.Phase);
            Assert.Equal(BatchFlightClaimExpiry.StageDeadlinePassed, retired.FlightClaimExpiry);
            Assert.Contains("gate step", retired.Reason);
            Assert.Equal(originTip, Git(_origin, "rev-parse", "develop"));

            // The members fly again in a new batch and publish; no restart.
            for (var attempt = 0; attempt < 200 && !Published("CODE-GATEHANG-1"); attempt++)
            {
                pilot.RequestCloseNow(ProjectName);
                await pilot.TickAsync(CancellationToken.None);
                await Task.Delay(50);
            }
            Assert.True(Published("CODE-GATEHANG-1") && Published("CODE-GATEHANG-2"), _errors.ToString());

            // The stale gate returns green; its flight records no verdict and publishes nothing.
            var publishedTip = Git(_origin, "rev-parse", "develop");
            hang.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(BatchPhase.Abandoned, store.LatestState(hung.BatchId).Phase);
            var run = Assert.Single(store.ListRuns(hung.BatchId));
            Assert.Null(store.ReadVerdict(hung.BatchId, run.BatchRunId));
            Assert.Null(store.ReadPublication(hung.BatchId));
            Assert.Equal(publishedTip, Git(_origin, "rev-parse", "develop"));
        }
        finally
        {
            hang.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }

        bool Published(string key) => store.ListManifests().Any(manifest =>
            manifest.Members.Any(member => member.TaskKey == key)
            && store.LatestState(manifest.BatchId).Phase == BatchPhase.Published);
    }

    [Fact]
    public async Task Flight_whose_claim_another_process_took_over_during_the_gate_does_not_publish()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 40);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var other = new BatchGateStore(Path.Combine(_workspace, "batch-gates"));
        _gate.BatchAsync = (request, _) =>
        {
            TakeOver(other, request.LedgerBatchId!);
            return Task.FromResult(Green(request.ExpectedSha));
        };
        var originTip = Git(_origin, "rev-parse", "develop");

        SeedMember(factory, "CODE-STALE-1", "src/stale-1.cs");
        SeedMember(factory, "CODE-STALE-2", "src/stale-2.cs");
        pilot.RequestCloseNow(ProjectName);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

        var batch = Assert.Single(store.ListManifests());
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(BatchPhase.Running, store.LatestState(batch.BatchId).Phase);
        var run = Assert.Single(store.ListRuns(batch.BatchId));
        Assert.Null(store.ReadVerdict(batch.BatchId, run.BatchRunId));
        Assert.Null(store.ReadPublication(batch.BatchId));
        Assert.Equal(originTip, Git(_origin, "rev-parse", "develop"));
        Assert.Equal("other-host", store.ReadFlightClaim(ProjectName)!.Owner);
    }

    [Fact]
    public async Task Flight_whose_claim_was_taken_over_while_it_waited_never_touches_the_candidate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 40);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var other = new BatchGateStore(Path.Combine(_workspace, "batch-gates"));
        _gate.Batch = request => Green(request.ExpectedSha);
        pilot.BeforeReservationForTesting = manifest =>
        {
            TakeOver(other, manifest.BatchId);
            return Task.CompletedTask;
        };

        SeedMember(factory, "CODE-NOCAND-1", "src/nocand-1.cs");
        SeedMember(factory, "CODE-NOCAND-2", "src/nocand-2.cs");
        pilot.RequestCloseNow(ProjectName);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

        var batch = Assert.Single(store.ListManifests());
        Assert.Equal(BatchPhase.Formed, store.LatestState(batch.BatchId).Phase);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.All(batch.Members, member => Assert.Null(store.TryReadReplay(batch.BatchId, member.TaskKey)));
        Assert.Empty(Git(_repo, "for-each-ref", "refs/agent-studio/batch-candidates/" + batch.BatchId));
    }

    [Fact]
    public async Task Second_host_never_flies_a_claimed_batch_and_takes_it_over_after_the_deadline()
    {
        using var first = BuildFactory();
        _ = first.CreateClient();
        ConfigureFlights(first, reservationDeadlineMinutes: 5);
        var firstPilot = first.Services.GetRequiredService<BatchGatePilotService>();
        firstPilot.WaitForFlightsInTick = false;
        var store = first.Services.GetRequiredService<BatchGateStore>();
        _gate.Batch = request => Green(request.ExpectedSha);
        // The first host's flight hangs before its reservation and ignores cancellation.
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstPilot.BeforeReservationForTesting = _ => hang.Task;
        SeedMember(first, "CODE-HOSTS-1", "src/hosts-1.cs");
        SeedMember(first, "CODE-HOSTS-2", "src/hosts-2.cs");
        firstPilot.RequestCloseNow(ProjectName);
        await firstPilot.TickAsync(CancellationToken.None);
        var batch = Assert.Single(store.ListRecoverableManifests());
        var firstClaim = store.ReadFlightClaim(ProjectName)!;
        Assert.Equal(firstPilot.FlightClaimOwner, firstClaim.Owner);

        // A second host over the same workspace: separate memory, shared store.
        using var second = BuildFactory();
        _ = second.CreateClient();
        ConfigureFlights(second, reservationDeadlineMinutes: 5);
        var secondPilot = second.Services.GetRequiredService<BatchGatePilotService>();
        Assert.NotEqual(firstPilot.FlightClaimOwner, secondPilot.FlightClaimOwner);
        try
        {
            await secondPilot.TickAsync(CancellationToken.None);

            Assert.Equal(0, _gate.BatchRuns);
            Assert.Equal(BatchPhase.Formed, store.LatestState(batch.BatchId).Phase);
            Assert.Equal(firstClaim, store.ReadFlightClaim(ProjectName));

            // Past the reservation deadline the second host expires the claim and flies the batch.
            _clock = _clock.AddMinutes(6);
            await secondPilot.TickAsync(CancellationToken.None);
            await secondPilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(BatchPhase.Published, store.LatestState(batch.BatchId).Phase);
            Assert.Equal(1, _gate.BatchRuns);
            var claims = store.StateHistory(batch.BatchId).Where(state => state.FlightClaimId is not null).ToArray();
            Assert.Equal(2, claims.Length);
            var expiry = Assert.Single(store.StateHistory(batch.BatchId), state => state.FlightClaimExpiry is not null);
            Assert.Equal(BatchGateStartPolicy.LeaseDeadlineReason(5), expiry.Reason);
            var final = store.ReadFlightClaim(ProjectName)!;
            Assert.Equal(secondPilot.FlightClaimOwner, final.Owner);
            Assert.Equal(firstClaim.Fence + 1, final.Fence);

            // The first host's flight wakes after the takeover and changes nothing.
            var history = store.StateHistory(batch.BatchId).Count;
            hang.TrySetResult();
            await firstPilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(history, store.StateHistory(batch.BatchId).Count);
            Assert.Equal(1, _gate.BatchRuns);
            Assert.Equal(final, store.ReadFlightClaim(ProjectName));
        }
        finally
        {
            hang.TrySetResult();
            await firstPilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
            await secondPilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restart_frees_the_claim_of_an_exited_owner_without_waiting_for_its_heartbeat()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureFlights(factory, reservationDeadlineMinutes: 40);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var other = new BatchGateStore(Path.Combine(_workspace, "batch-gates"));
        _gate.Batch = request => Green(request.ExpectedSha);
        var exited = ExitedProcessId();
        // The claim the batch's first flight wrote passes to an owner that then exits.
        pilot.BeforeReservationForTesting = manifest =>
        {
            TakeOver(other, manifest.BatchId, $"{Environment.MachineName}/{exited}/previous-instance",
                advanceClock: false);
            return Task.CompletedTask;
        };
        SeedMember(factory, "CODE-RESTART-1", "src/restart-1.cs");
        SeedMember(factory, "CODE-RESTART-2", "src/restart-2.cs");
        pilot.RequestCloseNow(ProjectName);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        var batch = Assert.Single(store.ListManifests());
        Assert.Equal(BatchPhase.Formed, store.LatestState(batch.BatchId).Phase);
        Assert.Contains($"/{exited}/", store.ReadFlightClaim(ProjectName)!.Owner);
        pilot.BeforeReservationForTesting = null;
        var clock = _clock;

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(clock, _clock);
        Assert.Equal(BatchPhase.Published, store.LatestState(batch.BatchId).Phase);
        Assert.Equal(pilot.FlightClaimOwner, store.ReadFlightClaim(ProjectName)!.Owner);
    }

    /// <summary>
    /// Another process expires the current claim after its heartbeat lapsed
    /// and claims the slot itself, as a takeover by a second host does.
    /// </summary>
    private void TakeOver(BatchGateStore other, string batchId, string owner = "other-host",
        bool advanceClock = true)
    {
        var current = other.ReadFlightClaim(ProjectName)!;
        Assert.Equal(batchId, current.BatchId);
        if (advanceClock) _clock = _clock.AddMinutes(3);
        Assert.NotNull(other.ExpireFlightClaim(current, BatchFlightExpiryOutcome.Recover, "taken over",
            settled: true, _clock, ownerGone: !advanceClock));
        Assert.NotNull(other.TryClaimFlight(ProjectName, batchId, owner, 40, _clock).Claim);
    }

    private static int ExitedProcessId()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("true")
        {
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        return process.Id;
    }

    private async Task<BatchFlightClaim> NextClaimAsync(BatchGatePilotService pilot, BatchGateStore store, long fence)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (store.ReadFlightClaim(ProjectName) is { End: null } claim && claim.Fence == fence) return claim;
            await Task.Delay(50);
            await pilot.TickAsync(CancellationToken.None);
        }
        throw new TimeoutException("No new flight claim. " + _errors);
    }

    private static void ConfigureFlights(WebApplicationFactory<Program> factory, int reservationDeadlineMinutes)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: "code", Planner: "threshold", CloseSize: 2,
            MinimumSize: 2, MaximumSize: 2, DeadlineMinutes: 45, PressureSize: 2,
            FlightLeaseDeadlineMinutes: reservationDeadlineMinutes));
    }
}
