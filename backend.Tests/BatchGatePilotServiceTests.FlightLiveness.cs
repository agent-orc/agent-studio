using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3562: a hung flight frees its slot and a finished flight starts the next batch.</summary>
public sealed class BatchGatePilotFlightLivenessTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Flight_blocked_on_the_reservation_is_cancelled_at_its_deadline_and_the_next_batch_runs()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureOneFlightPerPair(factory, flightLeaseDeadlineMinutes: 5);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var reservations = factory.Services.GetRequiredService<BatchIntegrationReservationService>();
        _gate.Batch = request => Green(request.ExpectedSha);
        // 2 November 2026: a flight waited on the project reservation for
        // hours and only an API restart freed the project slot.
        var held = await reservations.AcquireAsync(ProjectName, CancellationToken.None);
        try
        {
            var first = SeedMember(factory, "CODE-HUNG-A1", "src/hung-a1.cs");
            SeedMember(factory, "CODE-HUNG-A2", "src/hung-a2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            var blocked = Assert.Single(store.ListRecoverableManifests());
            SeedMember(factory, "CODE-HUNG-B1", "src/hung-b1.cs");
            SeedMember(factory, "CODE-HUNG-B2", "src/hung-b2.cs");
            _clock = _clock.AddSeconds(1);
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            var next = Assert.Single(store.ListRecoverableManifests(), manifest =>
                manifest.BatchId != blocked.BatchId
                && store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed);
            Assert.Equal(BatchPhase.Formed, store.LatestState(blocked.BatchId).Phase);
            Assert.Equal(0, _gate.BatchRuns);

            _clock = _clock.AddMinutes(6);
            await pilot.TickAsync(CancellationToken.None);

            var cancelled = store.LatestState(blocked.BatchId);
            Assert.Equal(BatchPhase.Formed, cancelled.Phase);
            Assert.Equal(BatchGateStartPolicy.LeaseDeadlineReason(5), cancelled.Reason);
            var folder = Path.Combine(_watchPath, TaskStates.AutoReview, first.Key);
            var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(folder);
            Assert.Equal(blocked.BatchId, Assert.Single(timeline, entry =>
                entry.Kind == BatchGateStartPolicy.LeaseDeadlineEventKind).Details!["batchId"]);
            var state = System.Text.Json.JsonSerializer.Serialize(pilot.State(ProjectName));
            Assert.Contains("no coordinator lease within 5 minutes", state);

            // The stuck holder lets go: the next batch runs without a restart,
            // and the cancelled batch is retried after it.
            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

            Assert.Equal(BatchPhase.Published, store.LatestState(next.BatchId).Phase);
            Assert.True(Published("CODE-HUNG-A1") && Published("CODE-HUNG-A2"), _errors.ToString());
            Assert.True(_gate.BatchRuns >= 2);
        }
        finally
        {
            held.Dispose();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }

        bool Published(string key) => store.ListManifests().Any(manifest =>
            manifest.Members.Any(member => member.TaskKey == key)
            && store.LatestState(manifest.BatchId).Phase == BatchPhase.Published);
    }

    [Fact]
    public async Task Finished_flight_starts_the_next_formed_batch_without_a_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureOneFlightPerPair(factory, flightLeaseDeadlineMinutes: 40);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        _gate.BatchAsync = async (request, ct) =>
        {
            if (Interlocked.Increment(ref runs) == 1) await release.Task.WaitAsync(ct);
            else secondStarted.TrySetResult();
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "CODE-NEXT-A1", "src/next-a1.cs");
            SeedMember(factory, "CODE-NEXT-A2", "src/next-a2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            var running = Assert.Single(store.ListRecoverableManifests());
            SeedMember(factory, "CODE-NEXT-B1", "src/next-b1.cs");
            SeedMember(factory, "CODE-NEXT-B2", "src/next-b2.cs");
            _clock = _clock.AddSeconds(1);
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            Assert.Single(store.ListRecoverableManifests(), manifest =>
                manifest.BatchId != running.BatchId
                && store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed);

            // No tick follows the release: the finished flight starts the next batch.
            release.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

            Assert.Equal(BatchPhase.Published, store.LatestState(running.BatchId).Phase);
            Assert.All(new[] { "CODE-NEXT-B1", "CODE-NEXT-B2" }, key => Assert.Contains(
                store.ListManifests(), manifest => manifest.Members.Any(member => member.TaskKey == key)
                    && store.LatestState(manifest.BatchId).Phase == BatchPhase.Published));
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    private static void ConfigureOneFlightPerPair(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        int flightLeaseDeadlineMinutes)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: "code", Planner: "threshold", CloseSize: 2,
            MinimumSize: 2, MaximumSize: 2, DeadlineMinutes: 45, PressureSize: 2,
            FlightLeaseDeadlineMinutes: flightLeaseDeadlineMinutes));
    }
}
