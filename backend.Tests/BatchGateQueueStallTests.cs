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
        await pilot.TickAsync(CancellationToken.None);
        Assert.Null(pilot.QueueStalls.OpenEpisode(ProjectName));
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
