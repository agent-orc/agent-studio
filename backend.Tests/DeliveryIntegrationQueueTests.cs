using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// clock-independent: delivery and board times are fixed labels for ordering and text; no assertion compares them with now.
namespace AgentStudio.Tests;

/// <summary>
/// AGT-4020: the delivery integration queue hands every waiting card to the
/// post-merge train in drain order, and a waiting card names its position and
/// what is in front of it.
/// </summary>
public sealed class DeliveryIntegrationQueueTests
{
    [Fact]
    public async Task TheTrainGetsTheHeadAndEveryWaitingCardInDeliveryOrder()
    {
        var board = new DeliveryIntegrationQueueBoard();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trains = new List<string[]>();
        var integrated = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                lock (integrated) integrated.Add(request.JobId);
                if (request.JobId == "AGT-1") await release.Task;
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            planTrain: queue =>
            {
                lock (trains) trains.Add(queue.Select(request => request.JobId).ToArray());
                return Task.CompletedTask;
            },
            queueBoard: board);

        var head = coordinator.EnqueueAsync(Request("AGT-1", 0));
        var waiting = new[] { ("AGT-6", 6), ("AGT-3", 3), ("AGT-2", 2), ("AGT-5", 5), ("AGT-4", 4) }
            .Select(item => coordinator.EnqueueAsync(Request(item.Item1, item.Item2))).ToArray();

        var third = board.PositionOf("project", "AGT-3");
        Assert.NotNull(third);
        Assert.Equal(3, third!.Position);
        Assert.Equal(6, third.Total);
        Assert.Equal(["AGT-1", "AGT-2"], third.Ahead.Select(row => row.JobId));
        Assert.StartsWith("integration queue position 3 of 6; in front: AGT-1 (integrating since ", third.Describe());
        Assert.EndsWith(", AGT-2", third.Describe());
        Assert.Equal("integration queue position 1 of 6; integrating now",
            board.PositionOf("project", "AGT-1")!.Describe());

        release.SetResult();
        await head;
        await Task.WhenAll(waiting);

        Assert.Equal(["AGT-1", "AGT-2", "AGT-3", "AGT-4", "AGT-5", "AGT-6"], integrated);
        Assert.Equal(["AGT-2", "AGT-3", "AGT-4", "AGT-5", "AGT-6"], trains[0]);
        Assert.Null(board.PositionOf("project", "AGT-6"));
    }

    [Fact]
    public void AWaitingCardNamesItsPositionWhatIsInFrontAndWhyItsLastAttemptFailed()
    {
        var board = new DeliveryIntegrationQueueBoard();
        var since = new DateTimeOffset(2026, 11, 23, 15, 41, 0, TimeSpan.Zero);
        board.Publish("project",
        [
            new("AGT-3993", DeliveryIntegrationQueueBoard.CompilingTrain(6), since),
            new("AGT-4002", DeliveryIntegrationQueueBoard.Waiting, since),
            new("AGT-4011", DeliveryIntegrationQueueBoard.Waiting, since),
        ]);
        board.RecordOutcome("project", "AGT-4011",
            "The build gate blocked the merge into develop: guard step failed without a failing guard test (timed out).");

        var line = TaskLiveStatusProjection.WithIntegrationQueue(
            new TaskLiveQueue { Kind = "review", State = TaskLiveQueueStates.Integration, Reason = "waiting for delivery integration" },
            board.PositionOf("project", "AGT-4011"));

        Assert.Equal(TaskLiveQueueStates.Integration, line!.State);
        Assert.Equal(
            "integration queue position 3 of 3; in front: AGT-3993 (compiling the combined tree of 6 cards since 15:41:00Z), AGT-4002; "
            + "previous attempt failed: The build gate blocked the merge into develop: guard step failed without a failing guard test (timed out).",
            line.Reason);

        board.RecordOutcome("project", "AGT-4011", null);
        Assert.DoesNotContain("previous attempt", board.PositionOf("project", "AGT-4011")!.Describe());
        // A card the queue does not hold, and a line about something else, are left alone.
        var reviewing = new TaskLiveQueue { Kind = "review", State = TaskLiveQueueStates.Reviewing, Reason = "reviewing" };
        Assert.Same(reviewing, TaskLiveStatusProjection.WithIntegrationQueue(reviewing, board.PositionOf("project", "AGT-4002")));
        Assert.Null(TaskLiveStatusProjection.WithIntegrationQueue(null, board.PositionOf("project", "AGT-9999")));
    }

    private static RemoteDeliveryIntegrationRequest Request(string jobId, int minute)
        => new(
            "project",
            jobId,
            "/task/" + jobId,
            "/project",
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            new DateTimeOffset(2026, 11, 23, 15, minute, 0, TimeSpan.Zero));
}
