using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateWaitingBatchTests : BatchGatePilotServiceTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Three_cards_arriving_behind_a_busy_project_gate_share_one_formed_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureThresholdWaiting(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "CODE-BUSY-1", "src/busy-1.cs");
            SeedMember(factory, "CODE-BUSY-2", "src/busy-2.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;
            var running = Assert.Single(store.ListRecoverableManifests());
            Assert.Equal(BatchPhase.Running, store.LatestState(running.BatchId).Phase);

            // Seed reviews in the past without advancing the lease clock past
            // its two-minute TTL while the fake gate is still running.
            var gateStartedAt = _clock;
            _clock = gateStartedAt.AddMinutes(-46);
            SeedMember(factory, "CODE-WAIT-1", "src/wait-1.cs");
            _clock = gateStartedAt.AddSeconds(1);
            await pilot.TickAsync(CancellationToken.None);
            Assert.DoesNotContain(store.ListRecoverableManifests(), manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed);

            _clock = gateStartedAt.AddMinutes(-46).AddSeconds(1);
            SeedMember(factory, "CODE-WAIT-2", "src/wait-2.cs");
            _clock = gateStartedAt.AddSeconds(2);
            await pilot.TickAsync(CancellationToken.None);
            var initialWait = Assert.Single(store.ListRecoverableManifests().Where(manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed));
            Assert.Equal(["CODE-WAIT-1", "CODE-WAIT-2"],
                initialWait.Members.Select(member => member.TaskKey));
            _clock = gateStartedAt.AddMinutes(-46).AddSeconds(2);
            SeedMember(factory, "CODE-WAIT-3", "src/wait-3.cs");
            _clock = gateStartedAt.AddSeconds(3);
            await pilot.TickAsync(CancellationToken.None);
            Assert.Equal(BatchPhase.Running, store.LatestState(running.BatchId).Phase);

            var formed = Assert.Single(store.ListRecoverableManifests().Where(manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed));
            Assert.Equal(["CODE-WAIT-1", "CODE-WAIT-2", "CODE-WAIT-3"],
                formed.Members.Select(member => member.TaskKey));
            Assert.DoesNotContain(formed.Members, member =>
                member.TaskKey.StartsWith("CODE-BUSY-", StringComparison.Ordinal));
            Assert.Equal(1, _gate.BatchRuns);
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Separate_overlap_keeps_shared_file_out_and_records_the_file_on_batch_state()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureThresholdWaiting(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "CODE-OVERLAP-BUSY-1", "src/busy-a.cs");
            SeedMember(factory, "CODE-OVERLAP-BUSY-2", "src/busy-b.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted;

            SeedMember(factory, "CODE-OVERLAP-A", "src/shared.cs");
            SeedMember(factory, "CODE-OVERLAP-B", "src/other.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            SeedMember(factory, "CODE-OVERLAP-C", "src/shared.cs");
            SeedMember(factory, "CODE-OVERLAP-D", "src/fourth.cs");
            _clock = _clock.AddSeconds(1);
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);

            var separate = store.ListManifests().Single(manifest =>
                manifest.Members.Any(member => member.TaskKey == "CODE-OVERLAP-C")
                && manifest.Members.Any(member => member.TaskKey == "CODE-OVERLAP-D"));
            Assert.Contains("overlap separate", separate.FormationReason);
            Assert.Contains("src/shared.cs", separate.FormationReason);
            var formed = Assert.Single(store.ListRecoverableManifests().Where(manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed));
            Assert.Contains(formed.Members, member => member.TaskKey == "CODE-OVERLAP-A");
            Assert.DoesNotContain(formed.Members, member => member.TaskKey == "CODE-OVERLAP-C");
            Assert.DoesNotContain(formed.Members, member =>
                member.TaskKey.StartsWith("CODE-OVERLAP-BUSY-", StringComparison.Ordinal));
            var excluded = Assert.Single(formed.Exclusions, exclusion =>
                exclusion.TaskKey == "CODE-OVERLAP-C");
            Assert.Equal(BatchExclusionReason.OverlapSeparate, excluded.Reason);
            Assert.Contains("src/shared.cs", excluded.Detail);
            var state = System.Text.Json.JsonSerializer.Serialize(
                BatchGateStateSnapshotReader.Read(store, ProjectName, Settings(factory)));
            Assert.Contains("src/shared.cs", state);
        }
        finally
        {
            release.TrySetResult();
            await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Due_card_with_no_compatible_waiting_batch_gets_a_per_task_fallback()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        ConfigureThresholdWaiting(factory);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        _gate.Fallback = request => Green(request.ExpectedSha);
        try
        {
            SeedMember(factory, "CODE-BUSY-FALLBACK-A", "src/busy-a.cs");
            SeedMember(factory, "CODE-BUSY-FALLBACK-B", "src/busy-b.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            await _gate.FirstBatchStarted.WaitAsync(TimeSpan.FromSeconds(45));

            SeedMember(factory, "CODE-WAIT-FALLBACK-A", "src/shared.cs");
            SeedMember(factory, "CODE-WAIT-FALLBACK-B", "src/other.cs");
            pilot.RequestCloseNow(ProjectName);
            await pilot.TickAsync(CancellationToken.None);
            Assert.Single(store.ListRecoverableManifests(), manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed);

            var started = _clock;
            _clock = started.AddMinutes(-46);
            var lone = SeedMember(factory, "CODE-WAIT-FALLBACK-C", "src/shared.cs");
            _clock = started.AddSeconds(1);
            await pilot.TickAsync(CancellationToken.None);

            var state = System.Text.Json.JsonSerializer.SerializeToElement(pilot.State(ProjectName))
                .GetProperty("members").GetProperty(lone.Key);
            Assert.Equal("fallback", state.GetProperty("status").GetString());
            Assert.Equal(PerTaskFallbackRules.PlannerPerTask,
                state.GetProperty("fallbackRule").GetString());
        }
        finally
        {
            release.TrySetResult();
        }
    }
}

public abstract partial class BatchGatePilotServiceTests
{
    private protected static void ConfigureThresholdWaiting(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: "code", Planner: "threshold", CloseSize: 6,
            MinimumSize: 2, MaximumSize: 8, DeadlineMinutes: 45,
            PressureSize: 2, Overlap: "separate"));
    }
}
