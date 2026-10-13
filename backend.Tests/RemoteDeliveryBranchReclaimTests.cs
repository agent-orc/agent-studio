using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2793 requirement 2(a): <see cref="RemoteDeliveryIntegrationCoordinator"/>
/// is the merge every live Remote delivery takes, so it is the
/// after-integration trigger for the branch reclaim. It fires only on a
/// successful integration and never on a gate failure or conflict.
/// </summary>
public sealed class RemoteDeliveryBranchReclaimTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly FakeTimeProvider _clock = new();

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged)]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyMerged)]
    public async Task Successful_integration_reclaims_the_delivered_task(MergeIntoIntegrationOutcome outcome)
    {
        var reclaimed = new ReclaimRecorder();
        var coordinator = Coordinator(_ => MergeIntoIntegrationResult.Of(outcome, mergedSha: new string('a', 40)), reclaimed);

        var result = await coordinator.EnqueueAsync(Request("integrated")).WaitAsync(Deadline);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(new[] { "integrated" }, await reclaimed.WaitForFirstAsync());
    }

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.GateFailed)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict)]
    [InlineData(MergeIntoIntegrationOutcome.Error)]
    public async Task Failed_integration_never_reclaims(MergeIntoIntegrationOutcome outcome)
    {
        var reclaimed = new ReclaimRecorder();
        var coordinator = Coordinator(
            request => request.JobId == "sentinel"
                ? MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('b', 40))
                : outcome == MergeIntoIntegrationOutcome.Conflict
                    ? MergeIntoIntegrationResult.Conflicted(["shared.txt"], "conflict")
                    : MergeIntoIntegrationResult.Of(outcome, error: "rejected"),
            reclaimed);

        var failed = await coordinator.EnqueueAsync(Request("rejected")).WaitAsync(Deadline);
        // The per-project drain is serial, so once the later sentinel delivery
        // has reclaimed, the rejected one has long been finished.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.EnqueueAsync(Request("sentinel")).WaitAsync(Deadline);

        Assert.Equal(outcome, failed.Outcome);
        Assert.Equal(new[] { "sentinel" }, await reclaimed.WaitForFirstAsync());
    }

    [Fact]
    public async Task Delivery_gate_failure_before_integration_never_reclaims()
    {
        var reclaimed = new ReclaimRecorder();
        var recordedFailures = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            request => Task.FromResult(MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Merged, mergedSha: new string('b', 40))),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            recordFailure: (request, code, _, _) => recordedFailures.Add(request.JobId + ":" + code),
            reclaimAfterIntegration: reclaimed.Record);

        coordinator.RecordGateFailure(Request("gate-red"), "build-tests failed");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.EnqueueAsync(Request("sentinel")).WaitAsync(Deadline);

        Assert.Equal(new[] { "gate-red:" + AcceptedIntegrationFailureCodes.DeliveryGateFailed }, recordedFailures);
        Assert.Equal(new[] { "sentinel" }, await reclaimed.WaitForFirstAsync());
    }

    [Fact]
    public async Task Reclaim_failure_never_turns_a_landed_merge_into_an_integration_failure()
    {
        var recordedFailures = new List<string>();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Merged, mergedSha: new string('a', 40))),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            recordFailure: (request, code, _, _) => recordedFailures.Add(request.JobId + ":" + code),
            reclaimAfterIntegration: _ =>
            {
                attempted.TrySetResult();
                throw new InvalidOperationException("remote rejected the ref deletion");
            });

        var result = await coordinator.EnqueueAsync(Request("landed")).WaitAsync(Deadline);
        await attempted.Task.WaitAsync(Deadline);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var next = await coordinator.EnqueueAsync(Request("next")).WaitAsync(Deadline);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, next.Outcome);
        Assert.Empty(recordedFailures);
    }

    private static RemoteDeliveryIntegrationCoordinator Coordinator(
        Func<RemoteDeliveryIntegrationRequest, MergeIntoIntegrationResult> integrate,
        ReclaimRecorder reclaimed)
        => new(
            request => Task.FromResult(integrate(request)),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            reclaimAfterIntegration: reclaimed.Record);

    private RemoteDeliveryIntegrationRequest Request(string jobId)
        => new(
            "project",
            jobId,
            "/task/" + jobId,
            "/project",
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            _clock.GetUtcNow());

    /// <summary>
    /// The reclaim runs on the drain after the caller already has its result,
    /// so the tests wait for it instead of reading it right after the await.
    /// </summary>
    private sealed class ReclaimRecorder
    {
        private readonly object _gate = new();
        private readonly List<string> _jobIds = [];
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(RemoteDeliveryIntegrationRequest request)
        {
            lock (_gate) _jobIds.Add(request.JobId);
            _first.TrySetResult();
        }

        public async Task<string[]> WaitForFirstAsync()
        {
            await _first.Task.WaitAsync(Deadline);
            lock (_gate) return [.. _jobIds];
        }
    }
}
