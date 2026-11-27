using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

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
