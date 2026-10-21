using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3281: on 21.10.2026 sixteen cards waited in Ready for up to sixteen
/// hours. Each named a DegradeWithFollowUp decision that was still applying
/// after an operator fix round had moved its card out of Auto Review, and the
/// recovery loop retried every one of them every 30 seconds. A decision whose
/// card left its lane is now settled as superseded, and it never blocks a claim.
/// The tests own every recovery pass and the clock.
/// </summary>
public sealed class EngineDecisionSupersessionTests : IDisposable
{
    private const string Project = "agent-runner-01";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "engine-decision-supersession-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 0, 45, 0, TimeSpan.Zero));

    private string ProjectPath => Path.Combine(_workspace, "projects", Project);
    private string OccurrencePath => Path.Combine(_workspace, ".orchestration-occurrences");

    [Fact]
    public async Task Operator_fix_round_supersedes_an_applying_degrade_decision_at_the_move()
    {
        const string key = "AGT-FIX-ROUND";
        var folder = Card(key, TaskStates.AutoReview);
        var runId = await ApplyingOnDiskAsync(key, TransitionAction.DegradeWithFollowUp);
        EngineDecisionWaitMarker.Write(folder, runId);
        using var factory = Factory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        Assert.True(Find(factory, key).WaitingForEngineDecision);

        using var response = await http.PostAsJsonAsync(
            $"/api/tasks/{key}/move?watchPath={Uri.EscapeDataString(ProjectPath)}",
            new { targetState = TaskStates.Ready, reason = "Fix round after the deep review." });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var occurrence = store.GetOccurrence(runId)!;
        Assert.True(MonolithOrchestrationStore.IsSuperseded(occurrence));
        Assert.Equal("superseded:" + EngineDecisionSupersessionCauses.OperatorMove, occurrence.CancellationReason);
        Assert.Equal(TaskStates.Ready, occurrence.SupersededInState);
        AssertClaimable(factory, key);
        var entry = Assert.Single(Superseded(factory, key));
        Assert.Equal(EngineDecisionSupersessionCauses.OperatorMove, entry.Details!["cause"]);
        Assert.Equal(TaskStates.AutoReview, entry.Details["expectedSourceState"]);
        Assert.Equal(TaskStates.Ready, entry.Details["observedState"]);
        Assert.StartsWith("human", entry.Actor, StringComparison.Ordinal);

        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 0, 0),
            await store.RecoverApplyingAsync(CancellationToken.None));
        AssertNoFollowUp(factory, runId);
    }

    [Fact]
    public async Task Card_moved_by_another_path_while_applying_is_superseded_once_by_recovery()
    {
        const string key = "AGT-SYSTEM-MOVE";
        var folder = Card(key, TaskStates.AutoReview);
        var runId = await ApplyingOnDiskAsync(key, TransitionAction.DegradeWithFollowUp);
        EngineDecisionWaitMarker.Write(folder, runId);
        using var factory = Factory();
        _ = factory.CreateClient();
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            key, TaskStates.Ready, ProjectPath, CancellationToken.None, cause: "system",
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        // The marker moved with the card: this is the state the operator found.
        Assert.True(Find(factory, key).WaitingForEngineDecision);
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var first = await store.RecoverApplyingAsync(CancellationToken.None);
        var second = await store.RecoverApplyingAsync(CancellationToken.None);

        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 1, 0), first);
        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 0, 0), second);
        var occurrence = store.GetOccurrence(runId)!;
        Assert.Equal("superseded:" + EngineDecisionSupersessionCauses.SourceStateMismatch,
            occurrence.CancellationReason);
        Assert.Equal(TaskStates.Ready, occurrence.SupersededInState);
        AssertClaimable(factory, key);
        Assert.Equal(TaskStates.Ready, Assert.Single(Superseded(factory, key)).Details!["observedState"]);
        AssertNoFollowUp(factory, runId);
    }

    [Fact]
    public async Task Card_completed_before_the_engine_decision_applies_settles_it_as_superseded()
    {
        const string key = "AGT-COMPLETED-FIRST";
        var folder = Card(key, TaskStates.Completed);
        using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        // The report was decided in Auto Review; the card completed before the engine answered.
        var run = store.Create(Project, key, OrchestrationStage.ReviewDecision, ReviewPayload(key),
            $"review-report:review_{key}:report", TaskStates.AutoReview, "review_" + key, 3, 4,
            profile: "ReviewReport");
        EngineDecisionWaitMarker.Write(folder, run.RunId);
        var claim = store.Claim(new OrchestrationClaimRequest("engine_test", "instance",
            [OrchestrationStage.ReviewDecision]));
        var completion = Completion(claim, key, TransitionAction.DegradeWithFollowUp);

        var settled = await store.CompleteAsync(run.RunId, completion, CancellationToken.None);
        var replay = await store.CompleteAsync(run.RunId, completion, CancellationToken.None);

        Assert.Equal("cancelled", settled.Status);
        Assert.Equal("cancelled", replay.Status);
        Assert.Equal(TaskStates.Completed, store.GetOccurrence(run.RunId)!.SupersededInState);
        Assert.Null(EngineDecisionWaitMarker.Read(folder));
        Assert.False(Find(factory, key).WaitingForEngineDecision);
        Assert.Equal(TaskStates.Completed, Assert.Single(Superseded(factory, key)).Details!["observedState"]);
        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 0, 0),
            await store.RecoverApplyingAsync(CancellationToken.None));
        AssertNoFollowUp(factory, run.RunId);
    }

    [Fact]
    public async Task Restart_with_superseded_occurrences_on_disk_neither_retries_nor_blocks_claims()
    {
        // The incident shape: still applying, card already in Ready with the marker.
        const string stuck = "AGT-STUCK-APPLYING";
        var stuckFolder = Card(stuck, TaskStates.Ready);
        var stuckRun = await ApplyingOnDiskAsync(stuck, TransitionAction.DegradeWithFollowUp);
        EngineDecisionWaitMarker.Write(stuckFolder, stuckRun);
        // Settled as superseded before the restart, with the marker left behind.
        const string settled = "AGT-SETTLED-BEFORE";
        var settledFolder = Card(settled, TaskStates.Ready);
        var journal = new MonolithOrchestrationStore(OccurrencePath, _clock, new StoppedApplier());
        var prepared = journal.Create(Project, settled, OrchestrationStage.ReviewDecision, ReviewPayload(settled),
            $"review-report:review_{settled}:report", TaskStates.AutoReview, "review_" + settled, 3, 4,
            claimable: false, profile: "ReviewReport");
        Assert.NotNull(journal.TrySupersede(prepared.RunId, EngineDecisionSupersessionCauses.OperatorMove,
            TaskStates.Ready));
        EngineDecisionWaitMarker.Write(settledFolder, prepared.RunId);

        using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);
        _clock.Advance(TimeSpan.FromSeconds(31));

        recovery.RecoverOnce();
        var applied = await store.RecoverApplyingAsync(CancellationToken.None);
        recovery.RecoverOnce();

        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 1, 0), applied);
        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(0, 0, 0),
            await store.RecoverApplyingAsync(CancellationToken.None));
        Assert.True(MonolithOrchestrationStore.IsSuperseded(store.GetOccurrence(stuckRun)!));
        // A superseded preparation is never activated again.
        Assert.Equal("cancelled", store.Get(prepared.RunId)!.Status);
        Assert.DoesNotContain(store.AwaitingActivation(), item => item.Run.RunId == prepared.RunId);
        AssertClaimable(factory, stuck);
        AssertClaimable(factory, settled);
        Assert.Empty(store.WaitingOccurrences());
    }

    [Fact]
    public async Task Applying_decision_in_its_source_lane_still_applies_and_releases_the_card()
    {
        const string key = "AGT-IN-LANE";
        var folder = Card(key, TaskStates.AutoReview);
        var runId = await ApplyingOnDiskAsync(key, TransitionAction.Halt);
        EngineDecisionWaitMarker.Write(folder, runId);
        using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        Assert.True(Find(factory, key).WaitingForEngineDecision);

        var recovered = await store.RecoverApplyingAsync(CancellationToken.None);

        Assert.Equal(new MonolithOrchestrationStore.ApplyingRecovery(1, 0, 0), recovered);
        Assert.Equal("completed", store.Get(runId)!.Status);
        var card = Find(factory, key);
        Assert.Equal(TaskStates.AutoReview, card.State);
        Assert.False(card.WaitingForEngineDecision);
        Assert.Empty(Superseded(factory, key));
    }

    [Fact]
    public async Task Undecided_lane_decision_is_superseded_after_its_card_leaves_the_lane()
    {
        const string key = "AGT-UNDECIDED";
        Card(key, TaskStates.AutoReview);
        using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);
        var run = store.Create(Project, key, OrchestrationStage.ReviewDecision, ReviewPayload(key),
            $"review-report:review_{key}:report", TaskStates.AutoReview, "review_" + key, 3, 4,
            profile: "ReviewReport");
        recovery.RecoverOnce();
        Assert.True(Find(factory, key).WaitingForEngineDecision);
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            key, TaskStates.Ready, ProjectPath, CancellationToken.None, cause: "system",
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        // Inside the preparation grace the card may still be moving into the lane.
        recovery.RecoverOnce();
        Assert.Equal("pending", store.Get(run.RunId)!.Status);
        _clock.Advance(EngineDecisionStandingPolicy.PreparationGrace + TimeSpan.FromSeconds(1));
        recovery.RecoverOnce();

        Assert.Equal("superseded:" + EngineDecisionSupersessionCauses.SourceStateMismatch,
            store.GetOccurrence(run.RunId)!.CancellationReason);
        AssertClaimable(factory, key);
        Assert.Single(Superseded(factory, key));
        Assert.Equal("empty", store.Claim(new OrchestrationClaimRequest("engine_test", "instance",
            [OrchestrationStage.ReviewDecision])).Status);
    }

    [Fact]
    public void Card_waiting_past_the_bound_while_the_engine_is_live_raises_one_overdue_alert()
    {
        const string key = "AGT-OVERDUE";
        Card(key, TaskStates.AutoReview);
        using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var readiness = factory.Services.GetRequiredService<OrchestrationEngineReadiness>();
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);
        var run = store.Create(Project, key, OrchestrationStage.ReviewDecision, ReviewPayload(key),
            $"review-report:review_{key}:report", TaskStates.AutoReview, "review_" + key, 3, 4,
            profile: "ReviewReport");
        recovery.RecoverOnce();
        Assert.True(readiness.IsLive);

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(recovery.AlertOverdueWaits());
        Assert.Equal(0, readiness.OverdueDecisions);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var overdue = recovery.AlertOverdueWaits();
        recovery.AlertOverdueWaits();

        Assert.Equal(run.RunId, Assert.Single(overdue).Run.RunId);
        Assert.Equal(1, readiness.OverdueDecisions);
        Assert.Equal(new DateTimeOffset(run.CreatedAt, TimeSpan.Zero), readiness.OldestOverdueSince);
        Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(ProjectPath),
            entry => entry.Topic == "engine-decision-overdue:" + run.RunId
                && entry.Kind == OrchestratorLogKinds.Alert);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = ProjectPath,
                    ["WatchPaths:0:RootPath"] = ProjectPath,
                    ["WatchPaths:0:RepositoryPath"] = ProjectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                // The test owns every recovery pass; no host loop runs beside it.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });

    private string Card(string key, string state)
    {
        foreach (var lane in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(ProjectPath, lane));
        var folder = Path.Combine(ProjectPath, state, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state, order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Deliver the change.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: delivered.");
        return folder;
    }

    private static string ReviewPayload(string key)
        => JsonSerializer.Serialize(new { reviewOutcome = "ProductFailure", reviewAttemptId = "review_" + key });

    /// <summary>
    /// The engine decided and the host stopped before the decision applied, so
    /// the occurrence is applying on disk when the host starts.
    /// </summary>
    private async Task<string> ApplyingOnDiskAsync(string key, TransitionAction action)
    {
        var journal = new MonolithOrchestrationStore(OccurrencePath, _clock, new StoppedApplier());
        var run = journal.Create(Project, key, OrchestrationStage.ReviewDecision, ReviewPayload(key),
            $"review-report:review_{key}:report", TaskStates.AutoReview, "review_" + key, 3, 4,
            profile: "ReviewReport");
        var claim = journal.Claim(new OrchestrationClaimRequest("engine_test", "instance",
            [OrchestrationStage.ReviewDecision]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        await Assert.ThrowsAsync<IOException>(() => journal.CompleteAsync(run.RunId,
            Completion(claim, key, action), CancellationToken.None));
        Assert.Equal("applying", journal.Get(run.RunId)!.Status);
        return run.RunId;
    }

    private static CompleteOrchestrationStageRequest Completion(OrchestrationClaimResponse claim, string key,
        TransitionAction action)
    {
        var decision = TransitionDecisionPolicy.Create(action, "Engine decision for the reviewed delivery.",
            key, "review_" + key, 1, 4);
        return new CompleteOrchestrationStageRequest(claim.Lease!.EngineId, claim.Lease.InstanceId,
            claim.Lease.LeaseId, claim.Lease.Fence, OrchestrationStage.ReviewDecision,
            OrchestrationAction.Continue, JsonSerializer.Serialize(new { transition = decision }),
            "engine:" + claim.Run!.RunId);
    }

    private TaskInfo Find(WebApplicationFactory<Program> factory, string key)
    {
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        return scanner.FindJob(key, ProjectPath)!;
    }

    /// <summary>Claim paths skip a card in Ready that waits for an engine decision.</summary>
    private void AssertClaimable(WebApplicationFactory<Program> factory, string key)
    {
        var card = Find(factory, key);
        Assert.Equal(TaskStates.Ready, card.State);
        Assert.False(card.WaitingForEngineDecision);
        Assert.Null(EngineDecisionWaitMarker.Read(card.FolderPath));
    }

    private List<TimelineEvent> Superseded(WebApplicationFactory<Program> factory, string key)
        => factory.Services.GetRequiredService<TimelineLog>().ReadAll(Find(factory, key).FolderPath)
            .Where(entry => entry.Kind == TimelineEventKinds.EngineDecisionSuperseded).ToList();

    private static void AssertNoFollowUp(WebApplicationFactory<Program> factory, string runId)
        => Assert.DoesNotContain(factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs(),
            card => card.CreationSource == "engine-follow-up:" + runId);

    /// <summary>A host that stops after the engine decision is saved and before it applies.</summary>
    private sealed class StoppedApplier : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
            => throw new IOException("The host stopped before the decision applied.");
    }
}
