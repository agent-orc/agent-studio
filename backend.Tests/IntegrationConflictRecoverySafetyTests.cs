using AgentStudio.Git;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationConflictRecoverySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "conflict-safety-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task InvalidReceiptDoesNotStrandTheProjectDrain(string content)
    {
        var request = Request("broken");
        File.WriteAllText(Path.Combine(request.JobFolderPath, "integration-conflict-recovery.json"), content);
        var card = new TaskInfo { Id = request.JobId, FolderPath = request.JobFolderPath, State = TaskStates.AutoReview };
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false, _ => ["shared.cs"], _ => [card]);
        var entered = new List<string>();
        var coordinator = Coordinator(recovery, entered);

        var result = await coordinator.EnqueueAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.EnqueueAsync(Request("next")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.Contains("integration-conflict-receipt-unreadable", result.Error);
        Assert.Equal(["next"], entered);
        Assert.Equal(content, File.ReadAllText(Path.Combine(request.JobFolderPath, "integration-conflict-recovery.json")));
    }

    [Fact]
    public void ReceiptReadIoFailureRefusesRetryInsteadOfThrowing()
    {
        var request = Request("locked");
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false, _ => [], _ => []);
        recovery.Record(request, Conflict());
        using var locked = new FileStream(Path.Combine(request.JobFolderPath, "integration-conflict-recovery.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, recovery.Replay(request)?.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowConflictFactsDoNotBlockAnotherProject(bool retry)
    {
        var request = Request("slow");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabled = false;
        void WaitForFacts()
        {
            if (!enabled) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
        var card = new TaskInfo { Id = "repair", FolderPath = request.JobFolderPath, State = TaskStates.AutoReview };
        var recovery = new IntegrationConflictRecovery(
            item => { if (retry && item.JobId == "slow") WaitForFacts(); return "tip"; }, (_, _, _) => false,
            item => { if (!retry && item.JobId == "slow") WaitForFacts(); return ["other.cs"]; },
            item => item.Project == "project" ? [card] : []);
        recovery.Record(request, Conflict());
        IntegrationConflictRecovery.MarkQueued(request.JobFolderPath);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(Conflict()), NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            conflictRecovery: recovery);
        if (retry) await coordinator.EnqueueAsync(request);
        enabled = true;
        var slow = Task.Run(() => coordinator.EnqueueAsync(request));
        Task<MergeIntoIntegrationResult>? other = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            other = Task.Run(() => coordinator.EnqueueAsync(Request("other") with { Project = "other" }));
            await other.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await slow.WaitAsync(TimeSpan.FromSeconds(5));
            if (other is not null) await other.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task QueueChangesDuringPathReadAreRecheckedBeforeSelectingTheHead()
    {
        var request = Request("later") with { DeliveredAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(2) };
        var reservation = Request("repair");
        var card = new TaskInfo { Id = reservation.JobId, FolderPath = reservation.JobFolderPath, State = TaskStates.AutoReview };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false, item =>
        {
            if (item.JobId == "later" && Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return ["other.cs"];
        }, _ => [card]);
        recovery.Record(reservation, Conflict());
        IntegrationConflictRecovery.MarkQueued(reservation.JobFolderPath);
        var integrated = new List<string>();
        var coordinator = Coordinator(recovery, integrated);
        var later = Task.Run(() => coordinator.EnqueueAsync(request));
        Task<MergeIntoIntegrationResult>? earlier = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Enqueue returns while the first snapshot's path read is still blocked.
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            earlier = Task.Run(async () =>
            {
                var pending = coordinator.EnqueueAsync(Request("earlier") with
                { DeliveredAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1) });
                queued.SetResult();
                return await pending;
            });
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await later.WaitAsync(TimeSpan.FromSeconds(5));
            if (earlier is not null) await earlier.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(["earlier", "later"], integrated);
    }

    private static RemoteDeliveryIntegrationCoordinator Coordinator(IntegrationConflictRecovery recovery, List<string> entered)
        => new(request =>
        {
            entered.Add(request.JobId);
            return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged));
        }, NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, conflictRecovery: recovery);

    private RemoteDeliveryIntegrationRequest Request(string id)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        return new("project", id, folder, _root, "develop", IntegrationStrategies.DirectMerge,
            PipelineTypes.Task, DateTimeOffset.UnixEpoch, ReviewedResultSha: "delivery-" + id);
    }

    private static MergeIntoIntegrationResult Conflict() => MergeIntoIntegrationResult.RequiresAgentRound(
        ["shared.cs"], "Net-change replay conflicted.", new IntegrationConflictReport
        { IntegrationTipSha = "tip", DeliverySha = "delivery", ConflictedFiles = ["shared.cs"] });

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
