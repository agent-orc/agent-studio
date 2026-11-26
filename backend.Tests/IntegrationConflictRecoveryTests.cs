using AgentStudio.Git;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationConflictRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "conflict-recovery-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task UnchangedCollisionSurvivesRestartAndExplicitReplayAndDoesNotIntegrateAgain()
    {
        var request = Request("first", 1);
        var tip = "target-1";
        var touched = false;
        var calls = 0;
        IntegrationConflictRecovery Recovery() => new(_ => tip, (_, _, _) => touched,
            _ => ["shared.cs"], _ => []);
        RemoteDeliveryIntegrationCoordinator Coordinator() => new(
            _ => { calls++; return Task.FromResult(Conflict(tip)); },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, conflictRecovery: Recovery());
        await Coordinator().EnqueueAsync(request);
        await Coordinator().EnqueueAsync(request with { ReviewAttemptId = "new-review" }, discardCompletedReplay: true);
        Assert.Equal(1, calls);
        tip = "unrelated-target";
        await Coordinator().EnqueueAsync(request, discardCompletedReplay: true);
        Assert.Equal(1, calls);
        touched = true;
        await Coordinator().EnqueueAsync(request, discardCompletedReplay: true);
        Assert.Equal(2, calls);
        await Coordinator().EnqueueAsync(request with { ReviewedResultSha = "new-delivery" }, discardCompletedReplay: true);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RecoveryWaitingForReviewHoldsLaterOverlappingDeliveryButLetsUnrelatedWorkPass()
    {
        var recovery = Request("repair", 1);
        var later = Request("later", 2);
        var unrelated = Request("unrelated", 3);
        var current = new TaskInfo { Id = "repair", FolderPath = recovery.JobFolderPath, State = TaskStates.AutoReview };
        var store = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false,
            request => request.JobId == "unrelated" ? ["other.cs"] : ["shared.cs"], _ => [current]);
        store.Record(recovery, Conflict("tip"));
        IntegrationConflictRecovery.MarkQueued(recovery.JobFolderPath);
        var entered = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(request =>
        {
            entered.Add(request.JobId);
            return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged));
        }, NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, conflictRecovery: store)
        { HoldRetryDelay = TimeSpan.FromMilliseconds(5) };
        var pending = coordinator.EnqueueAsync(later);
        await coordinator.EnqueueAsync(unrelated).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        // The repaired delivery arrived last but retains its original position.
        await coordinator.EnqueueAsync(recovery with { ReviewedResultSha = "resolved-sha", DeliveredAtUtc = At.AddHours(1) });
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["unrelated", "repair", "later"], entered);
    }

    private RemoteDeliveryIntegrationRequest Request(string id, int minute)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        return new("project", id, folder, _root, "develop", IntegrationStrategies.DirectMerge,
            PipelineTypes.Task, At.AddMinutes(minute), ReviewedResultSha: "delivery-" + id);
    }

    private static MergeIntoIntegrationResult Conflict(string tip) => MergeIntoIntegrationResult.RequiresAgentRound(
        ["shared.cs"], "Net-change replay conflicted.", new IntegrationConflictReport
        { IntegrationTipSha = tip, DeliverySha = "delivery-first", ConflictedFiles = ["shared.cs"] });

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
