using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3694: on 10 November 2026 AGT-3563 and AGT-3183 stayed in Auto Review
/// for twelve hours after an API restart. Their per-task gate had spent its
/// start budget, an engine decision on the gate outcome stayed open, and every
/// tick returned before the budget check without a trace. Each wait of a
/// per-task fallback is now named in the batch state and on the card, and
/// past its bound it raises a stall event and ends in a GateInfra escalation.
/// </summary>
public sealed class BatchGateFallbackStallTests : BatchGatePilotServiceTests
{
    private static readonly Dictionary<string, string?> EngineGateResult = new()
    {
        ["Orchestration:Transitions:GateResult"] = "Engine",
    };

    [Fact]
    public async Task Spent_start_budget_after_a_restart_escalates_as_gate_infra_with_attempts_and_base()
    {
        var (member, baseSha, candidateSha) = ArrangeSpentBudgetBeforeRestart(configuration: null);

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 4);
        await restarted.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        var ejected = Assert.Single(IntegrationLaneEvents.Read(CardFolder(TaskStates.Escalated, member.Key)),
            item => item.Kind == "ejected");
        Assert.Equal("GateInfra", ejected.Outcome);
        Assert.Contains("per-task gate interrupted 2 times", ejected.Reason);
        Assert.Contains($"per-task gate starts 2 of 2 on base {baseSha}, candidate {candidateSha}", ejected.Reason);
        Assert.Empty(restarted.Services.GetRequiredService<BatchGateStore>().ListPending());
    }

    [Fact]
    public async Task Red_engine_decision_left_pending_after_a_restart_is_named_then_escalates_past_its_bound()
    {
        // The spent budget asks the engine to escalate; no engine instance claims it.
        var (member, baseSha, _) = ArrangeSpentBudgetBeforeRestart(EngineGateResult);
        string runId;
        using (var before = BuildFactory(EngineGateResult))
        {
            _ = before.CreateClient();
            EnableBatchGate(before, closeSize: 4);
            await before.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);
            runId = Assert.Single(GateResultOccurrences(
                before.Services.GetRequiredService<MonolithOrchestrationStore>(), member.Key)).Run.RunId;
        }
        AssertLane(member.Key, TaskStates.AutoReview);
        var heldSince = _clock;
        _clock = _clock.AddMinutes(10);

        using var restarted = BuildFactory(EngineGateResult);
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 4);
        var pilot = restarted.Services.GetRequiredService<BatchGatePilotService>();
        var orchestration = restarted.Services.GetRequiredService<MonolithOrchestrationStore>();
        var store = restarted.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        // Within the bound the card waits, and the state says for what.
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal("pending", orchestration.Get(runId)!.Status);
        using (var state = State(pilot))
        {
            var card = state.RootElement.GetProperty("members").GetProperty(member.Key);
            Assert.Equal(PerTaskFallbackWaitKinds.EngineDecision, card.GetProperty("waitKind").GetString());
            Assert.Equal(runId, card.GetProperty("engineRunId").GetString());
            Assert.Equal("pending", card.GetProperty("engineStatus").GetString());
            Assert.Contains($"engine decision {runId} on the gate red is pending since",
                card.GetProperty("waitingFor").GetString());
        }
        Assert.Empty(store.ListFallbackStalls(ProjectName));

        _clock = heldSince + PerTaskFallbackWaitPolicy.EngineDecisionBound;
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        var superseded = orchestration.GetOccurrence(runId)!;
        Assert.Equal("cancelled", superseded.Run.Status);
        Assert.StartsWith("superseded: batch-fallback-engine-stall;", superseded.CancellationReason);
        var folder = CardFolder(TaskStates.Escalated, member.Key);
        Assert.Null(EngineDecisionWaitMarker.Read(folder));
        var ejected = Assert.Single(IntegrationLaneEvents.Read(folder), item => item.Kind == "ejected");
        Assert.Equal("GateInfra", ejected.Outcome);
        Assert.Contains($"engine decision {runId} on the per-task gate red stayed pending", ejected.Reason);
        Assert.Contains($"per-task gate starts 2 of 2 on base {baseSha}", ejected.Reason);
        var stall = Assert.Single(store.ListFallbackStalls(ProjectName));
        Assert.Equal((PerTaskFallbackWaitKinds.EngineDecision, "escalated", runId, "pending"),
            (stall.Kind, stall.Outcome, stall.EngineRunId, stall.EngineStatus));
        Assert.Equal(stall, Assert.Single(pilot.QueueStallReport(ProjectName).FallbackStalls!));
        Assert.Contains(restarted.Services.GetRequiredService<TimelineLog>().ReadAll(folder),
            entry => entry.Kind == PerTaskFallbackWaitPolicy.StallEventKind
                && entry.Details?.GetValueOrDefault("orchestrationRunId") == runId);
        Assert.Empty(store.ListPending());

        // A replayed tick neither waits again nor raises the stall twice.
        await pilot.TickAsync(CancellationToken.None);
        Assert.Single(store.ListFallbackStalls(ProjectName));
    }

    [Fact]
    public async Task Release_engine_decision_whose_lease_expired_escalates_without_releasing_the_card()
    {
        using var factory = BuildFactory(EngineGateResult);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-STALL-RELEASE", "docs/stall-release.md");
        _gate.Batch = _ => Infrastructure();
        _gate.Fallback = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);
        var held = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.StartsWith("batch-fallback:", held.IdempotencyKey);
        AssertLane(member.Key, TaskStates.AutoReview);
        // An engine instance claims it and is lost before it decides.
        var claim = orchestration.Claim(new("engine_lost", "lost-host",
            [Contract.OrchestrationStage.GateDispatch], RequestedTtlSeconds: 30));
        Assert.Equal(held.Run.RunId, claim.Run?.RunId);

        _clock = _clock.AddSeconds(30) + PerTaskFallbackWaitPolicy.LeaseGrace;
        await pilot.TickAsync(CancellationToken.None);

        // The monolith never takes the engine's release: the card is parked.
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Equal("cancelled", orchestration.Get(held.Run.RunId)!.Status);
        var ejected = Assert.Single(IntegrationLaneEvents.Read(CardFolder(TaskStates.Escalated, member.Key)),
            item => item.Kind == "ejected");
        Assert.Equal("GateInfra", ejected.Outcome);
        Assert.Contains($"engine decision {held.Run.RunId} on the per-task gate release stayed leased", ejected.Reason);
        Assert.Contains("engine lease expired", ejected.Reason);
        var stall = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListFallbackStalls(ProjectName));
        Assert.Equal(("leased", held.IdempotencyKey), (stall.EngineStatus, stall.OccurrenceKey));
    }

    [Fact]
    public async Task Stalled_gate_flight_that_honours_its_cancellation_resumes_with_the_start_charged()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-STALL-CANCEL", "docs/stall-cancel.md");
        var pilot = ResumingPilot(factory, member);
        _gate.FallbackAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };

        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstFallbackStarted;
        using (var state = State(pilot))
            Assert.Equal(PerTaskFallbackWaitKinds.GateRunning, state.RootElement.GetProperty("members")
                .GetProperty(member.Key).GetProperty("waitKind").GetString());

        _clock = _clock + PerTaskFallbackWaitPolicy.StageBound(PerTaskFallbackWaitKinds.GateRunning,
            TimeSpan.FromMinutes(Settings(factory).GateTimeoutMinutes));
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        AssertLane(member.Key, TaskStates.AutoReview);
        var marker = Assert.IsType<BatchGateOwnership>(Ownership(member.Key));
        Assert.True(marker.FallbackGateActive);
        Assert.Equal(1, marker.FallbackGateStarts);
        var stall = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListFallbackStalls(ProjectName));
        Assert.Equal((PerTaskFallbackWaitKinds.GateRunning, "cancelled"), (stall.Kind, stall.Outcome));

        _gate.FallbackAsync = null;
        _gate.Fallback = request => Green(request.ExpectedSha);
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(2, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Stalled_gate_flight_that_ignores_its_cancellation_is_detached_and_its_resume_escalates()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-STALL-HUNG", "docs/stall-hung.md");
        var pilot = ResumingPilot(factory, member);
        var hung = new TaskCompletionSource<BuildTestGateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.FallbackAsync = (_, _) => hung.Task;
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstFallbackStarted;
        _clock = _clock + PerTaskFallbackWaitPolicy.StageBound(PerTaskFallbackWaitKinds.GateRunning,
            TimeSpan.FromMinutes(Settings(factory).GateTimeoutMinutes));
        await pilot.TickAsync(CancellationToken.None);

        // Cancelled, but the gate never returns: the flight still blocks a second gate.
        Assert.True(pilot.IsPerTaskGateRunning(member.ReviewAttemptId));
        using (var state = State(pilot))
            Assert.Contains("cancelled at", state.RootElement.GetProperty("members")
                .GetProperty(member.Key).GetProperty("waitingFor").GetString());

        _clock += PerTaskFallbackWaitPolicy.CancellationGrace;
        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        var ejected = Assert.Single(IntegrationLaneEvents.Read(CardFolder(TaskStates.Escalated, member.Key)),
            item => item.Kind == "ejected");
        Assert.Equal("GateInfra", ejected.Outcome);
        Assert.Contains("per-task gate flight stalled in gate-running", ejected.Reason);
        Assert.Contains("did not stop after its cancellation; per-task gate starts 1 of 2 on base", ejected.Reason);
        Assert.Equal(["cancelled", "detached"], store.ListFallbackStalls(ProjectName).Select(item => item.Outcome));
    }

    [Fact]
    public void Wait_policy_bounds_engine_decisions_and_flight_stages()
    {
        // clock-independent: the pure policy receives a fixed fixture instant and compares only with it.
        var now = new DateTimeOffset(2026, 11, 10, 5, 20, 0, TimeSpan.Zero);
        EngineDecisionWaitFacts Facts(string status, TimeSpan open, TimeSpan? leaseExpiredFor = null,
            bool applyingHere = false)
            => new(status, now - open, leaseExpiredFor is { } expired ? now - expired : null, applyingHere, now);

        Assert.False(MonolithTransitionAdapter.PerTaskFallbackEngineWait(Facts("pending", TimeSpan.FromMinutes(29))).Stalled);
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackEngineWait(Facts("pending", TimeSpan.FromMinutes(30))).Stalled);
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackEngineWait(Facts("applying", TimeSpan.FromHours(12))).Stalled);
        Assert.False(MonolithTransitionAdapter.PerTaskFallbackEngineWait(
            Facts("applying", TimeSpan.FromHours(12), applyingHere: true)).Stalled);
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackEngineWait(
            Facts("leased", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2))).Stalled);
        Assert.False(MonolithTransitionAdapter.PerTaskFallbackEngineWait(
            Facts("leased", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1))).Stalled);
        Assert.False(MonolithTransitionAdapter.PerTaskFallbackEngineWait(Facts("completed", TimeSpan.FromDays(1))).Stalled);

        var timeout = TimeSpan.FromMinutes(150);
        Assert.Equal(TimeSpan.FromMinutes(165),
            PerTaskFallbackWaitPolicy.StageBound(PerTaskFallbackWaitKinds.GateRunning, timeout));
        Assert.Equal(TimeSpan.FromMinutes(600),
            PerTaskFallbackWaitPolicy.StageBound(PerTaskFallbackWaitKinds.MachineGateSlot, timeout));
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackFlight(new(PerTaskFallbackWaitKinds.GateRunning,
            now - TimeSpan.FromMinutes(165), timeout, now)).Stalled);
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackCancelsStalledStage(PerTaskFallbackWaitKinds.GateRunning));
        Assert.False(MonolithTransitionAdapter.PerTaskFallbackCancelsStalledStage(PerTaskFallbackWaitKinds.PerTaskSlot));
        Assert.False(MonolithTransitionAdapter.PerTaskFallbackCancelsStalledStage(PerTaskFallbackWaitKinds.Integration));
        Assert.True(MonolithTransitionAdapter.PerTaskFallbackDetachesCancelledFlight(now - TimeSpan.FromMinutes(2), now));
    }

    /// <summary>
    /// The live state before the restart, generated: an active per-task marker
    /// that spent its start budget on a recorded base and merged candidate.
    /// The first factory only seeds the card and is disposed, as the process ended.
    /// </summary>
    private (SeededMember Member, string BaseSha, string CandidateSha) ArrangeSpentBudgetBeforeRestart(
        IReadOnlyDictionary<string, string?>? configuration)
    {
        using var factory = BuildFactory(configuration);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-STALL-BUDGET", "docs/stall-budget.md");
        var pending = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        var baseSha = RemoteTip();
        var candidateSha = new string('c', 40);
        BatchGateOwnershipStore.Write(CardFolder(TaskStates.AutoReview, member.Key),
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                FallbackGateActive: true, FallbackGateStarts: BatchGatePilotService.FallbackGateStartBudget,
                FallbackBaseSha: baseSha, FallbackCandidateSha: candidateSha));
        _gate.Fallback = request => Green(request.ExpectedSha);
        return (member, baseSha, candidateSha);
    }

    // A durable active marker with no flight resumes on the next tick; the
    // tick does not wait for the flight, so the test can observe it running.
    private BatchGatePilotService ResumingPilot(WebApplicationFactory<Program> factory, SeededMember member)
    {
        var pending = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        BatchGateOwnershipStore.Write(CardFolder(TaskStates.AutoReview, member.Key),
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject, FallbackGateActive: true));
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        return pilot;
    }

    private static JsonDocument State(BatchGatePilotService pilot)
        => JsonDocument.Parse(JsonSerializer.Serialize(pilot.State(ProjectName),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
