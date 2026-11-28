extern alias Runner;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        // AGT-3079: a no-change plan records its skipped tool aspects. A skip
        // is not a planned build/test gate, so a Pass review stays admissible.
        var noChange = new Contract.ReviewPlanDto(
            [
                new Contract.ReviewCommandDto("aspect-requirement-fit", "requirement-fit", "claude", [],
                    ExecutionKind: Contract.ReviewCommandKinds.AgentAspect),
                new Contract.ReviewCommandDto("aspect-code-quality", "code-quality", "claude", [],
                    ExecutionKind: Contract.ReviewCommandKinds.AgentAspect),
            ],
            ["requirement-fit", "code-quality"],
            IntegrationRef: "develop",
            Preparation: [],
            SkippedAspects:
            [
                new("build-tests", "Result has no changed path against its merge base with develop."),
                new("lint", "Result has no changed path against its merge base with develop."),
            ]);
        var noChangeDecision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            noChange,
            [
                new Contract.ReviewVerdictDto("requirement-fit", "pass", "Verified", "The brief is satisfied."),
                new Contract.ReviewVerdictDto("code-quality", "pass", "Verified", "No change to judge."),
            ]);

        Assert.True(noChangeDecision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.NotApplicable, noChangeDecision.BuildTestGate);
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
        Assert.Equal(RemoteBuildTestGateClass.NonBlocking, decision.BuildTestGate);
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

    [Fact]
    public void Unattributed_environment_concern_passes_grading_and_admission()
    {
        var diagnosis = new Contract.DeliveryFailureDiagnosisResult(
            Contract.DeliveryFailureDiagnosis.Environment, 1,
            [Contract.ReviewUnattributedTestFailurePolicy.EvidencePrefix + "candidate and baseline failed different tests"]);
        var command = new Contract.ReviewCommandDto("verify-tests", "build-tests", "dotnet", ["test"]);
        var comparison = Runner::AgentRunner.BaselineComparison.Create(new string('a', 40),
            ["Product.Tests.Old"], ["Product.Tests.Other"], false, 1) with { Diagnosis = diagnosis };
        var verdict = Runner::AgentRunner.RemoteReviewWorkspace.BaselineVerdict(command, comparison);
        Assert.Equal("concerns", verdict.Status);
        Assert.NotEqual(Contract.ReviewGrade.ProductFailure,
            Contract.ReviewGradingPolicy.Grade([verdict.Status]));
        var admission = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass",
            new Contract.ReviewPlanDto([command], [command.Aspect]), [verdict]);
        Assert.True(admission.ShouldIntegrate, admission.Reason);
        Assert.Contains("different tests", admission.Reason);
        Assert.Contains("verify-tests", admission.Reason);
    }

    [Fact]
    public void Missing_second_planned_gate_is_not_hidden_by_a_pass_in_the_same_aspect()
    {
        var plan = new Contract.ReviewPlanDto([
            new("build", "build-tests", "dotnet", ["build"]),
            new("test", "build-tests", "dotnet", ["test"])], ["build-tests"]);
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", plan,
            [new("build-tests", "pass", "Verified", "Build passed", "command:build")]);
        Assert.False(decision.ShouldIntegrate);
        Assert.Contains("test", decision.Reason);
    }

    [Fact]
    public void An_extra_blocking_verdict_cannot_be_hidden_by_a_passing_sibling()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", Plan("build-tests"),
            [new("build-tests", "pass", "Verified", "green", "command:verify"),
             new("build-tests", "block", "NewTestFailures", "Product.Tests.New failed", "command:other")]);
        Assert.False(decision.ShouldIntegrate);
        Assert.Contains("Product.Tests.New", decision.Reason);
    }

    [Fact]
    public void Genuine_red_refusal_names_step_diagnosis_and_next_action()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "ProductFailure", Plan("build-tests"),
            [new("build-tests", "block", "NewTestFailures", "Product.Tests.New failed", "command:verify")]);
        Assert.False(decision.ShouldIntegrate);
        Assert.Contains("verify", decision.Reason);
        Assert.Contains("Product.Tests.New", decision.Reason);
        Assert.Contains("fix round", decision.Reason);
    }

    private static Contract.ReviewPlanDto Plan(string aspect)
        => new(
            [new Contract.ReviewCommandDto("verify", aspect, "git", ["status", "--short"])],
            [aspect]);

    [Theory]
    [InlineData("Pass")]
    [InlineData("IntegrationBranchDefect")]
    public void Deferred_full_suite_cannot_be_integrated_per_card(string outcome)
    {
        var plan = Plan("review-verification") with { BuildTestDeferredToBatch = true };
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, outcome, plan,
            [new Contract.ReviewVerdictDto("review-verification", "pass", "Verified", "Selected tests passed.")]);
        Assert.False(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Failed, decision.BuildTestGate);
    }

    [Fact]
    public void Planned_review_verification_without_verdict_fails_closed()
    {
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass",
            Plan("review-verification"), []);
        Assert.False(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.Failed, decision.BuildTestGate);
    }
}

public sealed class RemoteDeliveryIntegrationCoordinatorTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Held_delivery_waits_without_a_repair_round_while_unrelated_and_repair_deliveries_integrate()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repaired = false;
        var rounds = 0;
        var attempts = 0;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(request =>
        {
            if (request.JobId == "repair") repaired = true;
            if (request.JobId == "overlap" && !repaired)
            {
                attempts++;
                held.TrySetResult();
                return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.BlockedFailingArea,
                    error: "Quarantine quarantine-one: Product.Tests.Isolated holds backend/Features/Pipeline/PostMerge"));
            }
            return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('a', 40)));
        }, NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
        startAgentRound: (_, _) =>
        {
            rounds++;
            return Task.FromResult(new IntegrationAgentRoundStartResult(false, "Unexpected repair round"));
        }) { HoldRetryDelay = TimeSpan.FromMilliseconds(100) };
        var pending = coordinator.EnqueueAsync(Request("overlap", 1));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged,
            (await coordinator.EnqueueAsync(Request("frontend", 2)).WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged,
            (await coordinator.EnqueueAsync(Request("repair", 3)).WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.True(attempts >= 1);
        Assert.Equal(0, rounds);
    }

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged, 0, RemoteIntegrationContinuationAction.None)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 0, RemoteIntegrationContinuationAction.StartAgentRound)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, 1, RemoteIntegrationContinuationAction.StartAgentRound)]
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
    public async Task Recovery_round_waiting_for_claim_does_not_hold_the_serial_integration_lane()
    {
        var roundQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var roundClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                entered.Add(request.JobId);
                if (request.JobId == "AGT-3988")
                {
                    headEntered.SetResult();
                    await releaseHead.Task;
                    return MergeIntoIntegrationResult.RequiresAgentRound([], "delivery-attribution-ambiguous");
                }
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
            }, NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (_, _) =>
            {
                roundQueued.SetResult();
                // Only enqueueing the steer belongs here. Its eventual claim is independent.
                return Task.FromResult(new IntegrationAgentRoundStartResult(true, "Recovery queued in Ready; awaiting a free runner slot."));
            });
        var head = coordinator.EnqueueAsync(Request("AGT-3988", 1));
        await headEntered.Task;
        var next = coordinator.EnqueueAsync(Request("AGT-3989", 2));
        releaseHead.SetResult();
        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, (await head).Outcome);
        await roundQueued.Task;
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await next).Outcome);
        Assert.False(roundClaimed.Task.IsCompleted);
        Assert.Equal(["AGT-3988", "AGT-3989"], entered);
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
    public async Task EnqueueAsync_RecoveryBudgetSpent_ProjectsExactParkReason()
    {
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(MergeIntoIntegrationResult.RequiresAgentRound(
                ["shared.txt"],
                "Merge into develop conflicted.")),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (_, _) => Task.FromResult(
                new IntegrationAgentRoundStartResult(
                    false,
                    "automatic recovery budget used: 2/2")
                {
                    BudgetUsed = 2,
                    BudgetLimit = 2,
                    BudgetExhausted = true,
                }));

        var result = await coordinator.EnqueueAsync(Request("spent", 1));

        Assert.Equal("automatic recovery budget used: 2/2", result.AutomaticRecoveryDetail);
        Assert.Equal(2, result.AutomaticRecoveryBudgetUsed);
        Assert.Equal(2, result.AutomaticRecoveryBudgetLimit);
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

    [Fact]
    public async Task EnqueueAsync_InfrastructureRetryBypassesCompletedFailureWithinRetention()
    {
        var calls = 0;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(++calls == 1
                ? MergeIntoIntegrationResult.Failed("integration-error", "host timeout")
                : MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged,
                    mergedSha: new string('a', 40))),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);
        var request = Request("infrastructure-retry", 1);

        var first = await coordinator.EnqueueAsync(request);
        var retry = await coordinator.EnqueueAsync(request, discardCompletedReplay: true);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, first.Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, retry.Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Queued_delivery_from_superseded_review_never_starts_integration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var current = true;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                calls.Add(request.JobId);
                if (request.JobId == "first")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged,
                    mergedSha: new string('a', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            isCurrentReview: request => request.ReviewAttemptId is null || current);

        var first = coordinator.EnqueueAsync(Request("first", 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stale = coordinator.EnqueueAsync(Request("second", 2) with
        {
            ReviewAttemptId = "review-old",
        });
        current = false;
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        var skipped = await stale.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first"], calls);
        Assert.Equal(MergeIntoIntegrationOutcome.Error, skipped.Outcome);
        Assert.Equal("superseded-review-generation", skipped.Error);
    }

    /// <summary>
    /// AGT-2936 code-quality: the merge runner's publication fence is the commit
    /// point. A successor created after the fence admitted the publication must
    /// not relabel the published merge as a superseded error.
    /// </summary>
    [Fact]
    public async Task Successor_after_a_published_merge_keeps_the_true_merge_result()
    {
        var current = true;
        var mergedSha = new string('a', 40);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ =>
            {
                current = false;
                return Task.FromResult(MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Merged, mergedSha: mergedSha));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            isCurrentReview: _ => current);

        var result = await coordinator.EnqueueAsync(Request("published", 1) with { ReviewAttemptId = "review-old" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(mergedSha, result.MergedSha);
    }

    [Fact]
    public async Task Superseded_conflict_starts_no_agent_round()
    {
        var current = true;
        var rounds = 0;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            _ =>
            {
                current = false;
                return Task.FromResult(MergeIntoIntegrationResult.Conflicted(["a.txt"], "conflict"));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (_, _) =>
            {
                rounds++;
                return Task.FromResult(new IntegrationAgentRoundStartResult(true, "started"));
            },
            isCurrentReview: _ => current);

        var result = await coordinator.EnqueueAsync(Request("conflicted", 1) with { ReviewAttemptId = "review-old" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MergeIntoIntegrationOutcome.Conflict, result.Outcome);
        Assert.Equal(0, rounds);
    }

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Conflict)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired)]
    public async Task Running_monolith_delivery_does_not_start_agent_round_after_engine_takes_ownership(
        MergeIntoIntegrationOutcome outcome)
    {
        var clock = new FakeTimeProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rounds = 0;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return outcome == MergeIntoIntegrationOutcome.Conflict
                    ? MergeIntoIntegrationResult.Conflicted(["a.txt"], "conflict")
                    : MergeIntoIntegrationResult.RequiresAgentRound(["a.txt"], "agent round required");
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            startAgentRound: (_, _) =>
            {
                rounds++;
                return Task.FromResult(new IntegrationAgentRoundStartResult(true, "started"));
            },
            configuration: configuration);
        var request = new RemoteDeliveryIntegrationRequest(
            "project", "owner-switch-" + outcome, "/task/owner-switch-" + outcome,
            "/project", "develop", IntegrationStrategies.DirectMerge, PipelineTypes.Task,
            clock.GetUtcNow(), ReviewAttemptId: "review-owner-switch", MonolithOwnedResume: true);

        var pending = coordinator.EnqueueAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        configuration["Orchestration:Transitions:ReviewReport"] = "Engine";
        release.SetResult();
        Assert.Equal(outcome, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(0, rounds);

        configuration["Orchestration:Transitions:ReviewReport"] = "Monolith";
        Assert.Equal(outcome,
            (await coordinator.EnqueueAsync(request, discardCompletedReplay: true)
                .WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(1, rounds);
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


public sealed class IntegrationRecoveryHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recovery-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TimelineLog _timeline;
    private const string Id = "AGT-4097-fixture";
    private readonly ReviewSubjectRecord _subject;

    public IntegrationRecoveryHandoffTests()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_root, state));
        var folder = TaskStorageLayout.JobDir(_root, 0, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            id = Id, key = Id, title = "Repair shared behavior", state = TaskStates.AutoReview,
            agent = "codex", cliType = "codex", model = "gpt-6-sol", taskType = "bug", order = 1,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Preserve the shared behavior.");
        _subject = Subject('a');
        ReviewSubjectStore.Write(folder, _subject);
        TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["WatchPaths:0:Name"] = "fixture", ["WatchPaths:0:Path"] = _root }).Build();
        _scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        _timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new AgentStudio.Registry.ProjectRegistry(config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
            notifier, NullLogger<TaskMutationService>.Instance);
        _states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance,
            notifier: notifier, timeline: _timeline);
    }

    [Fact]
    public async Task AutomaticRoundReplacesStaleReviewBaseBeforeReadyAndChargesOnlyValidHandoff()
    {
        var job = Job();
        ContinuationBaseStore.Save(job.FolderPath, new ContinuationBaseRecord("refs/heads/old", new string('c', 40),
            "review-fix-round", "old-attempt", DateTime.UnixEpoch));
        var result = await Service().TryStartAsync(Request(job), Conflict());
        Assert.True(result.Started, result.Reason);
        var queued = Job();
        var pin = Assert.IsType<ContinuationBaseRecord>(ContinuationBaseStore.Read(queued.FolderPath));
        Assert.Equal(_subject.ImmutableResultRef, pin.Ref);
        Assert.Equal(_subject.ResultSha, pin.CommitSha);
        Assert.Equal("continue-round-result", pin.Reason);
        Assert.Equal(_subject.RunAttemptId, pin.AttemptId);
        Assert.Contains("agent-studio-integration-preparation.json", queued.PendingIntent!.Prompt);
        Assert.Equal(1, IntegrationRecoveryBudget.Count(_timeline.ReadAll(queued.FolderPath), _subject, "develop").Used);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task ConcernPassConflictResumedFallbackFreshClaimRetainsBothDeliveryCommitsAndPreparation()
    {
        using var fixture = new IntegrationPreparationFixtureTests();
        var (workspace, delivery, target) = await fixture.Fixture(true, concernRound: true);
        var first = (await IntegrationPreparationFixtureTests.Git(workspace.SharedRepoPath, true,
            "rev-parse", delivery + "^")).Output;
        var job = Job();
        // The bounded concern round claimed the first delivery as a review fix.
        ContinuationBaseStore.Save(job.FolderPath, new ContinuationBaseRecord("refs/heads/results/first",
            first, "review-fix-round", "review-concern", DateTime.UnixEpoch));
        Assert.Equal(first, ContinuationBaseStore.ConsumeForClaim(job.FolderPath)!.CommitSha);
        ContinuationBaseStore.ClearAfterCompletion(job.FolderPath);
        // The second attempt passed review, then integration conflicted.
        var passed = _subject with { ResultSha = delivery, ImmutableResultRef = "refs/heads/results/delivery" };
        ReviewSubjectStore.Write(job.FolderPath, passed);
        var conflict = Conflict() with { PreviousIntegrationSha = target, ConflictedFiles = ["shared.txt"] };
        Assert.True((await Service().TryStartAsync(Request(job), conflict)).Started);
        var folder = Job().FolderPath;
        var resumed = ContinuationBaseStore.ConsumeForClaim(folder)!;
        Assert.True(resumed.RetainUntilIntegrated);
        Assert.Equal(delivery, resumed.CommitSha);
        var reason = Runner::AgentRunner.MechanicalRoundFallbackPolicy.Reason(true,
            Runner::AgentRunner.RunOutcomeKind.Unknown, Contract.ExecutionOutcomeKind.Timeout, "Runner timeout");
        Assert.Equal("resume-duration-ceiling", reason);
        ContinuationBaseStore.ClearAfterCompletion(folder);
        Assert.True(ContinuationBaseStore.SaveAttemptResult(folder,
            new ContinuationBaseRecord("refs/heads/empty-result", target, reason!, "resumed", DateTime.UnixEpoch)));
        var fresh = ContinuationBaseStore.ConsumeForClaim(folder)!;
        Assert.Equal(resumed, fresh);
        // Use the production runner preparation with the claim's immutable ref and SHA.
        Assert.Equal("refs/heads/results/delivery", fresh.Ref);
        workspace = fixture.CreateWorkspace(fresh);
        await workspace.PrepareAsync(default);
        await IntegrationPreparationFixtureTests.Git(workspace.RepoPath, true, "merge-base", "--is-ancestor", first, "HEAD");
        await IntegrationPreparationFixtureTests.Git(workspace.RepoPath, true, "merge-base", "--is-ancestor", fresh.CommitSha, "HEAD");
        Assert.Equal(target, (await IntegrationPreparationFixtureTests.Git(workspace.RepoPath, true,
            "rev-parse", "MERGE_HEAD")).Output);
        var recordPath = (await IntegrationPreparationFixtureTests.Git(workspace.RepoPath, true,
            "rev-parse", "--git-path", "agent-studio-integration-preparation.json")).Output;
        using var record = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(recordPath, workspace.RepoPath)));
        Assert.Equal(delivery, record.RootElement.GetProperty("deliverySha").GetString());
        Assert.Equal("shared.txt", record.RootElement.GetProperty("unresolvedFiles")[0].GetString());
        Assert.True(File.Exists(Path.Combine(workspace.RepoPath, "concern-fix.txt")));
    }

    [Fact]
    public async Task NeedsInputOrEmptyResultCannotReplaceUnintegratedReviewedDelivery()
    {
        Assert.True((await Service().TryStartAsync(Request(Job()), Conflict())).Started);
        var folder = Job().FolderPath;
        var delivery = ContinuationBaseStore.ConsumeForClaim(folder)!;
        ContinuationBaseStore.ClearAfterCompletion(folder); // NeedsInput settlement
        Assert.True(ContinuationBaseStore.SaveAttemptResult(folder, new ContinuationBaseRecord(
            "refs/heads/empty", new string('e', 40), "continue-round-result", "needs-input", DateTime.UnixEpoch)));
        var next = ContinueRoundBasePolicy.Decide(null,
            [new CardResultRef("refs/heads/empty", new string('e', 40), "needs-input", DateTime.UnixEpoch.AddMinutes(1))],
            ContinuationBaseStore.Read(folder), true, DateTime.UnixEpoch.AddMinutes(2));
        Assert.Equal(delivery, next.Base);
        Assert.Equal(delivery, ContinuationBaseStore.ConsumeForClaim(folder));
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false, _ => [], _ => []);
        recovery.Record(Request(Job()), MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged));
        Assert.Null(ContinuationBaseStore.Read(folder));
    }

    [Fact]
    public async Task UnwritableBaseNeverConsumesTheRecoveryBudgetOrSupersedesTheDelivery()
    {
        var job = Job();
        Directory.CreateDirectory(Path.Combine(job.FolderPath, ContinuationBaseStore.FileName));
        var result = await Service().TryStartAsync(Request(job), Conflict());
        Assert.False(result.Started);
        Assert.Contains("no round was charged", result.Reason);
        Assert.Equal(TaskStates.AutoReview, Job().State);
        Assert.Equal(_subject.ResultSha, ReviewSubjectStore.Read(job.FolderPath)!.ResultSha);
        Assert.Equal(0, IntegrationRecoveryBudget.Count(_timeline.ReadAll(job.FolderPath), _subject, "develop").Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffReservesCapacityAndChargesOnlyTheExactWorkerStartReceipt(bool started)
    {
        Assert.True((await Service().TryStartAsync(Request(Job()), Conflict())).Started);
        var job = Job();
        var handoff = Assert.Single(_timeline.ReadAll(job.FolderPath),
            row => row.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
        var hash = handoff.Details![IntegrationRecoveryBudget.HandoffPromptKey];
        var savedAt = handoff.Details[IntegrationRecoveryBudget.HandoffSavedAtKey];
        IntegrationRecoveryBudgetUsage Usage() => IntegrationRecoveryBudget.Count(
            _timeline.ReadAll(job.FolderPath), _subject, "develop");
        Assert.Equal(1, Usage().ReservedRounds);
        Assert.Equal(0, Usage().ChargedRounds);

        // A different saved intent, even with the same text, cannot charge it.
        _timeline.Append(job.FolderPath, TimelineEventKinds.FollowUpConsumed, TimelineActors.System,
            "Older handoff", runId: "older-attempt", details: new()
            { ["promptSha256"] = hash, ["savedAt"] = "older-save" });
        Assert.Equal(0, Usage().ChargedRounds);
        if (started)
        {
            _timeline.Append(job.FolderPath, TimelineEventKinds.FollowUpConsumed, TimelineActors.System,
                "Fenced worker-start acknowledgement", runId: "claimed-attempt", details: new()
                { ["promptSha256"] = hash, ["savedAt"] = savedAt });
            Assert.Equal(1, Usage().ChargedRounds);
            Assert.Equal(0, Usage().ReservedRounds);
        }

        _timeline.Append(job.FolderPath, TimelineEventKinds.FollowUpSuperseded, TimelineActors.System,
            "The operator replaced this follow-up", details: new()
            { ["promptSha256"] = hash, ["savedAt"] = savedAt });
        Assert.Equal(started ? 1 : 0, Usage().Used);
        Assert.Equal(started ? 1 : 0, Usage().ChargedRounds);
        Assert.Equal(0, Usage().ReservedRounds);
    }

    [Fact]
    public async Task ExhaustionAcrossChangedShasCreatesOneDurablePinnedRepairAndKeepsHumanHolds()
    {
        var job = Job();
        for (var i = 0; i < 2; i++)
            _timeline.Append(job.FolderPath, TimelineEventKinds.IntegrationRecoveryQueued, TimelineActors.System,
                "Earlier automatic handoff", details: new()
                {
                    ["automatic"] = "true", ["integrationBranch"] = "develop",
                    [IntegrationRecoveryBudget.DeliveryChainIdKey] = "old-delivery@" + i,
                    ["resultSha"] = new string((char)('c' + i), 40),
                });
        var result = await Service().TryStartAsync(Request(job), Conflict());
        Assert.False(result.Started);
        Assert.True(result.BudgetExhausted, result.Reason);
        Assert.Contains("Delivery quarantined; repair:", result.Reason);
        var repair = Assert.Single(_scanner.ScanAutomationJobsWithArchive(_root), item => item.Id != Id);
        Assert.Equal(TaskStates.Ready, repair.State);
        var pin = Assert.IsType<ContinuationBaseRecord>(ContinuationBaseStore.Read(repair.FolderPath));
        Assert.Equal(_subject.ImmutableResultRef, pin.Ref);
        Assert.Equal(_subject.ResultSha, pin.CommitSha);
        var quarantine = File.ReadAllText(Path.Combine(job.FolderPath, "integration-delivery-quarantine.json"));
        Assert.Contains(_subject.ResultSha, quarantine);
        Assert.Contains(new string('b', 40), quarantine);
        Assert.Contains("shared.cs", quarantine);
        Assert.Equal(_subject.ResultSha, ReviewSubjectStore.Read(job.FolderPath)!.ResultSha);
        Assert.Equal(2, IntegrationRecoveryBudget.Count(_timeline.ReadAll(job.FolderPath), _subject, "develop").Used);

        // Restart/replay, then a changed delivery and target, never mint another
        // repair or discard a person's review hold on the existing one.
        Assert.True((await Service().TryStartAsync(Request(job), Conflict())).BudgetExhausted);
        var move = _states.MoveJob(repair.Id, TaskStates.HumanReview, _root, expectedSourceState: TaskStates.Ready);
        Assert.Equal(MoveJobStatus.Success, move.Status);
        ReviewSubjectStore.Write(job.FolderPath, Subject('e'));
        Assert.True((await Service().TryStartAsync(Request(job), Conflict() with { PreviousIntegrationSha = new string('f', 40) })).BudgetExhausted);
        Assert.Single(_scanner.ScanAutomationJobsWithArchive(_root), item => item.Id != Id);
        Assert.Equal(TaskStates.HumanReview, _scanner.FindJob(repair.Id, _root)!.State);
    }

    [Fact]
    public async Task LegacyStoreRetainsAllCardsAndADurableRequestWithoutImplicitMigration()
    {
        var original = Job();
        var legacy = Path.Combine(_root, TaskStates.AutoReview, Id);
        Directory.Move(original.FolderPath, legacy);
        Directory.Delete(TaskStorageLayout.IndexRoot(_root), true);
        _scanner.InvalidateCache();
        var job = Job();
        for (var i = 0; i < 2; i++)
            _timeline.Append(job.FolderPath, TimelineEventKinds.IntegrationRecoveryQueued, TimelineActors.System,
                "Legacy automatic round", details: new() { ["automatic"] = "true" });
        var result = await Service().TryStartAsync(Request(job), Conflict());
        Assert.True(result.BudgetExhausted, result.Reason);
        Assert.Equal(TaskStates.AutoReview, Job().State);
        Assert.Empty(TaskStorageLayout.EnumerateJobDirs(_root));
        var quarantine = File.ReadAllText(Path.Combine(legacy, "integration-delivery-quarantine.json"));
        Assert.Contains("deferred-storage-migration", quarantine);
        Assert.Contains("repairRequest", quarantine);
        Assert.Contains(_subject.ImmutableResultRef!, quarantine);
    }

    [Fact]
    public void ManualRoundRecoversDurableConflictFactsAndPinsImmutableDelivery()
    {
        var job = Job();
        Assert.Null(IntegrationConflictRecovery.PrepareQueued(Request(job), _subject.ResultSha, Conflict()));
        var service = new TaskIntegrationRecoveryService(_scanner, _mutations, _states, _timeline,
            NullLogger<TaskIntegrationRecoveryService>.Instance);
        var result = service.Queue(job, new TaskIntegrationStatus
        { IntegrationBranch = "develop", Status = IntegrationStatuses.ConflictSkipped }, "merge-conflict", "operator");
        Assert.True(result.Queued, result.Error);
        var queued = Job();
        Assert.Contains("shared.cs", queued.PendingIntent!.Prompt);
        Assert.Contains(new string('b', 40), queued.PendingIntent.Prompt);
        Assert.Equal(_subject.ImmutableResultRef, ContinuationBaseStore.Read(queued.FolderPath)!.Ref);
        Assert.Equal(0, IntegrationRecoveryBudget.Count(_timeline.ReadAll(queued.FolderPath), _subject, "develop").Used);
    }

    private TaskInfo Job() => _scanner.FindJob(Id, _root)!;
    private IntegrationAgentRoundService Service() => new(_scanner, _mutations, _states, _timeline,
        NullLogger<IntegrationAgentRoundService>.Instance);
    private RemoteDeliveryIntegrationRequest Request(TaskInfo job) => new("fixture", job.Id, job.FolderPath,
        _root, "develop", IntegrationStrategies.DirectMerge, PipelineTypes.Task, DateTimeOffset.UnixEpoch);
    private static ReviewSubjectRecord Subject(char sha) => new()
    {
        TaskKey = Id, RunAttemptId = "run-" + sha, AttemptChainId = "chain-" + sha, ResultSha = new string(sha, 40),
        ResultRef = "refs/heads/mutable-" + sha, ImmutableResultRef = "refs/heads/results/" + sha,
        CompletedAtUtc = DateTimeOffset.UnixEpoch,
    };
    private static MergeIntoIntegrationResult Conflict() => new(MergeIntoIntegrationOutcome.AgentRoundRequired,
        null, "Net-change replay conflicted.", ["shared.cs"], [], new string('b', 40));
    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class RemoteReviewMeasurementContractTests
{
    private static readonly Contract.ReviewCommandDto Lint = new("lint", "build-tests", "sh", ["lint.sh"],
        CompareToBaseline: true, BaselineMode: Contract.ReviewBaselineModes.ExitStatus);
    private static readonly Contract.ReviewPlanDto Plan = new([Lint], ["build-tests"]);

    [Fact]
    public void Unattributed_test_diagnosis_survives_baseline_verdict_settlement_grading_and_admission()
    {
        var command = Lint with { FileName = "dotnet", Arguments = ["test"], BaselineMode = Contract.ReviewBaselineModes.TestFailures };
        var plan = new Contract.ReviewPlanDto([command], [command.Aspect]);
        var diagnosis = new Contract.DeliveryFailureDiagnosisResult(Contract.DeliveryFailureDiagnosis.Environment, 1,
            [Contract.ReviewUnattributedTestFailurePolicy.EvidencePrefix + "candidate and baseline failed different tests"]);
        var comparison = Runner::AgentRunner.BaselineComparison.Create(new string('a', 40),
            ["Product.Tests.Base"], ["Product.Tests.Other"], false, 1) with { Diagnosis = diagnosis };
        var verdict = Runner::AgentRunner.RemoteReviewWorkspace.BaselineVerdict(command, comparison);
        Assert.Equal("concerns", verdict.Status);
        var raw = RedBaselineReport();
        var settled = ReviewMeasurementPolicy.NormalizeReport(raw with
        {
            Commands = raw.Commands.Select(item => item with { Diagnosis = diagnosis }).ToArray(), Verdicts = [verdict],
        }, plan);
        Assert.Equal("Pass", settled.Outcome);
        Assert.Equal("concerns", Assert.Single(settled.Verdicts).Status);
        Assert.Equal("Pass", ReviewMeasurementPolicy.Outcome(plan, settled.Verdicts));
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, settled.Outcome, plan, settled.Verdicts);
        Assert.True(decision.ShouldIntegrate, decision.Reason);
        Assert.Equal(RemoteBuildTestGateClass.NonBlocking, decision.BuildTestGate);
        Assert.Contains("Product.Tests.Other", settled.Summary);
    }

    [Fact]
    public void Environment_on_red_baseline_settles_as_admissible_branch_debt()
    {
        var settled = ReviewMeasurementPolicy.NormalizeReport(RedBaselineReport(), Plan);
        Assert.Equal("IntegrationBranchDefect", settled.Outcome);
        Assert.Null(settled.FailureClassification);
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, settled.Outcome, Plan, settled.Verdicts);
        Assert.True(decision.ShouldIntegrate, decision.Reason);
        Assert.Equal(RemoteBuildTestGateClass.NonBlocking, decision.BuildTestGate);
        Assert.Contains("lint", settled.Summary);
        Assert.Contains("baseline", settled.Summary);
        Assert.Contains("Next automatic action", settled.Summary);
    }

    [Fact]
    public void Product_finding_outranks_environment_and_reaches_the_fix_round()
    {
        var report = RedBaselineReport();
        var aspect = new Contract.ReviewCommandDto("fit", "requirement-fit", "agent", [], ExecutionKind: Contract.ReviewCommandKinds.AgentAspect);
        var plan = Plan with { Commands = [Lint, aspect], RequiredAspects = ["build-tests", "requirement-fit"] };
        var product = new Contract.ReviewVerdictDto("requirement-fit", "block", "RemoteAspectVerdict",
            "Missing required behavior", "src/product.cs:12", "required behavior");
        var settled = ReviewMeasurementPolicy.NormalizeReport(report with
        {
            Verdicts = [.. report.Verdicts, product],
            Workspace = report.Workspace with { ChangedPaths = ["src/product.cs"] },
        }, plan);
        Assert.Equal("ProductFailure", settled.Outcome);
        Assert.Equal(Contract.DeliveryFailureDiagnosis.Product, settled.FailureClassification);
        Assert.Contains("fix round", settled.Summary);
    }

    [Theory]
    [InlineData("TestQuarantine")]
    [InlineData("FlakyQuarantine")]
    public void Quarantine_keeps_its_limitation_and_admits(string classification)
    {
        Contract.ReviewVerdictDto[] verdicts = [new("build-tests", "pass", classification,
            "Product.Tests.Flake quarantined until 2026-12-01", "command:lint")];
        Assert.Equal("Pass", ReviewMeasurementPolicy.Outcome(Plan, verdicts));
        var decision = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", Plan, verdicts);
        Assert.True(decision.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.NonBlocking, decision.BuildTestGate);
        Assert.Contains("Product.Tests.Flake", decision.Reason);
    }

    [Fact]
    public void Quarantine_only_failed_command_settles_and_admits_with_its_test_name()
    {
        var raw = RedBaselineReport();
        var failure = raw.Commands[0] with
        {
            Diagnosis = null, BaselineSha = null, BaselineExitCode = null,
            InitialFailures = ["Product.Tests.Flake"],
            QuarantinedFailures = [new("Product.Tests.Flake", "REPAIR-1", new DateOnly(2026, 12, 1))],
        };
        var settled = ReviewMeasurementPolicy.NormalizeReport(raw with
        {
            Outcome = "Pass", FailureClassification = null, Commands = [failure],
            Verdicts = [new("build-tests", "pass", "TestQuarantine", "Product.Tests.Flake quarantined until 2026-12-01", "command:lint")],
        }, Plan);
        Assert.Equal("Pass", settled.Outcome);
        Assert.True(RemoteDeliveryIntegrationPolicy.Decide(true, settled.Outcome, Plan, settled.Verdicts).ShouldIntegrate);
        Assert.Contains("Product.Tests.Flake", settled.Summary);
    }

    [Fact]
    public void Genuine_product_failure_missing_verdict_and_not_applicable_are_distinct()
    {
        Assert.Equal("ProductFailure", ReviewMeasurementPolicy.Outcome(Plan,
            [new("build-tests", "block", "NewTestFailures", "Product.Tests.New", "command:lint")]));
        Assert.Equal(ReviewMeasurementDisposition.MissingVerdict,
            Assert.Single(ReviewMeasurementPolicy.Measurements(Plan, [])).Disposition);
        Assert.Equal("ReviewInfra", ReviewMeasurementPolicy.Outcome(Plan, []));
        var na = RemoteDeliveryIntegrationPolicy.Decide(true, "Pass", Plan,
            [new("build-tests", "not-applicable", "NotApplicable", "No matching platform", "command:lint")]);
        Assert.True(na.ShouldIntegrate);
        Assert.Equal(RemoteBuildTestGateClass.NotApplicable, na.BuildTestGate);
    }

    [Fact]
    public void Green_baseline_environment_is_still_infrastructure_and_absent_proof_is_not_branch_debt()
    {
        var red = RedBaselineReport();
        var green = red with { Commands = red.Commands.Select(command => command with { BaselineExitCode = 0 }).ToArray() };
        Assert.Equal("ReviewInfra", ReviewMeasurementPolicy.NormalizeReport(green, Plan).Outcome);
        var missing = red with { Commands = [red.Commands[0]] };
        Assert.Equal("ReviewInfra", ReviewMeasurementPolicy.NormalizeReport(missing, Plan).Outcome);
    }

    [Fact]
    public void Review_infrastructure_successor_cannot_reuse_failed_attempt_measurements()
    {
        Assert.False(ReviewMeasurementPolicy.MayReuseResult(["failed-attempt"]));
        Assert.True(ReviewMeasurementPolicy.MayReuseResult(null));
    }

    internal static Contract.ReviewReportRequest RedBaselineReport()
    {
        var diagnosis = new Contract.DeliveryFailureDiagnosisResult(Contract.DeliveryFailureDiagnosis.Environment, 1,
            ["lint failed on delivery and baseline"]);
        var command = new Contract.ReviewCommandEvidenceDto("lint", "build-tests", "sh", ["lint.sh"],
            "delivery", "delivery", "tree", DateTime.UnixEpoch, DateTime.UnixEpoch,
            1, null, "out", "err", BaselineSha: "base", NewFailures: [], BaselineExitCode: 1, Diagnosis: diagnosis);
        return new("executor", "instance", "lease", 1, "report", "ReviewInfra", "environment", "old summary",
            new("repo", "delivery", "delivery", "tree", false, false, "identity", "namespace"),
            new("host", "executor", "instance", "Linux", "x64", "10", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [command, command with { Phase = "clean-repeat", WorkspaceRole = "clean-repeat" }], [],
            [new("build-tests", "pass", "environment", "lint is red", "command:lint", "none", diagnosis)]);
    }
}

[Trait("Category", "MachineBound")]
public sealed class RemoteReviewTargetMergeTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-target-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "IntegrationBranchDefect", Contract.ReviewBaselineModes.ExitStatus)]
    [InlineData(true, "ProductFailure", Contract.ReviewBaselineModes.ExitStatus)]
    [InlineData(false, "IntegrationBranchDefect", Contract.ReviewBaselineModes.TestFailures)]
    [InlineData(true, "ProductFailure", Contract.ReviewBaselineModes.TestFailures)]
    public async Task Repaired_target_is_measured_on_a_disposable_merge_without_changing_delivery(bool regression, string expected, string baselineMode)
    {
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        await Git(_root, "init", "--bare", origin);
        await Git(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "lint.sh"), "echo '  Failed Product.Tests.Known [1 ms]'\nexit 1\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "-m", "red baseline");
        await Git(seed, "remote", "add", "origin", origin);
        await Git(seed, "push", "origin", "main");
        await Git(seed, "checkout", "-b", "delivery");
        await File.WriteAllTextAsync(Path.Combine(seed, regression ? "regression" : "product"), "delivery\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "-m", "delivery");
        var delivery = await Git(seed, "rev-parse", "HEAD");
        await Git(seed, "push", "origin", "delivery");
        await Git(seed, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "lint.sh"), "if test -f regression; then echo '  Failed Product.Tests.Known [1 ms]'; exit 1; fi\nexit 0\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "-m", "repair target");
        await Git(seed, "push", "origin", "main");
        var command = new Contract.ReviewCommandDto("lint", "build-tests", "sh", ["lint.sh"],
            CompareToBaseline: true, BaselineMode: baselineMode);
        var workspace = Workspace(origin, delivery, [command], "first", null, resultRef: "refs/heads/delivery");
        await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);
        Assert.Equal(expected, evidence.Outcome);
        Assert.Equal(delivery, evidence.Workspace.ActualHead);
        Assert.False(evidence.Workspace.DirtyAfter);
        Assert.Contains(evidence.Commands, item => item.WorkspaceRole == "target-merge" && item.ExitCode == (regression ? 1 : 0));
        Assert.Contains(regression ? "failed twice" : "passed", Assert.Single(evidence.Verdicts).Summary);
        var settled = ReviewMeasurementPolicy.NormalizeReport(RemoteReviewMeasurementContractTests.RedBaselineReport() with
        {
            Outcome = evidence.Outcome, Commands = evidence.Commands, Verdicts = evidence.Verdicts,
            Workspace = evidence.Workspace,
        }, new([command], ["build-tests"]));
        Assert.Equal(expected, settled.Outcome);
    }

    [Fact]
    public async Task Infrastructure_retry_executes_verify_and_baseline_again()
    {
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        await Git(_root, "init", "--bare", origin);
        await Git(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "lint.sh"), "exit 1\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "-m", "baseline");
        var delivery = await Git(seed, "rev-parse", "HEAD");
        await Git(seed, "remote", "add", "origin", origin);
        await Git(seed, "push", "origin", "main");
        Contract.ReviewCommandDto[] commands = [new("green", "build-tests", "sh", ["-c", "exit 0"]),
            new("red", "build-tests", "sh", ["lint.sh"], CompareToBaseline: true, BaselineMode: Contract.ReviewBaselineModes.ExitStatus)];
        var first = Workspace(origin, delivery, commands, "first", null, missingVerdict: true);
        await first.PrepareAsync(null!, default);
        Assert.Equal("ReviewInfra", (await first.ExecutePlanAsync(default)).Outcome);
        var retry = Workspace(origin, delivery, commands, "retry", ["first"]);
        await retry.PrepareAsync(null!, default);
        var evidence = await retry.ExecutePlanAsync(default);
        Assert.All(evidence.Commands, item =>
        {
            Assert.Null(item.VerifyReusedFromAttemptId);
            Assert.Null(item.BaselineReusedFromAttemptId);
        });
        Assert.Contains(evidence.Commands, item => item.StepId == "red" && item.WorkspaceRole.StartsWith("baseline-"));
        Assert.Contains(evidence.Commands, item => item.StepId == "green" && item.WorkspaceRole == "candidate");
    }

    private Runner::AgentRunner.RemoteReviewWorkspace Workspace(string origin, string sha,
        IReadOnlyList<Contract.ReviewCommandDto> commands, string attempt, IReadOnlyList<string>? predecessors, bool missingVerdict = false, string? resultRef = null)
    {
        var subject = new Contract.ReviewSubjectDto("subject", "card", "run",
            Runner::AgentRunner.TaskServerClient.RepositoryIdentity(origin)!, origin, sha,
            resultRef ?? "refs/heads/main", null, null, "coding-host", "policy",
            new(commands, missingVerdict ? ["build-tests", "missing"] : ["build-tests"], IntegrationRef: "main", VerifyReuseFromAttemptIds: predecessors), _clock.GetUtcNow().UtcDateTime);
        // The immutable delivery ref and the independently fetched target remain distinct.
        var options = new Runner::AgentRunner.RunnerOptions
        {
            ServerUrl = "http://localhost", RunnerId = "reviewer", RunnerName = "reviewer", Hostname = "review-host",
            BackendName = "review", Role = "review", WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "review"), ReviewTempBase = Path.Combine(_root, "tmp"),
            BaseBranch = "main", TtlSeconds = 120, HeartbeatSeconds = 30,
        };
        var lease = new Contract.ReviewLeaseDto("lease", attempt, "subject", "reviewer", "instance", "review-host", 1,
            _clock.GetUtcNow().UtcDateTime, _clock.GetUtcNow().UtcDateTime.AddMinutes(2), "active", "review-" + attempt, 24000);
        return new(options, subject, lease, _ => { }, _ => Task.FromResult<Contract.FailureFingerprintHistoryDto?>(null),
            utcNow: () => _clock.GetUtcNow().UtcDateTime);
    }

    private static async Task<string> Git(string path, params string[] args)
    {
        var result = await Runner::AgentRunner.ProcessRunner.RunAsync("git",
            new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid" }.Concat(args).ToArray(), path);
        Assert.True(result.Success, result.StdOut + result.StdErr);
        return result.StdOut.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

public sealed class RemoteReviewOperatorRetryTests : IDisposable
{
    private const string ProjectName = "retry-project";
    private const string TaskKey = "RETRY-1";
    private const string RepositoryUrl = "https://example.invalid/review.git";
    private static readonly string ResultSha = new('a', 40);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "review-retry-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero));

    public RemoteReviewOperatorRetryTests()
    {
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    [Fact]
    public async Task Operator_token_claims_attention_then_retries_the_unchanged_parked_delivery()
    {
        SeedTask(TaskStates.AutoReview, _time.GetUtcNow().UtcDateTime.AddHours(-2));
        using var factory = BuildFactory();
        SeedReviewAttempt(factory.Services);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var pending = authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        var claimed = authority.ClaimReview(pending.AttemptId, "reviewer", "host", 120, "claim").ReviewAttempt!;
        Assert.True(authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "fail"),
            ResultSha, ReviewTerminalOutcome.InfrastructureFailure, "ExecutorRestarted", "review host restarted")).Accepted);
        authority.ScheduleReviewInfrastructureRetry(claimed.AttemptId, "host restarted");
        _time.Advance(TimeSpan.FromMinutes(32));
        Assert.Equal(1, await factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>().RunOnceAsync());
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob(TaskKey, _watchPath)!;
        var before = File.ReadAllBytes(Path.Combine(card.FolderPath, "task.json"));
        var ticks = File.GetLastWriteTimeUtc(Path.Combine(card.FolderPath, "task.json"));
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var password = "Fixture-" + Guid.NewGuid().ToString("N") + "!aA9";
        security.Bootstrap(new BootstrapRequest("owner", password, null));
        var token = security.CreateApiToken(new CreateApiTokenRequest("operator", StudioRoles.Operator,
            [ProjectName], _time.GetUtcNow().UtcDateTime.AddDays(1)), security.ListUsers().Single().Id).Secret;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var attention = await client.PostAsync($"/api/tasks/{TaskKey}/attention?project={ProjectName}", null);
        attention.EnsureSuccessStatusCode();
        var json = await attention.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(card.FolderPath, "task.json")));
        Assert.Equal(ticks, File.GetLastWriteTimeUtc(Path.Combine(card.FolderPath, "task.json")));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{TaskKey}/review/retry?project={ProjectName}");
        request.Headers.TryAddWithoutValidation("If-Match", json.GetProperty("revision").GetString());
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.NotEqual(claimed.AttemptId, authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
        Assert.Equal(ResultSha, authority.GetTaskProjection(TaskKey).CurrentRunAttempt!.ResultSha);
    }

    public void Dispose() { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true); }
    private void SeedReviewAttempt(IServiceProvider services)
    {
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(RepositoryUrl)!;
        var authority = services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(TaskKey, repositoryId, null, "coding-runner", "coding-host", 120,
            "seed-review-run").RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "seed-review-completion"),
            Outcome = "done",
            ResultSha = ResultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId, run.AttemptId, "4136f00d4136f00d4136f00d4136f00d4136f00d", ResultSha,
                "refs/heads/agent-studio/results/stranded-retry", null, new string('a', 64),
                RepositoryUrl: RepositoryUrl),
        }).Accepted);
        Assert.True(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey, repositoryId, ResultSha, run.AttemptId, "requirements", "policy", [],
            "seed-review-attempt", RepositoryUrl: RepositoryUrl)).Accepted);
    }

    private void SeedTask(string state, DateTime enteredLaneAt)
    {
        var folder = Path.Combine(_watchPath, state, TaskKey);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = TaskKey,
            ["key"] = TaskKey,
            ["title"] = "Review host restarted",
            ["state"] = state,
            ["order"] = 1,
            ["agent"] = "claude",
            ["kind"] = TaskKinds.Task,
            ["enteredLaneAt"] = enteredLaneAt,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["Security:Profile"] = "networked",
                        ["AllowedHosts"] = "studio.test",
                        ["Secrets:KeyFile"] = Path.Combine(_workspace, "keys", "secrets.kek"),
                        ["Secrets:KeyEnvironmentVariable"] = "AGT4099_UNUSED_" + Guid.NewGuid().ToString("N"),
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                        ["Runner:ReviewInfrastructureRetry:Enabled"] = "false",
                    }));
                builder.ConfigureTestServices(services =>
                {
                    // The fixture drives the watchdog and the scheduler tick by
                    // tick; no periodic worker may act between two ticks.
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<TimeProvider>(_time);
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(provider => new AttemptAuthorityService(
                        provider.GetRequiredService<IConfiguration>(),
                        provider.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                        () => _time.GetUtcNow().UtcDateTime,
                        provider.GetRequiredService<IAtomicJsonFileWriter>()));
                });
            });
}
