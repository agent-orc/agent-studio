using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationAgentRoundSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-session-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 0, false)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 1, false)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 2, false)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 0, false)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 1, false)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 2, false)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 0, true)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 0, true)]
    public async Task Follow_up_is_claimable_only_after_its_session_delta_and_reasoning_route_are_saved(
        MergeIntoIntegrationOutcome outcome, int used, bool receiptWriteFailure)
    {
        const string id = "AGT-3970";
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_root, state));
        var folder = Path.Combine(_root, TaskStates.AutoReview, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, title = "Update a local label", state = TaskStates.AutoReview, order = 1,
            agent = "claude", cliType = "claude", model = "claude-opus-4-6", thinkingLevel = "high",
            taskType = "feature", modelExplicit = true, thinkingLevelExplicit = true,
            commits = Array.Empty<object>(),
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Update the local label.");
        var waitingFolder = Path.Combine(_root, TaskStates.Ready, "waiting");
        Directory.CreateDirectory(waitingFolder);
        File.WriteAllText(Path.Combine(waitingFolder, "task.json"), JsonSerializer.Serialize(new
        { id = "waiting", title = "An existing follow-up", state = TaskStates.Ready, order = 1 }));
        var subject = new ReviewSubjectRecord
        {
            TaskKey = id, RunAttemptId = "run-original", AttemptChainId = "chain-original",
            ResultSha = new string('a', 40), ResultRef = "refs/heads/mutable-delivery",
            ImmutableResultRef = "refs/heads/results/original", CompletedAtUtc = At,
        };
        ReviewSubjectStore.Write(folder, subject);
        SessionContinuationLedgerStore.Append(folder, new(
            subject.RunAttemptId, id, "codex", "repo", folder, "task/agt-3970",
            subject.ImmutableResultRef, subject.ResultSha, "clean", null, "session", "fresh", null,
            false, 0, null, null, null, null, null, At.UtcDateTime,
            Model: "gpt-6-sol", ThinkingLevel: "high"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "fixture", ["WatchPaths:0:Path"] = _root,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new AgentStudio.Registry.ProjectRegistry(config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
            notifier, NullLogger<TaskMutationService>.Instance);
        mutations.SavePendingIntent("waiting", ContinueModes.Steer, "An earlier pending follow-up.",
            reason: "fixture", activeJobId: null, watchPath: _root);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance,
            notifier: notifier, timeline: timeline);
        AgentStudio.TaskServer.Contracts.MechanicalRoundDelta? deltaAtReady = null;
        string? effortAtReady = null;
        notifier.JobsBulkChanged += () =>
        {
            var ready = scanner.FindJob(id, _root);
            if (ready?.State != TaskStates.Ready) return;
            deltaAtReady = SessionContinuationLedgerStore.PeekDelta(ready.FolderPath);
            effortAtReady = ready.ThinkingLevel;
        };
        for (var round = 0; round < used; round++)
            timeline.Append(folder, TimelineEventKinds.IntegrationRecoveryQueued, TimelineActors.System,
                "Previous conflict in old.cs with AGT-4000.", details: new()
                {
                    ["automatic"] = "true", ["integrationBranch"] = "develop",
                    ["conflictedFiles"] = "old.cs", ["involvedCards"] = "AGT-4000",
                    [IntegrationRecoveryBudget.DeliveryChainIdKey] = "previous@" + round,
                });
        var service = new IntegrationAgentRoundService(scanner, mutations, states, timeline,
            NullLogger<IntegrationAgentRoundService>.Instance);
        var request = new RemoteDeliveryIntegrationRequest("fixture", id, folder, _root,
            "develop", IntegrationStrategies.DirectMerge, PipelineTypes.Task, At);
        var result = new MergeIntoIntegrationResult(outcome, null, "Integration needs a round.",
            ["label.cs"], [], new string('b', 40));

        if (receiptWriteFailure)
            Directory.CreateDirectory(Path.Combine(folder, "integration-conflict-recovery.json"));

        var started = await service.TryStartAsync(request, result, ["AGT-4001"]);
        if (receiptWriteFailure)
        {
            Assert.False(started.Started);
            Assert.Contains("integration-conflict-receipt-write-failed", started.Reason);
            var unchanged = scanner.FindJob(id, _root)!;
            Assert.Equal(TaskStates.AutoReview, unchanged.State);
            Assert.Null(unchanged.PendingIntent);
            Assert.Equal(subject.ResultSha, ReviewSubjectStore.Read(folder)?.ResultSha);
            Assert.Null(SessionContinuationLedgerStore.PeekDelta(folder));
            Assert.Equal("high", unchanged.ThinkingLevel);
            Assert.Single(timeline.ReadAll(folder), row => row.Kind == TimelineEventKinds.IntegrationFailed);
            Assert.DoesNotContain(timeline.ReadAll(folder), row => row.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            return;
        }
        if (used == 2)
        {
            Assert.False(started.Started);
            Assert.True(started.BudgetExhausted);
            Assert.Contains("2/2", started.Reason);
            Assert.Contains("old.cs", started.Reason);
            Assert.Contains("AGT-4000", started.Reason);
            Assert.Contains("label.cs", started.Reason);
            Assert.Equal(TaskStates.AutoReview, scanner.FindJob(id, _root)!.State);
            var duplicate = await service.TryStartAsync(request, result, ["AGT-4001"]);
            Assert.True(duplicate.BudgetExhausted);
            Assert.Equal(started.Reason, duplicate.Reason);
            Assert.Single(timeline.ReadAll(folder), row => row.Kind == TimelineEventKinds.IntegrationFailed);
            return;
        }

        Assert.True(started.Started, started.Reason);
        Assert.NotNull(deltaAtReady);
        Assert.Equal("medium", effortAtReady);
        Assert.Equal(subject.ImmutableResultRef, deltaAtReady.DeliveryRef);
        Assert.Equal(subject.ResultSha, deltaAtReady.DeliverySha);
        Assert.Equal(new string('b', 40), deltaAtReady.BaseSha);
        Assert.Equal(["label.cs"], deltaAtReady.ConflictPaths);
        var queued = scanner.FindJob(id, _root)!;
        Assert.Equal(queued.PendingIntent?.Prompt, deltaAtReady.Steer);
        Assert.True(queued.Order < scanner.FindJob("waiting", _root)!.Order);
        Assert.Equal("codex", queued.CliType);
        Assert.Equal("gpt-6-sol", queued.Model);
        Assert.Contains("keep both sides", queued.PendingIntent!.Prompt);
        Assert.Contains("AGT-4001", queued.PendingIntent.Prompt);
        Assert.DoesNotContain(timeline.ReadAll(queued.FolderPath), row => row.Kind == TimelineEventKinds.IntegrationFailed);
        Assert.Equal(used + 1, IntegrationRecoveryBudget.Count(timeline.ReadAll(queued.FolderPath), subject).Used);
        await service.TryStartAsync(request, result, ["AGT-4001"]);
        Assert.Equal(used + 1, IntegrationRecoveryBudget.Count(timeline.ReadAll(queued.FolderPath), subject).Used);
        Assert.DoesNotContain(timeline.ReadAll(queued.FolderPath), row => row.Kind == TimelineEventKinds.IntegrationFailed);
        Assert.NotNull(SessionContinuationLedgerStore.ConsumeDelta(queued.FolderPath));
        Assert.Null(SessionContinuationLedgerStore.ConsumeDelta(queued.FolderPath));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
