using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class RemoteDeliveryIntegrationPolicyTests
{
    [Fact]
    public void Decide_PassedBuildTestGate_IntegratesSettledEnvelope()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            Plan("build-tests"),
            [new Contract.ReviewVerdictDto(
                "build-tests",
                "pass",
                "GatePassed",
                "Build and tests passed.")]);

        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Passed, decision.BuildTestGate);
    }

    [Theory]
    [InlineData("not-applicable", "NoCommands")]
    [InlineData("skipped", "NoCommands")]
    [InlineData("pass", "NotApplicable")]
    public void Decide_NotApplicableBuildTestClass_RemainsGreen(
        string status,
        string classification)
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            Plan("build-tests"),
            [new Contract.ReviewVerdictDto(
                "build-tests",
                status,
                classification,
                "No build/test command applies.")]);

        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.NotApplicable, decision.BuildTestGate);
    }

    [Fact]
    public void Decide_NoBuildTestAspect_IsNotApplicableRatherThanFailed()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            Plan("completion"),
            [new Contract.ReviewVerdictDto(
                "completion",
                "pass",
                "Verified",
                "The delivery subject exists.")]);

        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.NotApplicable, decision.BuildTestGate);
    }

    [Fact]
    public void Decide_PlannedBuildTestWithoutVerdict_FailsClosed()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            Plan("build-tests"),
            []);

        Assert.False(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Failed, decision.BuildTestGate);
    }

    [Theory]
    [InlineData(false, "Pass", "pass")]
    [InlineData(true, "ProductFailure", "pass")]
    [InlineData(true, "Pass", "block")]
    public void Decide_UnsettledOrRedDelivery_DoesNotIntegrate(
        bool settledEnvelope,
        string outcome,
        string gateStatus)
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            settledEnvelope,
            outcome,
            Plan("build-tests"),
            [new Contract.ReviewVerdictDto(
                "build-tests",
                gateStatus,
                "Gate",
                "Gate result.")]);

        Assert.False(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Failed, decision.BuildTestGate);
    }

    /// <summary>
    /// AGT-2819: the acceptance rail refused every card with "Remote Review ended
    /// with 'ProductFailure', not Pass" while the lint gate was red on develop
    /// itself. A gate that is red on the integration branch does not refuse the
    /// delivery; the branch defect carries its own alert instead.
    /// </summary>
    [Fact]
    public void Decide_IntegrationBranchDefect_IsAdmittedAndNamesTheBranch()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: nameof(ReviewTerminalOutcome.IntegrationBranchDefect),
            Plan("build-tests"),
            [new Contract.ReviewVerdictDto(
                "build-tests",
                "pass",
                "IntegrationBranchDefect",
                "0 new failures; step verify-5 is already exiting 1 on the merge base.")]);

        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Passed, decision.BuildTestGate);
        Assert.Contains("already red on the integration branch", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_ProductFailure_StillRefusesTheDelivery()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: nameof(ReviewTerminalOutcome.ProductFailure),
            Plan("build-tests"),
            [new Contract.ReviewVerdictDto("build-tests", "block", "NewTestFailures", "1 new failure.")]);

        Assert.False(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Failed, decision.BuildTestGate);
    }

    private static Contract.ReviewPlanDto Plan(string aspect)
        => new(
            [new Contract.ReviewCommandDto("verify", aspect, "git", ["status", "--short"])],
            [aspect]);
}

public sealed class RemoteDeliveryIntegrationCoordinatorTests
{
    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged, 0, RemoteIntegrationContinuationAction.None)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 0, RemoteIntegrationContinuationAction.None)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 0, RemoteIntegrationContinuationAction.StartAgentRound)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 1, RemoteIntegrationContinuationAction.StartAgentRound)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, 2, RemoteIntegrationContinuationAction.LeaveForHumanReview)]
    public void ContinuationPolicy_BoundsAutomaticAgentRound(
        MergeIntoIntegrationOutcome outcome,
        int roundsUsed,
        RemoteIntegrationContinuationAction expected)
    {
        Assert.Equal(
            expected,
            RemoteIntegrationContinuationPolicy.Decide(outcome, roundsUsed));
    }

    [Fact]
    public async Task EnqueueAsync_AttributionAmbiguity_StartsAgentRound()
    {
        RemoteDeliveryIntegrationRequest? startedFor = null;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            request => Task.FromResult(MergeIntoIntegrationResult.RequiresAgentRound(
                [],
                "Mechanical rebase changed the delivery commit cardinality.")),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (request, _) =>
            {
                startedFor = request;
                return Task.FromResult(new IntegrationAgentRoundStartResult(true, "steer saved"));
            });

        var result = await coordinator.EnqueueAsync(Request("cardinality", 1));

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
        Assert.Equal("cardinality", startedFor?.JobId);
    }

    [Fact]
    public async Task EnqueueAsync_SpentRecoveryBudgetCarriesExactParkReason()
    {
        const string reason = "automatic recovery budget used: 2/2";
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(MergeIntoIntegrationResult.RequiresAgentRound(
                ["shared.txt"],
                "three-stage conflict")),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (_, _) => Task.FromResult(new IntegrationAgentRoundStartResult(
                false,
                reason,
                BudgetExhausted: true,
                AutomaticRoundsUsed: 2)));

        var result = await coordinator.EnqueueAsync(Request("budget-spent", 1));

        Assert.Equal(reason, result.AutomaticRecoveryParkReason);
    }

    [Fact]
    public async Task EnqueueAsync_SerializesSameProjectInDeliveryOrder()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                order.Add(request.JobId);
                if (request.JobId == "first")
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Merged,
                    mergedSha: new string('a', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);

        var first = coordinator.EnqueueAsync(Request("first", 1));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var third = coordinator.EnqueueAsync(Request("third", 3));
        var second = coordinator.EnqueueAsync(Request("second", 2));

        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        Assert.Equal(["first"], order);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task EnqueueAsync_CoalescesConcurrentReplayOfSameDelivery()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Merged,
                    mergedSha: new string('a', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);

        var request = Request("replayed", 1);
        var first = coordinator.EnqueueAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replay = coordinator.EnqueueAsync(request with
        {
            JobFolderPath = "/task/replayed-after-lane-move",
        });

        Assert.Same(first, replay);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult();
        await Task.WhenAll(first, replay).WaitAsync(TimeSpan.FromSeconds(5));
        var completedReplay = coordinator.EnqueueAsync(request);
        Assert.Same(first, completedReplay);
        Assert.Equal(1, Volatile.Read(ref calls));
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
            new DateTimeOffset(2026, 8, 9, 10, minute, 0, TimeSpan.Zero));
}
