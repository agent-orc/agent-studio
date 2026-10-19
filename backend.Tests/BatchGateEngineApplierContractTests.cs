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
    /// a passed gate that holds a batch member (it carries a lane), and the
    /// sweep hold, which reaches the applier's no-lane branch.
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
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var arranged = Arrange(factory, profile, action, variant);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var run = orchestration.Create(ProjectName, arranged.TaskId, arranged.Stage, arranged.Payload,
            arranged.IdempotencyKey, arranged.SourceState, arranged.AuthorityAttemptId, arranged.Fence,
            arranged.Epoch, profile: profile);
        if (arranged.Stage != Contract.OrchestrationStage.BatchPlan)
            EngineDecisionWaitMarker.Write(CardFolder(arranged.TaskId), run.RunId);
        var claim = orchestration.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
            [arranged.Stage]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        var completed = await orchestration.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest(
            "engine", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, arranged.Stage, arranged.StageAction,
            arranged.Output, "complete:" + run.RunId), CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal("completed", completed.Status);
        var row = Assert.IsType<EngineApplierEffect>(EngineApplierContract.Find(action));
        var lane = row.Lane == EngineApplierContract.StageLane ? arranged.PayloadLane : row.Lane;
        using var payload = JsonDocument.Parse(arranged.Payload);
        var owed = EngineApplierContract.Owed(row, lane, arranged.Stage == Contract.OrchestrationStage.GateDispatch,
            payload.RootElement.TryGetProperty("reviewOutcome", out _),
            payload.RootElement.TryGetProperty("reviewAttemptRequest", out var request)
                && request.ValueKind == JsonValueKind.Object);
        Assert.Contains(EngineApplierEffectKind.DecisionReceipt, owed);
        var occurrence = orchestration.GetOccurrence(run.RunId)!;
        foreach (var effect in owed)
            AssertApplied(factory, effect, occurrence, arranged, lane, action);
        // The canary reads the same effects with its probe.
        var check = factory.Services.GetRequiredService<EngineApplierEffectProbe>().Check(occurrence);
        Assert.True(check.Verified, $"{profile}/{action}: {check.Evidence} Errors: {_errors}");
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
        using var factory = BuildFactory(authorityOnTestClock: true, configuration: OneMatchThreshold);
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
        // Nothing is watched any more, and no second alert follows.
        Assert.Equal(0, canary.CheckOnce());
        Assert.Null(factory.Services.GetRequiredService<BatchGateStore>().ReadPending(deferred.Member.ReviewAttemptId));
    }

    [Fact]
    public async Task Applier_canary_passes_review_report_after_three_applied_engine_decisions()
    {
        using var factory = BuildFactory(authorityOnTestClock: true, configuration: OneMatchThreshold);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        StartReviewReportCanary(factory);
        var canary = ActivatorUtilities.CreateInstance<EngineApplierCanaryService>(factory.Services);

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
        "ReviewReport" or "OperatorSweep" => ArrangeReview(factory, profile, Enum.Parse<TransitionAction>(action)),
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

    /// <summary>A settled review of a card in Auto Review, as the review report leaves it.</summary>
    private ArrangedDecision ArrangeReview(WebApplicationFactory<Program> factory, string profile,
        TransitionAction action)
    {
        var infrastructure = action == TransitionAction.ReviewInfrastructureRetry;
        var member = SeedMember(factory, ContractKey, "docs/applier.md", settleReview: false,
            deferredPlan: action == TransitionAction.DeferToBatch);
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
        MonolithOrchestrationOccurrence occurrence, ArrangedDecision arranged, string? lane, string action)
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
                break;
            case EngineApplierEffectKind.BatchPlanIntent:
                Assert.True(factory.Services.GetRequiredService<BatchGatePilotService>()
                    .ReceivedEngineBatchPlan(runId));
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
