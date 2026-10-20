using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3218: the applier contract. Every action each rollout profile can
/// complete runs through the real <see cref="MonolithOrchestrationApplier"/>
/// on an engine-completed occurrence, and the effects come from the one
/// <see cref="EngineApplierContract"/> table that the applier canary checks.
/// The shadow streak that switched ReviewReport on 16.10.2026 compared
/// receipts only; these cases prove the applied side effect.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    private const string ContractKey = "DOC-APPLIER";
    private const string GateReasonText = "dotnet test failed in PilotTests.";

    /// <summary>
    /// Each profile action of <see cref="EngineApplierContract.ProfileActions"/>,
    /// a passed gate that holds a batch member (it carries a lane), the sweep
    /// hold, which reaches the applier's no-lane branch, and an accepted review
    /// whose integration handed the card to a review-input decision or, after a
    /// conflict, to an automatic agent round.
    /// </summary>
    internal static readonly (string Profile, string Action, string Variant)[] ApplierContractCases =
    [
        ("CodingCompletion", nameof(TransitionAction.CreateReview), ""),
        ("CodingCompletion", nameof(TransitionAction.ReturnToReady), ""),
        ("CodingCompletion", nameof(TransitionAction.SalvageContinuation), ""),
        ("CodingCompletion", nameof(TransitionAction.Escalate), ""),
        ("CodingCompletion", nameof(TransitionAction.HumanReview), ""),
        ("CodingCompletion", nameof(TransitionAction.Continue), ""),
        ("ReviewReport", nameof(TransitionAction.FindingRound), ""),
        ("ReviewReport", nameof(TransitionAction.ConcernRound), ""),
        ("ReviewReport", nameof(TransitionAction.AcceptAndIntegrate), ""),
        ("ReviewReport", nameof(TransitionAction.AcceptAndIntegrate), ReviewInputHandoffVariant),
        ("ReviewReport", nameof(TransitionAction.AcceptAndIntegrate), AgentRoundVariant),
        ("ReviewReport", nameof(TransitionAction.DeferToBatch), ""),
        ("ReviewReport", nameof(TransitionAction.HumanReview), ""),
        ("ReviewReport", nameof(TransitionAction.Escalate), ""),
        ("ReviewReport", nameof(TransitionAction.ReviewInfrastructureRetry), ""),
        ("ReviewReport", nameof(TransitionAction.DegradeWithFollowUp), ""),
        ("ReviewReport", nameof(TransitionAction.Halt), ""),
        ("GateResult", nameof(TransitionAction.Continue), ""),
        ("GateResult", nameof(TransitionAction.Continue), "batch-hold"),
        ("GateResult", nameof(TransitionAction.ReturnToReady), ""),
        ("GateResult", nameof(TransitionAction.Escalate), ""),
        ("BatchPlan", EngineApplierContract.BatchPlanAction, ""),
        ("OperatorSweep", nameof(TransitionAction.SweepHold), ""),
    ];

    private const string ReviewInputHandoffVariant = "review-input-handoff";

    /// <summary>The integration ends in Conflict, and the real agent round service moves the card to Ready.</summary>
    private const string AgentRoundVariant = "agent-round";

    /// <summary>As <see cref="AgentRoundVariant"/>, but an earlier review of the delivery spent its round.</summary>
    private const string BudgetSpentVariant = "agent-round-budget-spent";

    public static TheoryData<string, string, string> ApplierContractData()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (profile, action, variant) in ApplierContractCases) data.Add(profile, action, variant);
        return data;
    }

    [Theory]
    [MemberData(nameof(ApplierContractData))]
    public async Task Applier_contract_applies_the_effect_of_every_engine_decision(string profile, string action,
        string variant)
    {
        // The engine parks in Human Review without integrating only a delivery
        // that needs no integration (and a batch member after its gate
        // integrated it). The seeded card carries a changed result, so the
        // guarded delivery chain is off for those cases, as in
        // MonolithOrchestrationEndpointTests; the applier's effect is the subject here.
        using var factory = ContractFactory(action, variant);
        var (occurrence, arranged, handoff, lane, owed) = await ApplyContractCaseAsync(factory, profile, action,
            variant);
        var agentRound = variant == AgentRoundVariant;
        Assert.Equal("completed", occurrence.Run.Status);
        Assert.Contains(EngineApplierEffectKind.DecisionReceipt, owed);
        var probe = factory.Services.GetRequiredService<EngineApplierEffectProbe>();
        if (owed.Contains(EngineApplierEffectKind.GateDispatch))
        {
            // The applier only frees the card; the gate handler's next pass
            // dispatches the aspect review, so the canary gives it the apply deadline.
            var before = probe.Check(occurrence);
            Assert.Equal((false, true), (before.Verified, before.AwaitingDownstream));
            Assert.Equal([nameof(EngineApplierEffectKind.GateDispatch)], before.Missing);
            EngineGateDispatchReceipt.Record(factory.Services.GetRequiredService<TimelineLog>(),
                CardFolder(arranged.TaskId), occurrence.Run.RunId);
        }
        foreach (var effect in owed)
            AssertApplied(factory, effect, occurrence, arranged, lane, action, handoff);
        if (handoff is not null || agentRound)
        {
            // The review-input decision or the agent round owns the lane and
            // the next review; this decision records no park verdict.
            AssertLane(arranged.TaskId, agentRound ? TaskStates.Ready : arranged.SourceState);
            Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_workspace, ProjectName), record =>
                record.JobId == arranged.TaskId
                && record.FailureKind == $"remote-review-park:{arranged.ReviewAttemptId}");
        }
        // The canary reads the same effects with its probe.
        var check = probe.Check(occurrence);
        Assert.True(check.Verified, $"{profile}/{action}: {check.Evidence} Errors: {_errors}");
        Assert.Equal(handoff is not null ? EngineEffectBasis.ReviewInputHandoff
            : agentRound ? EngineEffectBasis.AgentRoundHandoff : EngineEffectBasis.Row, check.Basis);
        Assert.Equal(occurrence.Run.CompletedAt, check.CompletedAt);
    }

    /// <summary>
    /// The review of 20.10.2026: an applier that ran before the fix let the
    /// integration start the agent round, settled the journal, then tried to
    /// move the card it read before the integration and failed; every retry
    /// threw orchestration-source-state-mismatch. The state it left is seeded
    /// here: the journal at IntegrationSettled, the wait marker, the card in
    /// Ready. Unapplied, the probe counts the round as this decision's own
    /// effect, not as a refusal. The retry finds the handoff, settles the
    /// journal, clears the wait marker and completes.
    /// </summary>
    [Fact]
    public async Task Applier_completes_a_retried_accept_whose_integration_started_an_agent_round()
    {
        using var factory = ContractFactory(nameof(TransitionAction.AcceptAndIntegrate), AgentRoundVariant);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var arranged = Arrange(factory, "ReviewReport", nameof(TransitionAction.AcceptAndIntegrate), AgentRoundVariant);
        var request = IntegrationRequest(factory, arranged);
        SeedSettlementJournal(factory, arranged, request);
        var result = await factory.Services.GetRequiredService<RemoteDeliveryIntegrationCoordinator>()
            .EnqueueAsync(request).WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(AgentStudio.Git.MergeIntoIntegrationOutcome.Conflict, result.Outcome);
        AssertLane(arranged.TaskId, TaskStates.Ready);
        RemoteDeliverySettlementStore.Advance(CardFolder(arranged.TaskId),
            RemoteDeliverySettlementStage.IntegrationSettled, result.Outcome.ToString(),
            RemoteDeliveryParkReason.For(result));
        var refused = new RefusedOnce(factory.Services.GetRequiredService<IMonolithOrchestrationApplier>());
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "retried-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), refused);
        var run = journal.Create(ProjectName, arranged.TaskId, arranged.Stage, arranged.Payload,
            arranged.IdempotencyKey, arranged.SourceState, arranged.AuthorityAttemptId, arranged.Fence,
            arranged.Epoch, profile: "ReviewReport");
        EngineDecisionWaitMarker.Write(CardFolder(arranged.TaskId), run.RunId);
        var claim = journal.Claim(new Contract.OrchestrationClaimRequest("engine", "instance", [arranged.Stage]));
        var completion = new Contract.CompleteOrchestrationStageRequest("engine", "instance", claim.Lease!.LeaseId,
            claim.Lease.Fence, arranged.Stage, arranged.StageAction, arranged.Output, "complete:" + run.RunId);
        Assert.Equal("orchestration-source-state-mismatch", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.CompleteAsync(run.RunId, completion, CancellationToken.None))).Message);
        Assert.Equal("applying", journal.Get(run.RunId)!.Status);

        var probe = factory.Services.GetRequiredService<EngineApplierEffectProbe>();
        var unapplied = probe.Check(journal.GetOccurrence(run.RunId)!);
        Assert.Equal((EngineEffectBasis.Row, (string?)null), (unapplied.Basis, unapplied.WaitingOn));
        Assert.Equal([nameof(EngineApplierEffectKind.DecisionReceipt)], unapplied.Missing);
        var roundAt = Assert.IsType<DateTime>(unapplied.IntegrationEndedAt);
        var observedAt = roundAt.AddHours(-3);
        Assert.Equal(ApplierCanaryVerdict.Waiting, ApplierCanaryPolicy.Judge(unapplied, observedAt,
            roundAt.AddMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
        Assert.Equal(ApplierCanaryVerdict.Missing, ApplierCanaryPolicy.Judge(unapplied, observedAt,
            roundAt.AddMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));

        await journal.RecoverApplyingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal("completed", journal.Get(run.RunId)!.Status);
        Assert.Null(EngineDecisionWaitMarker.Read(CardFolder(arranged.TaskId)));
        Assert.Equal(RemoteDeliverySettlementStage.LaneSettled,
            RemoteDeliverySettlementStore.Read(CardFolder(arranged.TaskId))!.Stage);
        AssertLane(arranged.TaskId, TaskStates.Ready);
        var check = probe.Check(journal.GetOccurrence(run.RunId)!);
        Assert.True(check.Verified, check.Evidence);
        Assert.Equal(EngineEffectBasis.AgentRoundHandoff, check.Basis);
    }

    /// <summary>
    /// The review of 20.10.2026: two reviews of one delivery share its
    /// automatic agent rounds (MaxAutomaticAgentRounds = 1). The second
    /// review's integration conflicts again with the budget spent, so the
    /// round service leaves the card in Auto Review for Human Review. The
    /// first review's round row names the same SHA, but it is older than this
    /// review's enqueue, and the result spent the budget: the applier moves
    /// the card to Human Review with its park verdict.
    /// </summary>
    [Fact]
    public async Task Applier_parks_a_second_review_whose_integration_conflicts_with_the_round_budget_spent()
    {
        // The guarded delivery chain would refuse the park of the fixture's
        // unintegrated result, as for the other Human Review contract cases.
        using var factory = BuildFactory(configuration: UnguardedDeliveryChain,
            testServices: ConflictingIntegration);
        var (occurrence, arranged, _, lane, owed) = await ApplyContractCaseAsync(factory, "ReviewReport",
            nameof(TransitionAction.AcceptAndIntegrate), BudgetSpentVariant, SeedEarlierRound);

        Assert.Equal("completed", occurrence.Run.Status);
        Assert.Equal(TaskStates.HumanReview, lane);
        Assert.Contains(EngineApplierEffectKind.ParkVerdict, owed);
        Assert.DoesNotContain(EngineApplierEffectKind.AgentRoundHandoff, owed);
        foreach (var effect in owed)
            AssertApplied(factory, effect, occurrence, arranged, lane, nameof(TransitionAction.AcceptAndIntegrate), null);
        var journal = RemoteDeliverySettlementStore.Read(CardFolder(arranged.TaskId))!;
        Assert.Equal(nameof(AgentStudio.Git.MergeIntoIntegrationOutcome.Conflict), journal.IntegrationOutcome);
        var check = factory.Services.GetRequiredService<EngineApplierEffectProbe>().Check(occurrence);
        Assert.True(check.Verified, check.Evidence);
        Assert.Equal(EngineEffectBasis.Row, check.Basis);
    }

    /// <summary>
    /// A retry after a restart: the first pass wrote this review's journal and
    /// stopped before the integration; an earlier review of the same delivery
    /// left its round row. The card is still in Auto Review, so the retry
    /// integrates instead of reading the stale row as its handoff.
    /// </summary>
    [Fact]
    public async Task Applier_retry_with_a_stale_round_row_integrates()
    {
        using var factory = ContractFactory(nameof(TransitionAction.AcceptAndIntegrate), "");
        var (occurrence, arranged, _, lane, owed) = await ApplyContractCaseAsync(factory, "ReviewReport",
            nameof(TransitionAction.AcceptAndIntegrate), "", (seeded, decision) =>
            {
                SeedEarlierRound(seeded, decision);
                SeedSettlementJournal(seeded, decision, IntegrationRequest(seeded, decision));
            });

        Assert.Equal("completed", occurrence.Run.Status);
        foreach (var effect in owed)
            AssertApplied(factory, effect, occurrence, arranged, lane, nameof(TransitionAction.AcceptAndIntegrate), null);
        AssertLane(arranged.TaskId, TaskStates.HumanReview);
        var journal = RemoteDeliverySettlementStore.Read(CardFolder(arranged.TaskId))!;
        Assert.NotEqual(nameof(AgentStudio.Git.MergeIntoIntegrationOutcome.Conflict), journal.IntegrationOutcome);
        Assert.Contains(IntegrationLaneEvents.Read(CardFolder(arranged.TaskId)), entry => entry.Kind == "dequeued");
        var check = factory.Services.GetRequiredService<EngineApplierEffectProbe>().Check(occurrence);
        Assert.True(check.Verified, check.Evidence);
        Assert.Equal(EngineEffectBasis.Row, check.Basis);
    }

    /// <summary>
    /// A passed gate's card moves on before the gate handler's next pass, so
    /// that pass never writes the dispatch receipt: a refusal by design, not a
    /// missing effect.
    /// </summary>
    [Theory]
    [InlineData("lane")]
    [InlineData("pending-intent")]
    [InlineData("attempt")]
    public async Task Applier_canary_does_not_count_a_gate_decision_whose_card_moved_on_before_the_dispatch(
        string change)
    {
        using var factory = ContractFactory(nameof(TransitionAction.Continue), "");
        var (occurrence, arranged, _, _, _) = await ApplyContractCaseAsync(factory, "GateResult",
            nameof(TransitionAction.Continue), "");
        var probe = factory.Services.GetRequiredService<EngineApplierEffectProbe>();
        Assert.Equal(EngineEffectBasis.Row, probe.Check(occurrence).Basis);

        if (change == "lane")
            Assert.Equal(MoveJobStatus.Success, (await factory.Services.GetRequiredService<TaskTransitionService>()
                .MoveAsync(arranged.TaskId, TaskStates.Ready, _watchPath, CancellationToken.None,
                    cause: "human:operator@example.invalid", suppressProductExecution: true,
                    expectedSourceState: TaskStates.AutoReview)).Status);
        else if (change == "pending-intent")
            Assert.NotNull(factory.Services.GetRequiredService<TaskMutationService>().SavePendingIntent(
                arranged.TaskId, ContinueModes.Steer, "Fix the gate.", reason: "operator", activeJobId: null,
                watchPath: _watchPath));
        else Supersede(factory.Services.GetRequiredService<AttemptAuthorityService>(), arranged.TaskId);
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();

        var check = probe.Check(occurrence);
        Assert.Equal((false, EngineEffectBasis.GateSuperseded), (check.Verified, check.Basis));
        Assert.Contains(nameof(EngineApplierEffectKind.GateDispatch), check.Missing);
        Assert.Equal(ApplierCanaryVerdict.NotCounted, ApplierCanaryPolicy.Judge(check, _clock.UtcDateTime,
            _clock.UtcDateTime.AddDays(1), TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
    }

    private WebApplicationFactory<Program> ContractFactory(string action, string variant)
        => BuildFactory(
            configuration: action == nameof(TransitionAction.HumanReview) || variant == "batch-hold"
                ? UnguardedDeliveryChain : null,
            testServices: variant == AgentRoundVariant ? ConflictingIntegration : PublishingIntegration);

    /// <summary>
    /// The fixture removes every hosted service; an integrated delivery is
    /// published to the target branch by the push worker, without which the
    /// guarded lane move to Human Review finds the delivery merged locally only.
    /// </summary>
    private static void PublishingIntegration(IServiceCollection services)
        => services.AddHostedService<IntegrationPushWorker>();

    /// <summary>
    /// The per-project integration ends in Conflict and hands the card to the
    /// real agent round service, which queues the steer round and moves the
    /// card to Ready inside <see cref="RemoteDeliveryIntegrationCoordinator.EnqueueAsync"/>.
    /// </summary>
    private static void ConflictingIntegration(IServiceCollection services)
    {
        services.RemoveAll<RemoteDeliveryIntegrationCoordinator>();
        services.AddSingleton(provider => new RemoteDeliveryIntegrationCoordinator(
            _ => Task.FromResult(AgentStudio.Git.MergeIntoIntegrationResult.Conflicted(["docs/applier.md"], "conflict")),
            provider.GetRequiredService<ILogger<RemoteDeliveryIntegrationCoordinator>>(),
            startAgentRound: provider.GetRequiredService<IntegrationAgentRoundService>().TryStartAsync));
    }

    private sealed record AppliedCase(MonolithOrchestrationOccurrence Occurrence, ArrangedDecision Arranged,
        string? Handoff, string? Lane, IReadOnlyList<EngineApplierEffectKind> Owed);

    /// <summary>
    /// Arranges one contract case, lets <paramref name="seed"/> add the state
    /// an earlier pass or review left, and completes the occurrence through the
    /// real applier.
    /// </summary>
    private async Task<AppliedCase> ApplyContractCaseAsync(WebApplicationFactory<Program> factory, string profile,
        string action, string variant, Action<WebApplicationFactory<Program>, ArrangedDecision>? seed = null)
    {
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var arranged = Arrange(factory, profile, action, variant);
        seed?.Invoke(factory, arranged);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var run = orchestration.Create(ProjectName, arranged.TaskId, arranged.Stage, arranged.Payload,
            arranged.IdempotencyKey, arranged.SourceState, arranged.AuthorityAttemptId, arranged.Fence,
            arranged.Epoch, profile: profile);
        if (arranged.Stage != Contract.OrchestrationStage.BatchPlan)
            EngineDecisionWaitMarker.Write(CardFolder(arranged.TaskId), run.RunId);
        // The marker an integration's loop-exhaustion signal leaves: a
        // review-input decision now holds the card (MonolithOrchestrationApplier).
        var handoff = variant == ReviewInputHandoffVariant
            ? orchestration.CreateReviewInputDecision(ProjectName, arranged.TaskId,
                JsonSerializer.Serialize(new { kind = "loop-exhaustion" }), "contract-handoff",
                arranged.SourceState, arranged.AuthorityAttemptId, arranged.Fence, arranged.Epoch).RunId
            : null;
        if (handoff is not null) EngineDecisionWaitMarker.Write(CardFolder(arranged.TaskId), handoff);
        var claim = orchestration.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
            [arranged.Stage]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        if (action == nameof(TransitionAction.DegradeWithFollowUp)) SeedEngineFollowUp(factory, run.RunId);
        await orchestration.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest(
            "engine", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, arranged.Stage, arranged.StageAction,
            arranged.Output, "complete:" + run.RunId), CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

        var row = Assert.IsType<EngineApplierEffect>(EngineApplierContract.Find(action));
        var lane = row.Lane == EngineApplierContract.StageLane ? arranged.PayloadLane : row.Lane;
        using var payload = JsonDocument.Parse(arranged.Payload);
        var owed = EngineApplierContract.Owed(row, lane, arranged.Stage == Contract.OrchestrationStage.GateDispatch,
            payload.RootElement.TryGetProperty("reviewOutcome", out _),
            payload.RootElement.TryGetProperty("reviewAttemptRequest", out var request)
                && request.ValueKind == JsonValueKind.Object,
            handoff is not null, variant == AgentRoundVariant);
        return new AppliedCase(orchestration.GetOccurrence(run.RunId)!, arranged, handoff, lane, owed);
    }

    /// <summary>
    /// The 16.10.2026 incident under the canary: ReviewReport leaves a perfect
    /// shadow streak, and the applier as it was until 17.10.2026 completes each
    /// DeferToBatch decision without its batch admission. The first decision
    /// shows no admission within the canary window, so the profile rolls back
    /// to the monolith and the operator feed gets the evidence.
    /// </summary>
    [Fact]
    public async Task Applier_canary_rolls_review_report_back_when_defer_to_batch_skips_its_admission()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var unapplied = new MonolithOrchestrationStore(Path.Combine(_workspace, "pre-fix-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), new UnappliedDecisions(), rollout);
        var deferred = SettleDeferredReview(factory, "DOC-CANARY", "docs/canary.md");
        var decided = await DecideDeferToBatchAsync(unapplied, deferred);
        Assert.Equal("completed", decided.Status);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, unapplied);
        await unapplied.RecoverApplyingAsync(CancellationToken.None);

        // Within the window a missing admission is only a pending check.
        Assert.Equal(0, canary.CheckOnce());
        var watching = ReviewReport(rollout);
        Assert.Equal(("canary", "watching"), (watching.State, watching.Canary!.Status));
        Assert.Contains(nameof(EngineApplierEffectKind.BatchAdmission), watching.LastEffectCheck!.Missing);

        _clock = _clock.Add(rollout.CanaryWindow);
        Assert.Equal(1, canary.CheckOnce());

        var rolledBack = ReviewReport(rollout);
        Assert.Equal(("rolled-back", "failed"), (rolledBack.State, rolledBack.Canary!.Status));
        Assert.Equal(OrchestrationExecutionMode.Monolith, MonolithTransitionSwitch.For(
            factory.Services.GetRequiredService<IConfiguration>(), "ReviewReport"));
        var receipt = rollout.SwitchReceipts().Last();
        Assert.Equal((OrchestrationRolloutStore.CanaryActor, "rolled-back"), (receipt.Actor, receipt.To));
        Assert.Contains(nameof(EngineApplierEffectKind.BatchAdmission), receipt.Reason);
        var alert = Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath),
            entry => entry.Topic == $"applier-canary:ReviewReport:{decided.RunId}");
        Assert.Equal(OrchestratorLogKinds.Alert, alert.Kind);
        Assert.Contains("DOC-CANARY", alert.Summary);
        Assert.Contains("BatchAdmission", alert.Reasoning);
        // The rollback re-decides only pending occurrences; the alert names the card's redrive.
        Assert.Contains("Manual redrive", alert.Reasoning);
        Assert.Contains($"DOC-CANARY is in {TaskStates.AutoReview}", alert.Reasoning);
        Assert.False(rolledBack.Canary.PassedOnThisInstance);
        // Nothing is watched any more, and no second alert follows.
        Assert.Equal(0, canary.CheckOnce());
        Assert.Null(factory.Services.GetRequiredService<BatchGateStore>().ReadPending(deferred.Member.ReviewAttemptId));
    }

    [Fact]
    public async Task Applier_canary_passes_review_report_after_three_applied_engine_decisions()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        StartReviewReportCanary(factory);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services);
        await orchestration.RecoverApplyingAsync(CancellationToken.None);

        for (var index = 1; index <= 3; index++)
        {
            var deferred = SettleDeferredReview(factory, $"DOC-CANARY{index}", $"docs/canary{index}.md");
            Assert.Equal("completed", (await DecideDeferToBatchAsync(orchestration, deferred)).Status);
            Assert.Equal(0, canary.CheckOnce());
        }

        var passed = ReviewReport(rollout);
        Assert.Equal(("engine", "passed", 3), (passed.State, passed.Canary!.Status, passed.Canary.Verified));
        Assert.True(passed.Canary.PassedOnThisInstance);
        Assert.All(passed.Canary.Observations, item => Assert.Equal("DeferToBatch", item.Action));
        Assert.True(passed.LastEffectCheck!.Verified);
        Assert.Equal(3, factory.Services.GetRequiredService<BatchGateStore>().ListPending().Count);
        Assert.Equal(OrchestrationExecutionMode.Engine, MonolithTransitionSwitch.For(
            factory.Services.GetRequiredService<IConfiguration>(), "ReviewReport"));
    }

    /// <summary>
    /// The review of 19.10.2026: an applier that awaits a whole integration
    /// (queue and delivery gate) leaves its occurrence applying far past the
    /// effect window. That decision waits for the apply deadline, and its
    /// effects are judged once it completed.
    /// </summary>
    [Fact]
    public async Task Applier_canary_waits_for_an_applier_that_has_not_completed()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var held = new HeldApplier(factory.Services.GetRequiredService<IMonolithOrchestrationApplier>());
        var slow = new MonolithOrchestrationStore(Path.Combine(_workspace, "slow-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), held, rollout);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, slow);
        await slow.RecoverApplyingAsync(CancellationToken.None);
        var deciding = DecideDeferToBatchAsync(slow, SettleDeferredReview(factory, "DOC-SLOW", "docs/slow.md"));

        _clock = _clock.Add(rollout.CanaryWindow * 2);
        Assert.Equal(0, canary.CheckOnce());
        var applying = ReviewReport(rollout);
        Assert.Equal(("canary", "watching"), (applying.State, applying.Canary!.Status));
        Assert.Null(applying.LastEffectCheck!.CompletedAt);
        Assert.Equal([nameof(EngineApplierEffectKind.DecisionReceipt)], applying.LastEffectCheck.Missing);

        held.Release.SetResult();
        Assert.Equal("completed", (await deciding.WaitAsync(TimeSpan.FromMinutes(2))).Status);
        Assert.Equal(0, canary.CheckOnce());
        var verified = Assert.Single(ReviewReport(rollout).Canary!.Observations);
        Assert.Equal("verified", verified.Status);
        Assert.NotNull(verified.LastCheck!.CompletedAt);
    }

    /// <summary>
    /// The review of 20.10.2026: the applier writes a batch plan's durable
    /// intent before the occurrence completes. While the occurrence applies,
    /// that intent proves nothing: the check stays unverified and the canary,
    /// which needs only this one decision, is not promoted. Once the
    /// occurrence completed with its engine receipt, the same intent counts.
    /// </summary>
    [Fact]
    public async Task Applier_canary_does_not_count_a_batch_plan_whose_occurrence_has_not_completed()
    {
        using var factory = BuildFactory(configuration: new Dictionary<string, string?>(OneMatchThreshold)
        {
            [OrchestrationRolloutStore.CanaryDecisionsKey] = "1",
        });
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        rollout.Switch("BatchPlan", "shadow", "operator", "compare batch plans");
        var receipt = OrchestrationRolloutStore.Receipt("plan-shadow", "batch-plan", "{}", "wait",
            "Members are still arriving.", "monolith");
        rollout.Compare("BatchPlan", receipt, receipt with { Decider = "engine" });
        var held = new HeldApplier(factory.Services.GetRequiredService<IMonolithOrchestrationApplier>());
        var slow = new MonolithOrchestrationStore(Path.Combine(_workspace, "held-plans"),
            factory.Services.GetRequiredService<TimeProvider>(), held, rollout);
        Assert.Equal("canary", slow.SwitchProfile("BatchPlan", "canary", "operator", "shadow matched").State);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, slow);
        await slow.RecoverApplyingAsync(CancellationToken.None);

        var arranged = ArrangeBatchPlan();
        var run = slow.Create(ProjectName, arranged.TaskId, arranged.Stage, arranged.Payload,
            arranged.IdempotencyKey, arranged.SourceState, arranged.AuthorityAttemptId, arranged.Fence,
            arranged.Epoch, profile: "BatchPlan");
        var claim = slow.Claim(new Contract.OrchestrationClaimRequest("engine", "instance", [arranged.Stage]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        var deciding = slow.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest("engine",
            "instance", claim.Lease!.LeaseId, claim.Lease.Fence, arranged.Stage, arranged.StageAction,
            arranged.Output, "complete:" + run.RunId), CancellationToken.None);
        // The intent the applier writes before the occurrence completes.
        var batchQueue = factory.Services.GetRequiredService<BatchGateStore>();
        batchQueue.WriteEngineIntent(new BatchGateEngineIntent(run.RunId, arranged.TaskId,
            new BatchPlanDecision("wait", "Members are still arriving.", null, [], 4, []), _clock));
        Assert.Equal("applying", slow.GetOccurrence(run.RunId)!.Run.Status);

        var unapplied = factory.Services.GetRequiredService<EngineApplierEffectProbe>()
            .Check(slow.GetOccurrence(run.RunId)!);
        Assert.Equal((false, (DateTime?)null), (unapplied.Verified, unapplied.CompletedAt));
        Assert.Equal([nameof(EngineApplierEffectKind.DecisionReceipt)], unapplied.Missing);
        Assert.Equal(0, canary.CheckOnce());
        var watching = rollout.GetProfiles().Single(item => item.Profile == "BatchPlan");
        Assert.Equal(("canary", "watching", 0), (watching.State, watching.Canary!.Status, watching.Canary.Verified));
        Assert.Equal("pending", Assert.Single(watching.Canary.Observations).Status);
        Assert.False(watching.LastEffectCheck!.Verified);
        Assert.Equal(OrchestrationExecutionMode.Engine, MonolithTransitionSwitch.For(
            factory.Services.GetRequiredService<IConfiguration>(), "BatchPlan"));

        held.Release.SetResult();
        Assert.Equal("completed", (await deciding.WaitAsync(TimeSpan.FromMinutes(2))).Status);
        Assert.Equal(0, canary.CheckOnce());
        var passed = rollout.GetProfiles().Single(item => item.Profile == "BatchPlan");
        Assert.Equal(("engine", "passed", 1), (passed.State, passed.Canary!.Status, passed.Canary.Verified));
        Assert.NotNull(passed.LastEffectCheck!.CompletedAt);
    }

    /// <summary>
    /// The card leaves Auto Review by a move this decision did not write (a
    /// person, or any other writer) while the engine decides; the applier
    /// refuses the decision with a source-state mismatch on purpose. That is no
    /// applier defect: the decision is not counted, and the next engine
    /// decision takes its place.
    /// </summary>
    [Theory]
    [InlineData("human:operator@example.invalid", TaskStates.Escalated, LaneChangeCauses.Escalated)]
    [InlineData("runner:coding-host", TaskStates.Ready, LaneChangeCauses.IntegrationRecovery)]
    public async Task Applier_canary_does_not_count_a_decision_whose_card_another_writer_moved(string actor,
        string lane, string cause)
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        StartReviewReportCanary(factory);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services);
        var moved = SettleDeferredReview(factory, "DOC-MOVED", "docs/moved.md");
        var (run, claim) = PrepareReviewDecision(orchestration, moved);
        var manual = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            moved.Member.Key, lane, _watchPath, CancellationToken.None, cause: actor,
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview, transitionCause: cause);
        Assert.Equal(MoveJobStatus.Success, manual.Status);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestration.CompleteAsync(
            run.RunId, ReviewCompletion(claim, moved, TransitionAction.HumanReview), CancellationToken.None));
        Assert.Equal("orchestration-source-state-mismatch", refused.Message);
        // The recovery retry refuses it again.
        await Assert.ThrowsAsync<AggregateException>(() => orchestration.RecoverApplyingAsync(CancellationToken.None));
        _clock = _clock.Add(rollout.ApplyDeadline);
        Assert.Equal(0, canary.CheckOnce());

        var watching = ReviewReport(rollout);
        Assert.Equal(("canary", "watching"), (watching.State, watching.Canary!.Status));
        var skipped = Assert.Single(watching.Canary.Observations);
        Assert.Equal(("not-counted", EngineEffectBasis.SourceExit), (skipped.Status, skipped.LastCheck!.Basis));
        Assert.Contains(actor, skipped.LastCheck.Evidence);
        Assert.Contains(cause, skipped.LastCheck.Evidence);
        Assert.Equal("completed", (await DecideDeferToBatchAsync(orchestration,
            SettleDeferredReview(factory, "DOC-NEXT", "docs/next.md"))).Status);
        Assert.Equal(0, canary.CheckOnce());
        Assert.Equal(["not-counted", "verified"], ReviewReport(rollout).Canary!.Observations.Select(item => item.Status));
    }

    /// <summary>
    /// One decision the probe cannot read leaves the others checked in the same
    /// pass. A probe defect is no applier defect: the decision does not count,
    /// the operator feed gets an alert, and the profile is not rolled back.
    /// </summary>
    [Fact]
    public async Task Applier_canary_isolates_a_failing_probe_alerts_and_does_not_count_it()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "probe-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), new UnappliedDecisions(), rollout);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, journal);
        await journal.RecoverApplyingAsync(CancellationToken.None);
        var unreadable = SettleDeferredReview(factory, "DOC-UNREADABLE", "docs/unreadable.md");
        var (first, firstClaim) = PrepareReviewDecision(journal, unreadable);
        await journal.CompleteAsync(first.RunId, ReviewCompletion(firstClaim, unreadable, TransitionAction.Halt,
            activeChoice: new { action = 5 }), CancellationToken.None);
        // A Halt applies nothing, so it shows its effects without an applier.
        var halted = SettleDeferredReview(factory, "DOC-HALTED", "docs/halted.md");
        var (second, secondClaim) = PrepareReviewDecision(journal, halted, waitMarker: false);
        await journal.CompleteAsync(second.RunId, ReviewCompletion(secondClaim, halted, TransitionAction.Halt),
            CancellationToken.None);

        Assert.Equal(0, canary.CheckOnce());
        var observations = ReviewReport(rollout).Canary!.Observations;
        Assert.Equal(["not-counted", "verified"], observations.Select(item => item.Status));
        Assert.Equal((EngineEffectBasis.ProbeFailed, "probe"),
            (observations[0].LastCheck!.Basis, Assert.Single(observations[0].LastCheck!.Missing)));
        Assert.Contains("could not read", observations[0].LastCheck!.Evidence);
        var alert = Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath),
            entry => entry.Topic == $"applier-canary-probe:ReviewReport:{first.RunId}");
        Assert.Equal(OrchestratorLogKinds.Alert, alert.Kind);
        Assert.Contains("DOC-UNREADABLE", alert.Summary);

        _clock = _clock.Add(rollout.ApplyDeadline);
        Assert.Equal(0, canary.CheckOnce());
        Assert.Equal(("canary", "watching"), (ReviewReport(rollout).State, ReviewReport(rollout).Canary!.Status));
    }

    /// <summary>
    /// The review of 20.10.2026: an accepted review awaits the serial
    /// per-project integration, and every delivery ahead runs its gate. While
    /// the integration lane shows the delivery enqueued or dequeued without a
    /// terminal event, the decision waits; still in flight at the apply
    /// deadline, it does not count instead of rolling the profile back.
    /// </summary>
    [Fact]
    public async Task Applier_canary_waits_for_an_integration_in_flight_and_does_not_count_it_at_the_deadline()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var held = new HeldApplier(factory.Services.GetRequiredService<IMonolithOrchestrationApplier>());
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "queued-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), held, rollout);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, journal);
        await journal.RecoverApplyingAsync(CancellationToken.None);
        var accepted = SettleDeferredReview(factory, "DOC-QUEUED", "docs/queued.md");
        var (run, claim) = PrepareReviewDecision(journal, accepted);
        using var stop = new CancellationTokenSource();
        var deciding = journal.CompleteAsync(run.RunId,
            ReviewCompletion(claim, accepted, TransitionAction.AcceptAndIntegrate), stop.Token);
        // The coordinator's lane rows of the delivery: queued, then running its gate.
        var folder = CardFolder(accepted.Member.Key);
        IntegrationLaneEvents.Append(folder, ProjectName, accepted.Member.Key, "enqueued", sequence: 7,
            atUtc: _clock);
        IntegrationLaneEvents.Append(folder, ProjectName, accepted.Member.Key, "dequeued", sequence: 7,
            atUtc: _clock);

        Assert.Equal(0, canary.CheckOnce());
        var waiting = Assert.Single(ReviewReport(rollout).Canary!.Observations);
        Assert.Equal(("pending", EngineEffectWait.IntegrationInFlight), (waiting.Status, waiting.LastCheck!.WaitingOn));

        _clock = _clock.Add(rollout.ApplyDeadline);
        Assert.Equal(0, canary.CheckOnce());
        var profile = ReviewReport(rollout);
        Assert.Equal(("canary", "watching"), (profile.State, profile.Canary!.Status));
        Assert.Equal("not-counted", Assert.Single(profile.Canary.Observations).Status);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deciding);
    }

    /// <summary>
    /// The review of 20.10.2026: an integration that ends near the apply
    /// deadline (here with the batch gate's publication) leaves the applier one
    /// effect window to complete, counted from the terminal lane row; only
    /// then is the unapplied decision missing.
    /// </summary>
    [Fact]
    public async Task Applier_canary_gives_an_integration_that_ended_near_the_deadline_one_effect_window()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var held = new HeldApplier(factory.Services.GetRequiredService<IMonolithOrchestrationApplier>());
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "ended-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), held, rollout);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, journal);
        await journal.RecoverApplyingAsync(CancellationToken.None);
        var accepted = SettleDeferredReview(factory, "DOC-ENDED", "docs/ended.md");
        var (run, claim) = PrepareReviewDecision(journal, accepted);
        using var stop = new CancellationTokenSource();
        var deciding = journal.CompleteAsync(run.RunId,
            ReviewCompletion(claim, accepted, TransitionAction.AcceptAndIntegrate), stop.Token);
        var folder = CardFolder(accepted.Member.Key);
        IntegrationLaneEvents.Append(folder, ProjectName, accepted.Member.Key, "enqueued", sequence: 9,
            atUtc: _clock);
        IntegrationLaneEvents.Append(folder, ProjectName, accepted.Member.Key, "dequeued", sequence: 9,
            atUtc: _clock);
        Assert.Equal(0, canary.CheckOnce());

        _clock = _clock.Add(rollout.ApplyDeadline - TimeSpan.FromMinutes(1));
        var endedAt = _clock;
        IntegrationLaneEvents.Append(folder, ProjectName, accepted.Member.Key, "published", sequence: 9,
            atUtc: endedAt);
        _clock = _clock.AddMinutes(1);
        Assert.Equal(0, canary.CheckOnce());
        var waiting = Assert.Single(ReviewReport(rollout).Canary!.Observations);
        Assert.Equal(("pending", (string?)null), (waiting.Status, waiting.LastCheck!.WaitingOn));
        Assert.Equal(endedAt.UtcDateTime, waiting.LastCheck.IntegrationEndedAt);

        _clock = endedAt.Add(rollout.CanaryWindow);
        Assert.Equal(1, canary.CheckOnce());
        Assert.Equal("rolled-back", ReviewReport(rollout).State);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deciding);
    }

    /// <summary>
    /// Restart safety: after a start the canary judges nothing before the
    /// recovery service ran one pass over the applying occurrences, and the
    /// apply deadline counts from the start, so downtime is left out.
    /// </summary>
    [Fact]
    public async Task Applier_canary_leaves_downtime_out_and_waits_for_a_recovery_pass_after_a_start()
    {
        using var factory = BuildFactory(configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        StartReviewReportCanary(factory);
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "restarted-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), new FailingApplier(), rollout);
        var stuck = SettleDeferredReview(factory, "DOC-RESTART", "docs/restart.md");
        var (run, claim) = PrepareReviewDecision(journal, stuck);
        await Assert.ThrowsAsync<IOException>(() => journal.CompleteAsync(run.RunId,
            ReviewCompletion(claim, stuck, TransitionAction.HumanReview), CancellationToken.None));

        // The host was down for longer than the apply deadline.
        _clock = _clock.Add(rollout.ApplyDeadline + TimeSpan.FromHours(1));
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services, journal);
        Assert.Equal(0, canary.CheckOnce());
        Assert.Null(Assert.Single(ReviewReport(rollout).Canary!.Observations).LastCheck);
        // The status shows the wait, so a recovery loop that never finishes a pass is visible.
        Assert.True(ReviewReport(rollout).Canary!.AwaitingRecoveryPass);

        await Assert.ThrowsAsync<AggregateException>(() => journal.RecoverApplyingAsync(CancellationToken.None));
        Assert.Equal(0, canary.CheckOnce());
        Assert.Equal("pending", Assert.Single(ReviewReport(rollout).Canary!.Observations).Status);
        Assert.False(ReviewReport(rollout).Canary!.AwaitingRecoveryPass);

        _clock = _clock.Add(rollout.ApplyDeadline);
        Assert.Equal(1, canary.CheckOnce());
        Assert.Equal("rolled-back", ReviewReport(rollout).State);
    }

    /// <summary>A card missing from the scanner snapshot waits; at the apply deadline it does not count.</summary>
    [Fact]
    public void Applier_probe_waits_for_a_card_missing_from_the_snapshot()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var run = orchestration.Create(ProjectName, "DOC-GONE", Contract.OrchestrationStage.ReviewDecision,
            JsonSerializer.Serialize(new { reviewOutcome = "Pass", reviewAttemptId = "review_gone" }),
            "review-report:review_gone:report", TaskStates.AutoReview, "review_gone", 1, 1, profile: "ReviewReport");

        var check = factory.Services.GetRequiredService<EngineApplierEffectProbe>()
            .Check(orchestration.GetOccurrence(run.RunId)!);

        Assert.Equal((false, EngineEffectWait.CardNotFound), (check.Verified, check.WaitingOn));
        var observedAt = _clock.UtcDateTime;
        Assert.Equal(ApplierCanaryVerdict.Waiting, ApplierCanaryPolicy.Judge(check, observedAt,
            observedAt.AddHours(2), TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
        Assert.Equal(ApplierCanaryVerdict.NotCounted, ApplierCanaryPolicy.Judge(check, observedAt,
            observedAt.AddHours(3), TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
    }

    /// <summary>
    /// A DeferToBatch card in Human Review counts as admitted only through the
    /// per-task gate's keyed lane write of that review, not through any move.
    /// </summary>
    [Fact]
    public async Task Applier_probe_requires_the_per_task_gate_write_for_a_settled_defer_to_batch()
    {
        // The moves stand in for the per-task gate and a person; the guarded
        // delivery chain would refuse the card's unintegrated result.
        using var factory = BuildFactory(configuration: UnguardedDeliveryChain);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var journal = new MonolithOrchestrationStore(Path.Combine(_workspace, "fallback-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), new UnappliedDecisions());
        var deferred = SettleDeferredReview(factory, "DOC-FALLBACK", "docs/fallback.md");
        var decided = await DecideDeferToBatchAsync(journal, deferred);
        EngineDecisionWaitMarker.Clear(CardFolder(deferred.Member.Key), decided.RunId);
        var transitions = factory.Services.GetRequiredService<TaskTransitionService>();
        var probe = factory.Services.GetRequiredService<EngineApplierEffectProbe>();
        async Task MoveAsync(string from, string to, string cause)
        {
            Assert.Equal(MoveJobStatus.Success, (await transitions.MoveAsync(deferred.Member.Key, to, _watchPath,
                CancellationToken.None, cause: cause, suppressProductExecution: true,
                expectedSourceState: from)).Status);
            factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        }

        await MoveAsync(TaskStates.AutoReview, TaskStates.HumanReview, "human:operator@example.invalid");
        Assert.Equal([nameof(EngineApplierEffectKind.BatchAdmission)],
            probe.Check(journal.GetOccurrence(decided.RunId)!).Missing);

        await MoveAsync(TaskStates.HumanReview, TaskStates.AutoReview, "human:operator@example.invalid");
        await MoveAsync(TaskStates.AutoReview, TaskStates.HumanReview, "batch-fallback:" + deferred.Review.AttemptId);
        var check = probe.Check(journal.GetOccurrence(decided.RunId)!);
        Assert.True(check.Verified, check.Evidence);
    }

    /// <summary>The refusal the applier before the fix threw on a seeded state, once; then the real applier.</summary>
    private sealed class RefusedOnce(IMonolithOrchestrationApplier inner) : IMonolithOrchestrationApplier
    {
        private int _calls;

        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
            => Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("orchestration-source-state-mismatch")
                : inner.ApplyAsync(occurrence, completion, ct);
    }

    /// <summary>The integration request the applier builds for the arranged review.</summary>
    private RemoteDeliveryIntegrationRequest IntegrationRequest(WebApplicationFactory<Program> factory,
        ArrangedDecision arranged)
    {
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var task = scanner.FindJob(arranged.TaskId, _watchPath)!;
        var project = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .Get(task.ProjectName);
        var review = factory.Services.GetRequiredService<AttemptAuthorityService>()
            .GetReview(arranged.AuthorityAttemptId)!;
        return new RemoteDeliveryIntegrationRequest(task.ProjectName, task.Id, task.FolderPath, task.WatchPath,
            TaskIntegrationBranch.Resolve(task, project.IntegrationBranch), project.IntegrationStrategy,
            PipelineTypes.Resolve(task), _clock, review.AttemptId, review.Subject.ExpectedResultSha);
    }

    /// <summary>The settlement journal the applier's first pass writes for this review.</summary>
    private void SeedSettlementJournal(WebApplicationFactory<Program> factory, ArrangedDecision arranged,
        RemoteDeliveryIntegrationRequest request)
        => RemoteDeliverySettlementStore.Write(CardFolder(arranged.TaskId), new RemoteDeliverySettlementRecord
        {
            TaskKey = arranged.TaskId,
            ReviewAttemptId = arranged.AuthorityAttemptId,
            JournalRequired = true,
            Outcome = "Pass",
            ShouldIntegrate = true,
            BuildTestGate = "Passed",
            GateReason = "Engine decided AcceptAndIntegrate.",
            IntegrationBranch = request.IntegrationBranch,
            IntegrationStrategy = request.IntegrationStrategy,
            PipelineType = request.PipelineType,
            DeliveredAtUtc = request.DeliveredAtUtc,
            Stage = RemoteDeliverySettlementStage.IntegrationPending,
            RecordedAtUtc = _clock,
        });

    /// <summary>
    /// An earlier review of the same delivery: its integration was enqueued,
    /// conflicted, and the round service spent the delivery's one automatic
    /// round. Both rows are a minute older than anything this review writes.
    /// </summary>
    private void SeedEarlierRound(WebApplicationFactory<Program> factory, ArrangedDecision arranged)
    {
        var folder = CardFolder(arranged.TaskId);
        var subject = ReviewSubjectStore.Read(folder)!;
        // Older than the real integration's lane rows, which carry the wall clock.
        var earlier = _clock.AddMinutes(-1);
        IntegrationLaneEvents.Append(folder, ProjectName, arranged.TaskId, "enqueued", sequence: 1,
            atUtc: earlier.AddSeconds(-1));
        Assert.True(factory.Services.GetRequiredService<TimelineLog>().Append(folder, new TimelineEvent
        {
            Ts = earlier.UtcDateTime,
            Kind = TimelineEventKinds.IntegrationRecoveryQueued,
            Actor = TimelineActors.System,
            Summary = "Automatically started a new agent round to preserve unambiguous delivery SHA attribution.",
            Details = new Dictionary<string, string>
            {
                ["automatic"] = "true",
                [IntegrationRecoveryBudget.DeliveryChainIdKey] = IntegrationRecoveryBudget.DeliveryChainId(subject),
                ["resultSha"] = subject.ResultSha,
                ["reason"] = IntegrationAgentRoundService.AttributionAmbiguousReason,
                ["mode"] = ContinueModes.Steer,
                ["budgetUsed"] = "1",
                ["budgetLimit"] = "1",
            },
        }));
    }

    /// <summary>An applier that cannot apply, as one that is down or broken.</summary>
    private sealed class FailingApplier : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
            => throw new IOException("The applier is unavailable.");
    }

    private (Contract.OrchestrationRunDto Run, Contract.OrchestrationClaimResponse Claim) PrepareReviewDecision(
        MonolithOrchestrationStore orchestration, DeferredReview deferred, bool waitMarker = true)
    {
        var review = deferred.Review;
        var report = review.Reports.Last(item => item.AuthorityStatus == AttemptWriteStatus.Accepted);
        var run = orchestration.Create(ProjectName, deferred.Member.Key, Contract.OrchestrationStage.ReviewDecision,
            JsonSerializer.Serialize(new { reviewOutcome = "Pass", reviewAttemptId = review.AttemptId }),
            $"review-report:{review.AttemptId}:{report.IdempotencyKey}", TaskStates.AutoReview,
            review.AttemptId, review.LastFence, review.AuthorityEpoch, profile: "ReviewReport");
        if (waitMarker) EngineDecisionWaitMarker.Write(CardFolder(deferred.Member.Key), run.RunId);
        var claim = orchestration.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
            [Contract.OrchestrationStage.ReviewDecision]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        return (run, claim);
    }

    private static Contract.CompleteOrchestrationStageRequest ReviewCompletion(
        Contract.OrchestrationClaimResponse claim, DeferredReview deferred, TransitionAction action,
        object? activeChoice = null)
    {
        var transition = TransitionDecisionPolicy.Create(action, $"Engine decided {action}.", deferred.Member.Key,
            deferred.Review.AttemptId, 0, 4);
        var output = activeChoice is null
            ? JsonSerializer.Serialize(new { transition })
            : JsonSerializer.Serialize(new { transition, activeChoice });
        return new Contract.CompleteOrchestrationStageRequest("engine", "instance", claim.Lease!.LeaseId,
            claim.Lease.Fence, Contract.OrchestrationStage.ReviewDecision, StageDecisionPolicy.ToStageAction(action),
            output, "complete:" + claim.Run!.RunId);
    }

    /// <summary>The real applier, held until the test releases it, as a long integration holds it.</summary>
    private sealed class HeldApplier(IMonolithOrchestrationApplier inner) : IMonolithOrchestrationApplier
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            await Release.Task.WaitAsync(ct);
            await inner.ApplyAsync(occurrence, completion, ct);
        }
    }

    private static readonly Dictionary<string, string?> UnguardedDeliveryChain = new()
    {
        ["DeliveryChain:Guarded"] = "false",
    };

    private static readonly Dictionary<string, string?> OneMatchThreshold = new()
    {
        ["Orchestration:ShadowMatchThreshold"] = "1",
    };

    /// <summary>Shadow, one matching comparison, Ready, then the canary switch.</summary>
    private static void StartReviewReportCanary(WebApplicationFactory<Program> factory)
    {
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        rollout.Switch("ReviewReport", "shadow", "operator", "compare review decisions");
        var receipt = OrchestrationRolloutStore.Receipt("review-shadow", "review-report", "{}",
            nameof(TransitionAction.DeferToBatch), "The batch gate owns verification.", "monolith");
        rollout.Compare("ReviewReport", receipt, receipt with { Decider = "engine" });
        Assert.Equal("ready", ReviewReport(rollout).State);
        Assert.Equal("canary", factory.Services.GetRequiredService<MonolithOrchestrationStore>()
            .SwitchProfile("ReviewReport", "canary", "operator", "shadow matched").State);
    }

    private static OrchestrationProfileStatus ReviewReport(OrchestrationRolloutStore rollout)
        => rollout.GetProfiles().Single(item => item.Profile == "ReviewReport");

    private sealed record ArrangedDecision(string TaskId, Contract.OrchestrationStage Stage, string Payload,
        string IdempotencyKey, string SourceState, string AuthorityAttemptId, long Fence, long Epoch,
        Contract.OrchestrationAction StageAction, string Output, string? PayloadLane,
        string? ReviewAttemptId = null);

    private ArrangedDecision Arrange(WebApplicationFactory<Program> factory, string profile, string action,
        string variant) => profile switch
    {
        "CodingCompletion" => ArrangeCoding(factory, Enum.Parse<TransitionAction>(action)),
        "ReviewReport" or "OperatorSweep" => ArrangeReview(factory, profile, Enum.Parse<TransitionAction>(action),
            variant),
        "GateResult" => ArrangeGate(factory, Enum.Parse<TransitionAction>(action), variant == "batch-hold"),
        "BatchPlan" => ArrangeBatchPlan(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>
    /// A settled coding run whose card still waits in Progress, as the
    /// completion leaves it. A requeue to Ready follows a failed run without a
    /// result envelope; a completed delivery would be recovered to Auto Review.
    /// </summary>
    private ArrangedDecision ArrangeCoding(WebApplicationFactory<Program> factory, TransitionAction action)
    {
        var target = action switch
        {
            TransitionAction.CreateReview or TransitionAction.Continue => TaskStates.AutoReview,
            TransitionAction.ReturnToReady or TransitionAction.SalvageContinuation => TaskStates.Ready,
            TransitionAction.Escalate => TaskStates.Escalated,
            TransitionAction.HumanReview => TaskStates.HumanReview,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        var delivered = target != TaskStates.Ready;
        var baseSha = Git(_repo, "rev-parse", "origin/develop");
        var resultRef = "refs/heads/agent-studio/results/" + ContractKey;
        Git(_repo, "checkout", "-q", "--detach", baseSha);
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "applier.md"), ContractKey + "\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", ContractKey);
        var resultSha = Git(_repo, "rev-parse", "HEAD");
        PublishResult(ContractKey, resultSha);
        Git(_repo, "checkout", "-q", "--detach", baseSha);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var acquired = authority.AcquireRun(ContractKey, RepositoryId, null, "coding-runner", "coding-host",
            600, "acquire-" + ContractKey).RunAttempt!;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(acquired.AttemptId, acquired.LastFence, acquired.AuthorityEpoch,
                "settle-" + ContractKey),
            Outcome = delivered ? "done" : "environment-failure",
            ResultSha = delivered ? resultSha : null,
            ResultEnvelope = delivered
                ? new Contract.ImmutableResultEnvelope(RepositoryId, acquired.AttemptId,
                    baseSha, resultSha, resultRef, null, new string('a', 64))
                : null,
        });
        Assert.True(settled.Accepted, settled.Message);
        var run = authority.GetRun(acquired.AttemptId)!;
        var folder = Path.Combine(_watchPath, TaskStates.Progress, ContractKey);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = ContractKey, key = ContractKey, title = ContractKey, state = TaskStates.Progress, order = 1,
            agent = "claude", kind = TaskKinds.Task, enteredLaneAt = _clock.UtcDateTime,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Document the applier.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();

        var reviewRequest = action == TransitionAction.CreateReview
            ? new CreateReviewAttemptRequest(ContractKey, RepositoryId, resultSha, run.AttemptId, "requirements",
                "policy", [], "create-" + ContractKey, ResultRef: resultRef,
                Plan: new Contract.ReviewPlanDto(
                [
                    new("aspect-requirement-fit", "requirement-fit", "claude", [],
                        ExecutionKind: Contract.ReviewCommandKinds.AgentAspect, Prompt: "Review the delivery."),
                ], ["requirement-fit"]))
            : null;
        var payload = JsonSerializer.Serialize(new
        {
            agentOutcome = "done",
            runAttemptId = run.AttemptId,
            targetState = target,
            resultSha,
            resultRef,
            reviewAttemptRequest = reviewRequest,
            reviewSubject = reviewRequest is null ? null : new ReviewSubjectRecord
            {
                TaskKey = ContractKey, RunAttemptId = run.AttemptId, AttemptChainId = "chain-" + ContractKey,
                Project = ProjectName, Repository = RepositoryId, ResultSha = resultSha,
                ImmutableResultRef = resultRef, ResultRef = resultRef, CompletedAtUtc = _clock,
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new ArrangedDecision(ContractKey, Contract.OrchestrationStage.ReviewDecision, payload,
            $"coding-completion:{run.AttemptId}:contract", TaskStates.Progress, run.AttemptId, run.LastFence,
            run.AuthorityEpoch, StageDecisionPolicy.ToStageAction(action),
            Output(action, run.AttemptId), target);
    }

    /// <summary>
    /// A settled review of a card in Auto Review, as the review report leaves
    /// it. The agent round variants carry the remote result ref, which the
    /// round service needs to queue its steer round.
    /// </summary>
    private ArrangedDecision ArrangeReview(WebApplicationFactory<Program> factory, string profile,
        TransitionAction action, string variant = "")
    {
        var infrastructure = action == TransitionAction.ReviewInfrastructureRetry;
        var member = SeedMember(factory, ContractKey, "docs/applier.md", settleReview: false,
            deferredPlan: action == TransitionAction.DeferToBatch);
        if (variant is AgentRoundVariant or BudgetSpentVariant)
        {
            var folder = CardFolder(ContractKey);
            ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
            {
                ResultRef = "refs/heads/agent-studio/results/" + ContractKey,
            });
        }
        var review = Settle(factory, member,
            infrastructure ? ReviewTerminalOutcome.InfrastructureFailure : ReviewTerminalOutcome.Pass);
        var report = review.Reports.LastOrDefault(item => item.AuthorityStatus == AttemptWriteStatus.Accepted);
        var payload = JsonSerializer.Serialize(new
        {
            reviewOutcome = infrastructure ? Contract.ReviewOutcomeKinds.ReviewInfra : "Pass",
            reviewAttemptId = review.AttemptId,
        });
        return new ArrangedDecision(ContractKey, Contract.OrchestrationStage.ReviewDecision, payload,
            $"{(profile == "ReviewReport" ? "review-report" : "operator-sweep")}:{review.AttemptId}:"
                + (report?.IdempotencyKey ?? "contract"),
            TaskStates.AutoReview, review.AttemptId, review.LastFence, review.AuthorityEpoch,
            StageDecisionPolicy.ToStageAction(action), Output(action, review.AttemptId), null, review.AttemptId);
    }

    /// <summary>A card in Auto Review whose build and test gate reported a result.</summary>
    private ArrangedDecision ArrangeGate(WebApplicationFactory<Program> factory, TransitionAction action,
        bool batchHold)
    {
        var member = SeedMember(factory, ContractKey, "docs/applier.md", settleReview: false, deferredPlan: false);
        var source = factory.Services.GetRequiredService<AttemptAuthorityService>().GetRun(member.RunAttemptId)!;
        var passed = action == TransitionAction.Continue;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["monolithGateResult"] = true,
            ["gateAttemptId"] = source.AttemptId,
            ["automaticRoundsUsed"] = 0,
            ["maximumCardRounds"] = 4,
            ["gateReason"] = GateReasonText,
            ["gates"] = new[] { new { status = passed ? "passed" : "failed" } },
            ["targetState"] = batchHold ? TaskStates.HumanReview : null,
            ["reviewOutcome"] = batchHold ? "Pass" : null,
            ["reviewAttemptId"] = batchHold ? member.ReviewAttemptId : null,
        }.Where(pair => pair.Value is not null).ToDictionary());
        return new ArrangedDecision(ContractKey, Contract.OrchestrationStage.GateDispatch, payload,
            $"gate-result:{ContractKey}:{source.AttemptId}:{member.ResultSha}:{(passed ? "Ok" : "Fail")}",
            TaskStates.AutoReview, source.AttemptId, source.LastFence, source.AuthorityEpoch,
            StageDecisionPolicy.ToStageAction(action), Output(action, source.AttemptId),
            batchHold ? TaskStates.HumanReview : null, member.ReviewAttemptId);
    }

    private static ArrangedDecision ArrangeBatchPlan()
    {
        var taskId = BatchGatePilotService.BatchPlanTaskId("contract-scope");
        var plan = new BatchPlanDecision("wait", "Members are still arriving.", null, [], 4, []);
        return new ArrangedDecision(taskId, Contract.OrchestrationStage.BatchPlan, "{}", taskId + ":contract",
            "batch-plan", taskId, 0, 0, Contract.OrchestrationAction.Continue,
            JsonSerializer.Serialize(new { batchPlan = plan, decision = "wait" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), null);
    }

    /// <summary>
    /// The follow-up card an interrupted DegradeWithFollowUp left, as a replay
    /// finds it. Card creation writes the flat task layout, under which this
    /// fixture's lane-folder cards are no longer scanned, so the case runs the
    /// applier's replay branch: it links both cards and continues.
    /// </summary>
    private void SeedEngineFollowUp(WebApplicationFactory<Program> factory, string runId)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Ready, ContractKey + "-FOLLOW-UP");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = ContractKey + "-FOLLOW-UP", key = ContractKey + "-FOLLOW-UP",
            title = "Follow-up: review findings in " + ContractKey, state = TaskStates.Ready, order = 1,
            agent = "claude", kind = TaskKinds.Task, enteredLaneAt = _clock.UtcDateTime,
            creationSource = "engine-follow-up:" + runId,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fix the remaining review findings.\n");
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    private static string Output(TransitionAction action, string attemptId)
    {
        Contract.ReviewFollowUpFinding[] findings = action is TransitionAction.FindingRound
            or TransitionAction.ConcernRound or TransitionAction.DegradeWithFollowUp
            ? [new("requirement-fit", "block", "Document the applier limits.")]
            : [];
        return JsonSerializer.Serialize(new
        {
            transition = TransitionDecisionPolicy.Create(action, $"Engine decided {action}.", ContractKey,
                attemptId, 0, 4, findings),
        });
    }

    private void AssertApplied(WebApplicationFactory<Program> factory, EngineApplierEffectKind effect,
        MonolithOrchestrationOccurrence occurrence, ArrangedDecision arranged, string? lane, string action,
        string? handoff)
    {
        var runId = occurrence.Run.RunId;
        var writeKey = $"engine-decision:{runId}:complete:{runId}";
        string Folder() => CardFolder(arranged.TaskId);
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        switch (effect)
        {
            case EngineApplierEffectKind.LaneMove:
                AssertLane(arranged.TaskId, lane!);
                break;
            case EngineApplierEffectKind.LaneKept:
                AssertLane(arranged.TaskId, arranged.SourceState);
                break;
            case EngineApplierEffectKind.BatchAdmission:
                Assert.NotNull(factory.Services.GetRequiredService<BatchGateStore>()
                    .ReadPending(arranged.ReviewAttemptId!));
                Assert.Equal(arranged.ReviewAttemptId, Ownership(arranged.TaskId)?.ReviewAttemptId);
                break;
            case EngineApplierEffectKind.ReviewRetryScheduled:
                Assert.True(authority.HasScheduledReviewInfrastructureRetry(arranged.AuthorityAttemptId));
                break;
            case EngineApplierEffectKind.LinkedFollowUp:
                var scanner = factory.Services.GetRequiredService<TaskScannerService>();
                scanner.InvalidateCache();
                var followUp = Assert.Single(scanner.ScanAllJobs(),
                    task => task.CreationSource == "engine-follow-up:" + runId);
                Assert.Equal(TaskStates.Ready, followUp.State);
                Assert.Contains(arranged.TaskId, followUp.References.FollowUpOf);
                Assert.Contains(followUp.Key ?? followUp.Id,
                    scanner.FindJob(arranged.TaskId, _watchPath)!.References.RaisedFollowUps);
                break;
            case EngineApplierEffectKind.IntegrationJournal:
                var journal = RemoteDeliverySettlementStore.Read(Folder());
                Assert.Equal(arranged.AuthorityAttemptId, journal?.ReviewAttemptId);
                Assert.True(journal!.Stage >= RemoteDeliverySettlementStage.IntegrationSettled);
                break;
            case EngineApplierEffectKind.ContinuationPrompt:
                var prompt = File.ReadAllText(Path.Combine(Folder(), "prompt.md"));
                Assert.Contains($"<!-- engine-decision:{runId} -->", prompt);
                Assert.Contains(action == nameof(TransitionAction.ReturnToReady)
                    ? "Fix the build and test gate failure" : "Document the applier limits.", prompt);
                break;
            case EngineApplierEffectKind.QualityLoopReceipt:
                Assert.Contains(timeline.ReadAll(Folder()), entry =>
                    entry.Kind == TimelineEventKinds.QualityLoopReopened
                    && entry.Details?.GetValueOrDefault("authorityWriteKey") == writeKey);
                break;
            case EngineApplierEffectKind.ReviewAttempt:
                Assert.NotNull(ReviewSubjectStore.Read(Folder()));
                Assert.Equal(arranged.AuthorityAttemptId,
                    authority.GetTaskProjection(arranged.TaskId).CurrentReviewAttempt?.SourceRunAttemptId);
                break;
            case EngineApplierEffectKind.ParkVerdict:
                Assert.Contains(ReviewDecisionLog.ReadAll(_workspace, ProjectName), record =>
                    record.JobId == arranged.TaskId
                    && record.FailureKind == $"remote-review-park:{arranged.ReviewAttemptId}");
                break;
            case EngineApplierEffectKind.GateDispatch:
                AssertLane(arranged.TaskId, arranged.SourceState);
                Assert.Equal(Contract.OrchestrationAction.Continue,
                    Assert.Single(occurrence.Run.StageResults!).Action);
                Assert.Contains(timeline.ReadAll(Folder()), entry =>
                    entry.Kind == TimelineEventKinds.EngineGateDispatched
                    && entry.Details?.GetValueOrDefault(EngineGateDispatchReceipt.RunIdKey) == runId);
                break;
            case EngineApplierEffectKind.BatchPlanIntent:
                // The intent is a file, so a restarted host still finds it.
                var restarted = new BatchGateStore(Path.Combine(_workspace, "batch-gates"));
                Assert.Equal(runId, restarted.ReadEngineIntent(arranged.TaskId)?.RunId);
                break;
            case EngineApplierEffectKind.ReviewInputHandoff:
                Assert.Equal(handoff, EngineDecisionWaitMarker.Read(Folder()));
                break;
            case EngineApplierEffectKind.AgentRoundHandoff:
                AssertLane(arranged.TaskId, TaskStates.Ready);
                var resultSha = authority.GetReview(arranged.AuthorityAttemptId)!.Subject.ExpectedResultSha;
                Assert.Contains(timeline.ReadAll(Folder()), entry =>
                    entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                    && entry.Details?.GetValueOrDefault("resultSha") == resultSha);
                Assert.Equal(RemoteDeliverySettlementStage.LaneSettled,
                    RemoteDeliverySettlementStore.Read(Folder())!.Stage);
                break;
            case EngineApplierEffectKind.WaitMarkerCleared:
                Assert.NotEqual(runId, EngineDecisionWaitMarker.Read(Folder()));
                break;
            case EngineApplierEffectKind.DecisionReceipt:
                Assert.Equal("completed", occurrence.Run.Status);
                // A batch plan receipt names the plan's decision.
                Assert.Equal(action == EngineApplierContract.BatchPlanAction ? "wait" : action,
                    occurrence.EngineReceipt?.Action);
                Assert.Single(occurrence.Run.StageResults!);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"No contract assertion for {effect}.");
        }
    }
}
