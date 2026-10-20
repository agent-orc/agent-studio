using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3219: a per-task gate whose budget ran out in the machine gate queue
/// never ran, so the card stays in Auto Review and its request returns to the
/// batch queue. The test clock drives the stall episode and its event times.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Queue_overrun_keeps_cards_in_auto_review_and_raises_one_stall_event()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var first = SeedMember(factory, "DOC-QUEUE-A", "docs/queue-a.md");
        var second = SeedMember(factory, "DOC-QUEUE-B", "docs/queue-b.md");
        // Disabled, every queued member takes its own per-task gate on the tick.
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request =>
        {
            request.OnMachineGateSlotWaiting?.Invoke(2, 2);
            return QueueOverrun();
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var raisedAt = _clock;

        await pilot.TickAsync(CancellationToken.None);
        _clock = _clock.AddMinutes(155);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(4, _gate.FallbackRuns);
        foreach (var member in new[] { first, second })
        {
            AssertLane(member.Key, TaskStates.AutoReview);
            var ownership = Assert.IsType<BatchGateOwnership>(Ownership(member.Key));
            Assert.False(ownership.FallbackGateActive);
            Assert.Equal(0, ownership.FallbackGateStarts);
            Assert.True(store.IsQueued(member.ReviewAttemptId));
        }
        Assert.Empty(store.ListExecutions(ProjectName));
        var stall = Assert.Single(store.ListQueueStalls(ProjectName));
        Assert.Equal(raisedAt, stall.RaisedAtUtc);
        Assert.Equal(2, stall.SlotsTotal);
        Assert.Equal(9_327_018, stall.OldestWaitMs);
        Assert.True(stall.QueueDepth >= 1);
        Assert.Equal(stall.EpisodeId, pilot.QueueStalls.OpenEpisode(ProjectName)?.EpisodeId);
        var feed = Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath),
            entry => entry.Topic == BatchGatePilotService.QueueStallTopic);
        Assert.Equal(OrchestratorLogKinds.Alert, feed.Kind);
        Assert.Equal(raisedAt.UtcDateTime, feed.Ts);

        // The queue drains: both gates reach a slot and pass, which ends the episode.
        _gate.Fallback = request => Green(request.ExpectedSha);
        await pilot.TickAsync(CancellationToken.None);
        AssertLane(first.Key, TaskStates.HumanReview);
        AssertLane(second.Key, TaskStates.HumanReview);
        // A passed review drops its queue wait ledger.
        Assert.Null(store.ReadQueueWait(first.ReviewAttemptId));
        Assert.Null(store.ReadQueueWait(second.ReviewAttemptId));
        await pilot.TickAsync(CancellationToken.None);
        Assert.Null(pilot.QueueStalls.OpenEpisode(ProjectName));
    }

    // The 19./20.10.2026 incident: a gate result that returned after the card
    // or its review moved on parked the card in Escalated from its new lane.
    [Fact]
    public Task Queue_overrun_after_a_person_moved_the_card_to_ready_leaves_it_in_ready()
        => AssertStaleGateResultIsSuperseded(TaskStates.Ready, (factory, member) => MoveCard(factory, member,
            TaskStates.Ready), _ => QueueOverrun());

    [Fact]
    public Task Queue_overrun_after_a_follow_up_was_queued_leaves_the_card_in_auto_review()
        => AssertStaleGateResultIsSuperseded(TaskStates.AutoReview, SaveContinuation, _ => QueueOverrun());

    [Fact]
    public Task Queue_overrun_of_a_superseded_review_generation_leaves_the_card_in_auto_review()
        => AssertStaleGateResultIsSuperseded(TaskStates.AutoReview, ReplaceDelivery, _ => QueueOverrun());

    [Fact]
    public Task Execution_phase_red_of_a_stale_generation_does_not_escalate()
        => AssertStaleGateResultIsSuperseded(TaskStates.Ready, (factory, member) => MoveCard(factory, member,
            TaskStates.Ready), request => Red(request.ExpectedSha));

    [Fact]
    public Task Execution_phase_red_of_a_superseded_review_generation_does_not_escalate()
        => AssertStaleGateResultIsSuperseded(TaskStates.AutoReview, ReplaceDelivery,
            request => Red(request.ExpectedSha));

    private async Task AssertStaleGateResultIsSuperseded(string expectedLane,
        Action<WebApplicationFactory<Program>, SeededMember> duringWait,
        Func<BuildTestGateRequest, BuildTestGateResult> result)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-STALE-" + Guid.NewGuid().ToString("N")[..6], "docs/stale.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        _gate.Fallback = request =>
        {
            request.OnMachineGateSlotWaiting?.Invoke(2, 2);
            duringWait(factory, member);
            return result(request);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, expectedLane);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        Assert.Null(scanner.FindJob(member.Key, _watchPath)!.ParkedBlocker);
        Assert.False(store.IsQueued(member.ReviewAttemptId));
        Assert.Null(Ownership(member.Key));
        Assert.Null(store.ReadQueueWait(member.ReviewAttemptId));
        Assert.Empty(store.ListQueueStalls(ProjectName));
        var folder = scanner.FindJob(member.Key, _watchPath)!.FolderPath;
        Assert.Contains(IntegrationLaneEvents.Read(folder), lane => lane.Kind == "ejected" && lane.Outcome == "Superseded");
        Assert.DoesNotContain(factory.Services.GetRequiredService<TimelineLog>().ReadAll(folder), row =>
            row.Kind == TimelineEventKinds.LaneChanged && row.Details?.GetValueOrDefault("to") == TaskStates.Escalated);

        // Later ticks leave the card where it is.
        await pilot.TickAsync(CancellationToken.None);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, expectedLane);
    }

    private void MoveCard(WebApplicationFactory<Program> factory, SeededMember member, string lane)
    {
        var moved = factory.Services.GetRequiredService<TaskTransitionService>()
            .MoveAsync(member.Key, lane, _watchPath, CancellationToken.None, cause: "human:operator")
            .GetAwaiter().GetResult();
        Assert.True(moved.Status == MoveJobStatus.Success, $"{moved.Status}: {moved.Message}");
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    [Fact]
    public async Task Batch_gate_queue_overrun_requeues_the_members_for_the_next_batch_without_a_retry()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-BATCH-QUEUE", "docs/batch-queue.md");
        _gate.Batch = request =>
        {
            request.OnMachineGateSlotWaiting?.Invoke(2, 2);
            return QueueOverrun();
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.StartsWith("gate-queue-overrun",
            AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned).Reason, StringComparison.Ordinal);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.True(store.IsQueued(member.ReviewAttemptId));
        Assert.Null(Ownership(member.Key)?.BatchId);
        Assert.Equal(1, store.ReadQueueWait(member.ReviewAttemptId)?.Overruns);
        var stall = Assert.Single(store.ListQueueStalls(ProjectName));
        Assert.Equal(_clock, stall.RaisedAtUtc);
        Assert.Equal(member.Key, stall.TriggerTaskKey);

        // The next batch reaches a slot and publishes; the ledger goes with the pass.
        _gate.Batch = request => Green(request.ExpectedSha);
        await pilot.TickAsync(CancellationToken.None);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Equal(2, _gate.BatchRuns);
        Assert.Null(store.ReadQueueWait(member.ReviewAttemptId));
    }

    [Fact]
    public async Task Release_refuses_cards_a_stale_outcome_parked_from_another_lane_or_with_a_follow_up()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var fromReady = SeedMember(factory, "DOC-PARKED-READY", "docs/parked-ready.md");
        var followUp = SeedMember(factory, "DOC-PARKED-FOLLOW-UP", "docs/parked-follow-up.md");
        var stalled = SeedMember(factory, "DOC-PARKED-RELEASE", "docs/parked-release.md");
        // Before this fix a stale outcome escalated a card that had left Auto Review.
        MoveCard(factory, fromReady, TaskStates.Ready);
        await ParkAsLegacyFallback(factory, fromReady, QueueOverrun());
        await ParkAsLegacyFallback(factory, followUp, QueueOverrun());
        SaveContinuation(factory, followUp);
        await ParkAsLegacyFallback(factory, stalled, QueueOverrun());
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        Assert.Equal([stalled.Key], pilot.QueueStallReport(ProjectName).Releasable);
        var release = await pilot.ReleaseQueueStalledAsync(ProjectName, "operator", CancellationToken.None);

        Assert.Equal([stalled.Key], release.Released);
        Assert.Equal([followUp.Key, fromReady.Key],
            release.Refused.Select(refusal => refusal.TaskKey).Order(StringComparer.Ordinal));
        Assert.Contains("not escalated out of", Assert.Single(release.Refused, r => r.TaskKey == fromReady.Key).Reason);
        Assert.Contains("follow-up", Assert.Single(release.Refused, r => r.TaskKey == followUp.Key).Reason);
        AssertLane(fromReady.Key, TaskStates.Escalated);
        AssertLane(followUp.Key, TaskStates.Escalated);
        AssertLane(stalled.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Execution_phase_overrun_still_escalates_the_card()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-RUN-OVERRUN", "docs/run-overrun.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request => QueueOverrun() with
        {
            TestedSha = request.ExpectedSha,
            Reason = "gate run violated gate-run budget (phase=verification)",
            ViolatedBudget = new BuildTestGateBudgetEvidence("gate-run", 9_000_000, 9_000_100, "verification"),
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListQueueStalls(ProjectName));
        Assert.Empty(pilot.QueueStallReport(ProjectName).Releasable);
    }

    [Fact]
    public async Task Emergency_gate_queue_overrun_admits_the_review_to_the_batch_queue()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-QUEUE-EMERGENCY", "docs/queue-emergency.md", settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var passed = PassReview(authority, member);
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        _gate.Fallback = _ => QueueOverrun();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.RunEmergencyFallbackAsync(task, passed, authority.GetRun(member.RunAttemptId)!,
            _clock, CancellationToken.None);

        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.True(pilot.IsQueuedForBatch(member.ReviewAttemptId));
        Assert.False(Assert.IsType<BatchGateOwnership>(Ownership(member.Key)).FallbackGateActive);
        Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListQueueStalls(ProjectName));
    }

    [Fact]
    public async Task Release_requeues_cards_escalated_for_a_queue_overrun()
    {
        using var factory = BuildFactory();
        var http = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var stalled = SeedMember(factory, "DOC-PARKED-QUEUE", "docs/parked-queue.md");
        var red = SeedMember(factory, "DOC-PARKED-RED", "docs/parked-red.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var sequence = store.ReadPending(stalled.ReviewAttemptId)!.Subject.EnqueueSequence;
        // Before AGT-3219 a queue overrun escalated like a red suite.
        await ParkAsLegacyFallback(factory, stalled, QueueOverrun());
        await ParkAsLegacyFallback(factory, red, Red(red.ResultSha));
        Assert.False(store.IsQueued(stalled.ReviewAttemptId));

        var report = await http.GetFromJsonAsync<GateQueueStallReport>(
            $"/api/projects/{ProjectName}/batch-gate/queue-stalls", QueueStallJson);
        Assert.Equal([stalled.Key], report!.Releasable);

        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var response = await http.PostAsync($"/api/projects/{ProjectName}/batch-gate/queue-stalls/release", null);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GateQueueStallRelease>(QueueStallJson);

        Assert.Equal([stalled.Key], release!.Released);
        Assert.Empty(release.Refused);
        AssertLane(stalled.Key, TaskStates.AutoReview);
        AssertLane(red.Key, TaskStates.Escalated);
        Assert.True(store.IsQueued(stalled.ReviewAttemptId));
        Assert.Equal(sequence, store.ReadPending(stalled.ReviewAttemptId)!.Subject.EnqueueSequence);
        Assert.Equal(stalled.ReviewAttemptId, Ownership(stalled.Key)?.ReviewAttemptId);

        // The released member takes its gate from the queue like any other.
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = gate => Green(gate.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);
        AssertLane(stalled.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Queue_overruns_up_to_the_cap_escalate_as_gate_capacity_and_remind_while_the_episode_stays_open()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-QUEUE-CAP", "docs/queue-cap.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        _gate.Fallback = request =>
        {
            request.OnMachineGateSlotWaiting?.Invoke(2, 2);
            return QueueOverrun();
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var opened = _clock;

        // Three overruns stay below the cap of four queue budgets.
        for (var overrun = 1; overrun <= 3; overrun++)
        {
            await pilot.TickAsync(CancellationToken.None);
            AssertLane(member.Key, TaskStates.AutoReview);
            Assert.Equal(overrun, store.ReadQueueWait(member.ReviewAttemptId)?.Overruns);
            _clock = _clock.AddMinutes(155);
        }
        // The fourth reaches it: the card escalates under its own category.
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(4, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var parked = scanner.FindJob(member.Key, _watchPath)!;
        Assert.Equal(HumanReviewEscalationCategories.GateCapacity, parked.ParkedBlocker?.BlockerType);
        Assert.StartsWith("[gate-capacity] The gate request waited", parked.ParkedBlocker?.Reason);
        Assert.False(store.IsQueued(member.ReviewAttemptId));
        Assert.Equal(4 * 9_327_018L, store.ReadQueueWait(member.ReviewAttemptId)?.TotalQueueWaitMs);

        // One episode: opened at the first overrun, reminded once after four hours.
        var stalls = store.ListQueueStalls(ProjectName);
        Assert.Equal([1, 2], stalls.Select(stall => stall.Raise));
        Assert.Single(stalls.Select(stall => stall.EpisodeId).Distinct());
        Assert.Equal(opened, stalls[0].RaisedAtUtc);
        Assert.Equal(opened.AddMinutes(310), stalls[1].RaisedAtUtc);
        Assert.Equal(opened, stalls[1].EpisodeOpenedAtUtc);
        Assert.Equal(2, factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath)
            .Count(entry => entry.Topic == BatchGatePilotService.QueueStallTopic));

        // The release returns the card with a fresh cap; the gate now reaches a slot.
        Assert.Equal([member.Key], pilot.QueueStallReport(ProjectName).Releasable);
        var release = await pilot.ReleaseQueueStalledAsync(ProjectName, "operator", CancellationToken.None);
        Assert.Equal([member.Key], release.Released);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Null(store.ReadQueueWait(member.ReviewAttemptId));
        _gate.Fallback = request => Green(request.ExpectedSha);
        await pilot.TickAsync(CancellationToken.None);
        AssertLane(member.Key, TaskStates.HumanReview);
        await pilot.TickAsync(CancellationToken.None);
        Assert.Null(pilot.QueueStalls.OpenEpisode(ProjectName));
    }

    private static readonly JsonSerializerOptions QueueStallJson = new(JsonSerializerDefaults.Web);

    /// <summary>The escalation the per-task fallback wrote before AGT-3219.</summary>
    private async Task ParkAsLegacyFallback(WebApplicationFactory<Program> factory,
        SeededMember member, BuildTestGateResult gate)
    {
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        var evidence = Path.Combine(TaskPaths.LogsDir(task.FolderPath), $"batch-fallback-{member.ReviewAttemptId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(gate));
        var moved = await factory.Services.GetRequiredService<HumanReviewEscalation>().EscalateAsync(task.Id,
            task.WatchPath, task.ProjectName, HumanReviewEscalationCategories.AutoReviewEscalation,
            gate.Reason, CancellationToken.None);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        BatchGateOwnershipStore.ClearIfReviewAttempt(moved.NewFolderPath!, member.ReviewAttemptId);
        factory.Services.GetRequiredService<BatchGateStore>().ResolvePending(member.ReviewAttemptId, "per-task-gate-red");
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    private ReviewAttemptDto PassReview(AttemptAuthorityService authority, SeededMember member)
    {
        var claimed = authority.ClaimReview(member.ReviewAttemptId, "review-executor", "review-host",
            600, "claim-" + member.Key);
        Assert.True(claimed.Accepted, claimed.Message);
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(member.ReviewAttemptId, claimed.ReviewAttempt!.LastFence,
                claimed.ReviewAttempt.AuthorityEpoch, "pass-" + member.Key),
            member.ResultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        return passed.ReviewAttempt!;
    }

    /// <summary>The production failure of 17.10.2026: the request never reached a slot.</summary>
    private static BuildTestGateResult QueueOverrun()
        => new(BuildTestGateVerdict.Fail, null, 0, "",
            "machine build/test gate queue wait violated machine-gate-queue budget "
            + "(limit=9327000ms, consumed=9327018ms, phase=queue)", false, false)
        {
            FailureKind = BuildTestGateFailureKind.Timeout,
            GateQueueWaitMs = 9_327_018,
            ViolatedBudget = new BuildTestGateBudgetEvidence("machine-gate-queue", 9_327_000, 9_327_018, "queue"),
        };
}
