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
    [InlineData(MergeIntoIntegrationOutcome.Conflict)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired)]
    public async Task Follow_up_is_claimable_only_after_its_session_delta_and_reasoning_route_are_saved(
        MergeIntoIntegrationOutcome outcome)
    {
        const string id = "AGT-3970";
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_root, state));
        var folder = Path.Combine(_root, TaskStates.AutoReview, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, title = "Update a local label", state = TaskStates.AutoReview, order = 1,
            agent = "codex", cliType = "codex", model = "gpt-6-sol", thinkingLevel = "high",
            taskType = "feature", modelExplicit = false, thinkingLevelExplicit = false,
            commits = Array.Empty<object>(),
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Update the local label.");
        var subject = new ReviewSubjectRecord
        {
            TaskKey = id, RunAttemptId = "run-original", AttemptChainId = "chain-original",
            ResultSha = new string('a', 40), ResultRef = "refs/heads/mutable-delivery",
            ImmutableResultRef = "refs/heads/results/original", CompletedAtUtc = At,
        };
        ReviewSubjectStore.Write(folder, subject);
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
        var service = new IntegrationAgentRoundService(scanner, mutations, states, timeline,
            NullLogger<IntegrationAgentRoundService>.Instance);
        var request = new RemoteDeliveryIntegrationRequest("fixture", id, folder, _root,
            "develop", IntegrationStrategies.DirectMerge, PipelineTypes.Task, At);
        var result = new MergeIntoIntegrationResult(outcome, null, "Integration needs a round.",
            ["label.cs"], [], new string('b', 40));

        var started = await service.TryStartAsync(request, result);

        Assert.True(started.Started, started.Reason);
        Assert.NotNull(deltaAtReady);
        Assert.Equal("low", effortAtReady);
        Assert.Equal(subject.ImmutableResultRef, deltaAtReady.DeliveryRef);
        Assert.Equal(subject.ResultSha, deltaAtReady.DeliverySha);
        Assert.Equal(new string('b', 40), deltaAtReady.BaseSha);
        Assert.Equal(["label.cs"], deltaAtReady.ConflictPaths);
        var queued = scanner.FindJob(id, _root)!;
        Assert.Equal(queued.PendingIntent?.Prompt, deltaAtReady.Steer);
        Assert.NotNull(SessionContinuationLedgerStore.ConsumeDelta(queued.FolderPath));
        Assert.Null(SessionContinuationLedgerStore.ConsumeDelta(queued.FolderPath));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
