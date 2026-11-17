using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3883 - 16.11.2026: AGT-3876 (published to develop, card not advanced)
/// and AGT-3868 (gate environment failure, moved back by the operator) were
/// escalated by the stranding watchdog with "Per-task integration did not
/// resume: engine-owned". When the engine owns delivery the watchdog asks the
/// engine where the integration stands, reports it, and asks the engine again
/// once per lane visit; a delivery develop already carries is integrated.
/// </summary>
public sealed partial class AcceptanceIntegrationRoundTripTests
{
    private (Deps Deps, StrandingStack Stranding, FakeEngine Engine) EngineOwnedPassedReview(
        string name, EngineDeliveryIntegrationStatus state)
    {
        var deliverySha = PublishDelivery($"{name}.txt", "delivered work\n");
        var clock = VerificationFixtureAtUtc;
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview,
            reviewPlan: OwnBuildTestPlan(), reviewVerdicts: OwnBuildTestVerdicts(),
            engineMode: true, authorityClock: () => clock);
        var engine = new FakeEngine { State = state };
        return (deps, StrandingFixture(deps, engine), engine);
    }

    [Fact]
    public async Task Engine_owned_delivery_published_without_the_lane_move_asks_the_engine_to_finish_it()
    {
        var published = new string('c', 40);
        var (deps, stranding, engine) = EngineOwnedPassedReview("engine-published",
            new(EngineDeliveryIntegrationState.Published, PublishedSha: published));

        Assert.Equal(1, await stranding.Watchdog.RunOnceAsync());

        var request = Assert.Single(engine.Requests);
        Assert.Equal(EngineDeliveryIntegrationState.Published, request.State);
        var record = Assert.Single(stranding.Records.All());
        Assert.Equal(AutoReviewStrandingAction.IntegratePerTask.ToString(), record.Action);
        Assert.Equal(StallInterventionStatuses.Verified, record.Status);
        Assert.Contains("Per-task integration owned by the engine was published at ccccccc; "
            + "asked the engine to finish the lane move (orch_fake).", record.Done);
        Assert.DoesNotContain("engine-owned", record.Done);
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task Engine_owned_passed_review_without_an_engine_integration_asks_the_engine_to_start_it()
    {
        var (_, stranding, engine) = EngineOwnedPassedReview("engine-not-started",
            new(EngineDeliveryIntegrationState.NotStarted, Reason: "the engine has no decision for this review"));

        Assert.Equal(1, await stranding.Watchdog.RunOnceAsync());

        Assert.Equal(EngineDeliveryIntegrationState.NotStarted, Assert.Single(engine.Requests).State);
        var record = Assert.Single(stranding.Records.All());
        Assert.Equal(StallInterventionStatuses.Verified, record.Status);
        Assert.Contains("was not started (the engine has no decision for this review); "
            + "asked the engine to start the integration", record.Done);
    }

    [Fact]
    public async Task Engine_owned_gate_environment_failure_asks_the_engine_to_retry_the_same_delivery()
    {
        var (deps, stranding, engine) = EngineOwnedPassedReview("engine-gate-retry",
            new(EngineDeliveryIntegrationState.Failed,
                Reason: "GateEnvironment: the gate host or run budget prevented verification from completing",
                Retryable: true));
        var review = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;

        Assert.Equal(1, await stranding.Watchdog.RunOnceAsync());

        Assert.Equal(EngineDeliveryIntegrationState.Failed, Assert.Single(engine.Requests).State);
        var record = Assert.Single(stranding.Records.All());
        Assert.Equal(StallInterventionStatuses.Verified, record.Status);
        Assert.Contains("failed: GateEnvironment: the gate host or run budget prevented verification "
            + "from completing; asked the engine to retry", record.Done);
        // The retry reuses the passed review; the watchdog creates none.
        Assert.Equal(review.AttemptId, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
    }

    [Fact]
    public async Task Engine_owned_failure_that_cannot_retry_escalates_with_the_engine_reason()
    {
        var (deps, stranding, engine) = EngineOwnedPassedReview("engine-refused",
            new(EngineDeliveryIntegrationState.Failed,
                Reason: "engine decision orch_fake: Engine lane move failed: IntegrationFailed batch-gate-evidence-missing"));

        Assert.Equal(1, await stranding.Watchdog.RunOnceAsync());

        Assert.Empty(engine.Requests);
        var record = Assert.Single(stranding.Records.All());
        Assert.Equal(StallInterventionStatuses.Escalated, record.Status);
        Assert.Contains("Per-task integration owned by the engine failed: engine decision orch_fake: "
            + "Engine lane move failed: IntegrationFailed batch-gate-evidence-missing.", record.Done);
        Assert.DoesNotContain("did not resume: engine-owned", record.Done);
        Assert.NotEqual(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task Engine_owned_integration_that_still_runs_keeps_the_card()
    {
        var (deps, stranding, engine) = EngineOwnedPassedReview("engine-running",
            new(EngineDeliveryIntegrationState.Running, Reason: "engine decision orch_fake is applying"));

        Assert.Equal(0, await stranding.Watchdog.RunOnceAsync());

        Assert.Empty(engine.Requests);
        Assert.Empty(stranding.Records.All());
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    /// <summary>
    /// AGT-3868: the engine's integration rolled back on a gate environment
    /// fault and the operator moved the card back. The engine reads the failure
    /// as retryable, reopens the settlement and creates one fresh decision on
    /// the same review, with the review report's own payload.
    /// </summary>
    [Fact]
    public async Task Engine_reads_a_gate_environment_failure_and_requests_one_retry_per_lane_visit()
    {
        var deliverySha = PublishDelivery("engine-real-retry.txt", "delivered work\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview,
            reviewPlan: OwnBuildTestPlan(), reviewVerdicts: OwnBuildTestVerdicts(), engineMode: true);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var review = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        var (store, engine) = EngineStore(deps);
        var reported = await CompletedReviewReportOccurrence(store, review);
        RemoteDeliverySettlementStore.Write(card.FolderPath, new RemoteDeliverySettlementRecord
        {
            TaskKey = TaskKey,
            ReviewAttemptId = review.AttemptId,
            Outcome = "Pass",
            ShouldIntegrate = true,
            IntegrationBranch = "develop",
            Stage = RemoteDeliverySettlementStage.IntegrationSettled,
            IntegrationOutcome = nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure),
            IntegrationDetail = "The build gate blocked the merge into develop: host lost. GateEnvironment: "
                + "the same reviewed delivery will be retried without a new review.",
        });

        var failed = engine.Read(card, review);
        Assert.Equal(EngineDeliveryIntegrationState.Failed, failed.State);
        Assert.True(failed.Retryable);
        Assert.Contains("retried without a new review", failed.Describe());

        var asked = engine.Request(card, review, failed);
        Assert.True(asked.Started, asked.Detail);
        var settlement = RemoteDeliverySettlementStore.Read(card.FolderPath)!;
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationPending, settlement.Stage);
        Assert.True(settlement.DiscardCompletedReplay);
        Assert.Null(settlement.IntegrationOutcome);
        var fresh = store.ForAuthorityAttempt(Project, review.AttemptId).Last();
        Assert.NotEqual(reported.RunId, fresh.Run.RunId);
        Assert.Equal("pending", fresh.Run.Status);
        Assert.True(fresh.Claimable);
        Assert.Equal("ReviewReport", fresh.Profile);
        Assert.Equal(Contract.OrchestrationStage.ReviewDecision, fresh.Run.CurrentStage);
        Assert.Equal(reported.PayloadJson, fresh.Run.PayloadJson);
        Assert.Equal(TaskStates.AutoReview, fresh.ExpectedSourceState);

        Assert.Equal(EngineDeliveryIntegrationState.Running, engine.Read(card, review).State);
        var again = engine.Request(card, review, failed);
        Assert.False(again.Started);
        Assert.Contains("already asked in this lane visit", again.Detail);
    }

    [Fact]
    public void Engine_reads_a_passed_review_it_never_decided_as_not_started()
    {
        var deliverySha = PublishDelivery("engine-real-idle.txt", "delivered work\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview,
            reviewPlan: OwnBuildTestPlan(), reviewVerdicts: OwnBuildTestVerdicts(), engineMode: true);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var review = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        var (store, engine) = EngineStore(deps);

        var idle = engine.Read(card, review);
        Assert.Equal(EngineDeliveryIntegrationState.NotStarted, idle.State);
        Assert.Equal("not started (the engine has no decision for this review)", idle.Describe());

        Assert.True(engine.Request(card, review, idle).Started);
        // Without a report occurrence the passed review and its journal carry the facts.
        using var payload = JsonDocument.Parse(store.ForAuthorityAttempt(Project, review.AttemptId).Single().Run.PayloadJson);
        Assert.Equal("Pass", payload.RootElement.GetProperty("reviewOutcome").GetString());
        Assert.Equal(review.AttemptId, payload.RootElement.GetProperty("reviewAttemptId").GetString());
        Assert.Equal(deliverySha, payload.RootElement.GetProperty("resultSha").GetString());
        Assert.False(payload.RootElement.GetProperty("reviewPlan").GetProperty("buildTestDeferredToBatch").GetBoolean());
    }

    /// <summary>
    /// AGT-3876: develop carries the delivery as a net-change publication and
    /// then moved on. The card is integrated, the engine reports it published
    /// at that commit, and the monolith resume only moves the card: no second
    /// integration, no gate.
    /// </summary>
    [Fact]
    public async Task Delivery_already_on_develop_as_a_net_change_after_develop_moved_is_integrated_once()
    {
        var deliverySha = PublishDelivery("net-change-auto-review.txt", "delivered work\n");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, gateRunner: gate, initialState: TaskStates.AutoReview);
        var netChange = PublishLaterCommit("net-change-auto-review-net.txt", deliverySha);
        var laterTip = PublishLaterCommit("net-change-auto-review-later.txt");
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        IntegrationVerificationStore.Write(card.FolderPath, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = netChange,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.MergeGate,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            DeliveryShas = [netChange],
        });
        var review = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        RemoteDeliverySettlementStore.Write(card.FolderPath, new RemoteDeliverySettlementRecord
        {
            TaskKey = TaskKey,
            ReviewAttemptId = review.AttemptId,
            Outcome = "Pass",
            ShouldIntegrate = true,
            IntegrationBranch = "develop",
            IntegrationStrategy = IntegrationStrategies.DirectMerge,
            Stage = RemoteDeliverySettlementStage.IntegrationPending,
        });
        deps.Scanner.InvalidateCache();
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "origin/develop").Code);

        var status = deps.Integration.BuildLookup([card])[card.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Contains("net-change", status.Detail);
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
        Assert.Equal(IntegrationStatuses.Integrated, deps.Integration.BuildForAcceptance(card).Status);

        var (_, engine) = EngineStore(deps);
        var read = engine.Read(card, review);
        Assert.Equal(EngineDeliveryIntegrationState.Published, read.State);
        Assert.Equal(netChange, read.PublishedSha);

        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        var escalation = new HumanReviewEscalation(deps.States, deps.Transitions, deps.Configuration,
            NullLogger<HumanReviewEscalation>.Instance, deps.Scanner);
        var resume = new AutoReviewDeliveryResumeService(deps.Scanner, deps.Authority, deps.Integration,
            deps.Settings, coordinator, deps.Transitions, escalation,
            NullLogger<AutoReviewDeliveryResumeService>.Instance, configuration: deps.Configuration);

        var resumed = await resume.ResumeAsync(card, "test");

        Assert.True(resumed.Resumed, resumed.Reason + " " + resumed.Detail);
        Assert.Equal(AutoReviewResumeAction.CompleteTransition, resumed.Action);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(laterTip, Git(_repo, "rev-parse", "origin/develop").Out.Trim());
    }

    private (MonolithOrchestrationStore Store, EngineDeliveryIntegrationService Engine) EngineStore(Deps deps)
    {
        var store = new MonolithOrchestrationStore(Path.Combine(_tempDir, "occurrences"),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(VerificationFixtureAtUtc)),
            new RecordingApplier());
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return (store, new EngineDeliveryIntegrationService(services, deps.Integration,
            NullLogger<EngineDeliveryIntegrationService>.Instance));
    }

    /// <summary>The review report's engine decision, claimed and applied by a recording applier.</summary>
    private static async Task<Contract.OrchestrationRunDto> CompletedReviewReportOccurrence(
        MonolithOrchestrationStore store, ReviewAttemptDto review)
    {
        var payload = JsonSerializer.Serialize(new StagePayload(ReviewOutcome: "Pass",
            ReviewAttemptId: review.AttemptId, ResultSha: review.Subject.ExpectedResultSha,
            ReviewPlan: review.Subject.Plan), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var run = store.Create(Project, Slug, Contract.OrchestrationStage.ReviewDecision, payload,
            $"review-report:{review.AttemptId}:fixture", TaskStates.AutoReview, review.AttemptId,
            review.LastFence, review.AuthorityEpoch, claimable: true, profile: "ReviewReport");
        var claim = store.Claim(new Contract.OrchestrationClaimRequest("engine_fixture", "instance",
            [Contract.OrchestrationStage.ReviewDecision], 60));
        Assert.Equal(run.RunId, claim.Run?.RunId);
        var completed = await store.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest(
            "engine_fixture", "instance", claim.Lease!.LeaseId, claim.Lease.Fence,
            Contract.OrchestrationStage.ReviewDecision, Contract.OrchestrationAction.Continue, "{}",
            "fixture-completion"), CancellationToken.None);
        Assert.Equal("completed", completed.Status);
        return completed;
    }

    private sealed class RecordingApplier : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A fake engine that reports one scripted state and records every request.</summary>
    private sealed class FakeEngine : IEngineDeliveryIntegration
    {
        public required EngineDeliveryIntegrationStatus State { get; init; }

        public List<EngineDeliveryIntegrationStatus> Requests { get; } = [];

        public EngineDeliveryIntegrationStatus Read(TaskInfo card, ReviewAttemptDto review) => State;

        public EngineDeliveryIntegrationRequest Request(TaskInfo card, ReviewAttemptDto review,
            EngineDeliveryIntegrationStatus status)
        {
            Requests.Add(status);
            return new(true, status.State switch
            {
                EngineDeliveryIntegrationState.Published => "asked the engine to finish the lane move (orch_fake)",
                EngineDeliveryIntegrationState.Failed => "asked the engine to retry the integration of the same reviewed delivery (orch_fake)",
                _ => "asked the engine to start the integration (orch_fake)",
            });
        }
    }
}
