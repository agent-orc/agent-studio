using AgentStudio.Runner;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Contract = AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class AcceptanceIntegrationRoundTripTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [Trait("Category", "MachineBound")]
    public async Task Verified_delivery_recalls_only_the_obsolete_machine_stale_park(bool humanHold,
        bool retryOwnerPark, bool systemProxy)
    {
        var sha = PublishDelivery("recall.txt", "delivery\n");
        var deps = Build(sha, guarded: true);
        var lifecycle = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        var stranding = new AutoReviewStrandingWatchdog(deps.Scanner, deps.Authority,
            lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        // A previous stale recovery parked before another worker integrated it.
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.AutoReview, _watchPath).Status);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var integrated = await stranding.IntegrateCurrentDeliveryAsync(card);
        Assert.True(integrated!.Outcome.IsSuccessfulIntegration());
        PublishIntegratedStaleFixture(deps);
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var status = deps.Integration.BuildLookup([card])[card.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
        if (retryOwnerPark)
            new StaleVerdictRecovery(stranding.CurrentDeliveryIdentity(card)!,
                stranding.CurrentVerdictIdentity(card), "waiting-retry").Write(card);
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.Escalated, _watchPath,
            cause: humanHold && !systemProxy ? "operator" : TimelineActors.System,
            reason: retryOwnerPark ? "The gate environment retry budget was exhausted."
                : "The delivery verdict became stale, but the current delivery could not be integrated and sent for review: host failure",
            transitionCause: LaneChangeCauses.Escalated,
            transitionDetail: humanHold ? "operator-decision" : retryOwnerPark
                ? AcceptedIntegrationFailureCodes.GateEnvironmentFailure : DeliveryChainReasons.VerdictStale).Status);
        var previous = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        var reconciler = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
            deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
            stranding: stranding, timeline: deps.Timeline);

        await reconciler.RunOnceAsync();
        if (humanHold)
        {
            Assert.Equal(TaskStates.Escalated, deps.Scanner.FindJob(Slug, _watchPath)!.State);
            Assert.Equal(previous, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
            return;
        }
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
        await WaitUntilAsync(() => deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId != previous);
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, card.State);
        Assert.Contains(deps.Timeline.ReadAll(card.FolderPath), e => e.Kind == TimelineEventKinds.DeliveryReviewRequested);
        var review = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        await reconciler.RunOnceAsync();
        Assert.Equal(review, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [Trait("Category", "MachineBound")]
    public async Task Integrated_verified_delivery_replaces_a_superseded_verdict_only(bool changedContent,
        bool supersededVerdict, bool newRunSameContent)
    {
        var sha = PublishDelivery("reviewed.txt", "old delivery\n");
        var deps = Build(sha, guarded: true);
        var previousReview = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        var oldReview = previousReview.AttemptId;
        if (supersededVerdict)
        {
            var subject = previousReview.Subject;
            var pending = deps.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                TaskKey, "PROJ-FIXTURE", sha, previousReview.SourceRunAttemptId,
                subject.TaskRequirementsHash, subject.ReviewPolicyHash, [], "superseded-review"));
            Assert.True(pending.Accepted, pending.Message);
            deps.Authority.SupersedeOpenReviewAttempts(_ => "The reviewed delivery was superseded.");
            oldReview = pending.AttemptId;
        }
        if (changedContent || newRunSameContent)
        {
            if (changedContent)
            {
                RunGit(_repo, $"--git-dir={_origin}", "update-ref", "-d", $"refs/heads/{DeliveryRef}");
                sha = PublishDelivery("new.txt", "new delivery\n");
            }
            var run = deps.Authority.AcquireRun(TaskKey, "PROJ-FIXTURE", null,
                "agent-runner-01", "host-fixture", 60, "second-delivery").RunAttempt!;
            var settled = deps.Authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-second"),
                Outcome = "done", ResultSha = sha,
                ResultEnvelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
                    "PROJ-FIXTURE", run.AttemptId, Git(_repo, "rev-parse", $"{sha}^").Out.Trim(),
                    sha, $"refs/heads/{DeliveryRef}", null, new string('a', 64), RepositoryUrl: _origin),
            });
            Assert.True(settled.Accepted, settled.Message);
            var folder = deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath;
            TaskJsonFile.UpdateField(folder, "commit", Commit(sha), NullLogger.Instance);
            TaskJsonFile.UpdateField(folder, "commits", new[] { Commit(sha) }, NullLogger.Instance);
            deps.Scanner.InvalidateCache();
        }
        var lifecycle = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        var stranding = new AutoReviewStrandingWatchdog(deps.Scanner, deps.Authority,
            lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.AutoReview, _watchPath).Status);
        var integrated = await stranding.IntegrateCurrentDeliveryAsync(deps.Scanner.FindJob(Slug, _watchPath)!);
        Assert.True(integrated!.Outcome.IsSuccessfulIntegration(), integrated.Error);
        PublishIntegratedStaleFixture(deps);
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.HumanReview, _watchPath).Status);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var status = deps.Integration.BuildLookup([card])[card.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
        var reconciler = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
            deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
            stranding: stranding, timeline: deps.Timeline);
        await reconciler.RunOnceAsync();
        if (!changedContent && !supersededVerdict && !newRunSameContent)
        {
            Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
            Assert.Equal(oldReview, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
            return;
        }
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
        await WaitUntilAsync(() => deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId != oldReview);
        Assert.Equal(sha, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.Subject.ExpectedResultSha);
        Assert.Equal(deps.Authority.GetTaskProjection(TaskKey).CurrentRunAttempt!.AttemptId,
            deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.SourceRunAttemptId);
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Category", "MachineBound")]
    public async Task Stale_recovery_waits_for_environment_owner_then_observes_another_workers_success(bool changedFault,
        bool capacity)
    {
        var sha = PublishDelivery("environment-recovery.txt", "delivery\n");
        var gate = new StaleRecoveryGate { Capacity = capacity };
        var deps = Build(sha, guarded: true, gateRunner: gate);
        var lifecycle = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        var stranding = new AutoReviewStrandingWatchdog(deps.Scanner, deps.Authority,
            lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        var escalation = new HumanReviewEscalation(deps.States, deps.Transitions, deps.Configuration,
            NullLogger<HumanReviewEscalation>.Instance, deps.Scanner);
        var reconciler = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
            deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
            stranding: stranding, timeline: deps.Timeline, escalation: escalation);
        await reconciler.RunOnceAsync();
        await WaitUntilAsync(() =>
        {
            var current = deps.Scanner.FindJob(Slug, _watchPath)!;
            return current.State == TaskStates.Escalated
                || StaleVerdictRecovery.Read(current)?.Phase == (capacity ? "waiting-capacity" : "waiting-retry");
        });
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, card.State);
        Assert.Equal(capacity ? AcceptedIntegrationFailureCodes.GateCapacity
                : AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            deps.Integration.BuildLookup([card])[card.TaskKey].Failure?.Code);
        Assert.NotEqual(IntegrationVerificationStates.Verified,
            deps.Integration.BuildLookup([card])[card.TaskKey].Verification?.State);
        if (!capacity)
        {
            var invocations = gate.Invocations;
            await reconciler.RunOnceAsync();
            Assert.Equal(invocations, gate.Invocations);
        }
        if (changedFault)
        {
            const string failure = "The retry owner ended with a different generic integration failure.";
            var step = deps.Integration.ReadLatestMergeStep(card)!;
            deps.Pipeline.RecordStep(card.FolderPath, step with
            {
                FailureCode = AcceptedIntegrationFailureCodes.IntegrationError,
                Reason = failure,
            });
            var unavailable = new AutoReviewStrandingWatchdog(deps.Scanner, deps.Authority,
                lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
                deps.Transitions, null!, null!, null!, null!, deps.Configuration,
                NullLogger<AutoReviewStrandingWatchdog>.Instance);
            var failed = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
                deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
                stranding: unavailable, timeline: deps.Timeline, escalation: escalation);
            var verdict = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
            await failed.RunOnceAsync();
            await WaitUntilAsync(() => deps.Scanner.FindJob(Slug, _watchPath)!.State == TaskStates.Escalated);
            var reason = deps.Scanner.FindJob(Slug, _watchPath)!.ParkedBlocker!.Reason;
            Assert.Contains(sha, reason);
            Assert.Contains(verdict, reason);
            Assert.Contains(failure, reason);
            Assert.Contains("Continue", reason);
            return;
        }
        gate.Healthy = true;
        var integrated = await stranding.IntegrateCurrentDeliveryAsync(card);
        Assert.True(integrated!.Outcome.IsSuccessfulIntegration(), integrated.Error);
        PublishIntegratedStaleFixture(deps);
        // Reconstruct the reconciler to prove custody survives process restart.
        var restarted = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
            deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
            stranding: stranding, timeline: deps.Timeline, escalation: escalation);
        await restarted.RunOnceAsync();
        await WaitUntilAsync(() => deps.Timeline.ReadAll(deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath)
            .Any(e => e.Kind == TimelineEventKinds.DeliveryReviewRequested));
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Verified_delivery_waits_when_fresh_review_creation_is_unavailable()
    {
        var sha = PublishDelivery("review-handoff.txt", "delivery\n");
        var deps = Build(sha, guarded: true);
        // A separate authority epoch cannot mint a review for this settled run.
        var unavailable = new ReviewAttemptTaskLifecycleService(RebuiltAuthority(),
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        AutoReviewStrandingWatchdog Watchdog(ReviewAttemptTaskLifecycleService lifecycle) => new(
            deps.Scanner, deps.Authority, lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        var escalation = new HumanReviewEscalation(deps.States, deps.Transitions, deps.Configuration,
            NullLogger<HumanReviewEscalation>.Instance, deps.Scanner);
        DeliveryChainReconciler Reconciler(AutoReviewStrandingWatchdog watchdog) => new(
            deps.Scanner, deps.Integration, deps.Transitions, deps.Configuration,
            NullLogger<DeliveryChainReconciler>.Instance, stranding: watchdog,
            timeline: deps.Timeline, escalation: escalation);
        var previous = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        await Reconciler(Watchdog(unavailable)).RunOnceAsync();
        await WaitUntilAsync(() =>
        {
            var current = deps.Scanner.FindJob(Slug, _watchPath)!;
            return current.State == TaskStates.Escalated
                || StaleVerdictRecovery.Read(current)?.Phase == "requesting-review";
        });
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, card.State);
        PublishIntegratedStaleFixture(deps);
        Assert.Equal(IntegrationVerificationStates.Verified,
            deps.Integration.BuildLookup([card])[card.TaskKey].Verification?.State);
        Assert.Equal(previous, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
        var restored = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var restarted = Reconciler(Watchdog(restored));
        await restarted.RunOnceAsync();
        await WaitUntilAsync(() => deps.Timeline.ReadAll(deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath)
            .Any(e => e.Kind == TimelineEventKinds.DeliveryReviewRequested));
        var requested = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        Assert.NotEqual(previous, requested);
        // Simulate a crash after review creation and timeline append, before
        // marking the durable handoff complete.
        await WaitUntilAsync(() => StaleVerdictRecovery.Read(deps.Scanner.FindJob(Slug, _watchPath)!)?.Phase == "reviewed");
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        (StaleVerdictRecovery.Read(card)! with { Phase = "requesting-review" }).Write(card);
        await Reconciler(Watchdog(restored)).RunOnceAsync();
        await WaitUntilAsync(() => StaleVerdictRecovery.Read(deps.Scanner.FindJob(Slug, _watchPath)!)?.Phase == "reviewed");
        Assert.Equal(requested, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
        Assert.Single(deps.Timeline.ReadAll(deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath),
            e => e.Kind == TimelineEventKinds.DeliveryReviewRequested);
        var projection = deps.Authority.GetTaskProjection(TaskKey);
        var pending = projection.CurrentReviewAttempt!;
        var external = deps.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey, "PROJ-FIXTURE", sha, projection.CurrentRunAttempt!.AttemptId,
            pending.Subject.TaskRequirementsHash, pending.Subject.ReviewPolicyHash, [], "another-worker-review"));
        Assert.True(external.Accepted, external.Message);
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var healthy = Watchdog(restored);
        Assert.True(healthy.RequestFreshReview(card, healthy.CurrentDeliveryIdentity(card)));
        Assert.Equal(external.AttemptId, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    public async Task Engine_recovery_receipt_reviews_only_its_immutable_delivery(bool superseded)
    {
        var sha = PublishDelivery("engine-recovery.txt", "delivery\n");
        var deps = Build(sha, guarded: true, initialState: TaskStates.AutoReview);
        var lifecycle = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        var stranding = new AutoReviewStrandingWatchdog(deps.Scanner, deps.Authority,
            lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.True((await stranding.IntegrateCurrentDeliveryAsync(card))!.Outcome.IsSuccessfulIntegration());
        PublishIntegratedStaleFixture(deps);
        card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var status = deps.Integration.BuildLookup([card])[card.TaskKey];
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
        var previous = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        var reconciler = new DeliveryChainReconciler(deps.Scanner, deps.Integration,
            deps.Transitions, deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance,
            stranding: stranding, timeline: deps.Timeline);
        var recovery = reconciler.BeginRecovery(card) with
        { Phase = "awaiting-decision", DecisionFacts = new(true) };
        recovery.Write(card);
        var facts = DeliveryChainReconciler.Facts(card, status, false) with { Recovery = recovery.DecisionFacts! with
        { DeliveryIdentity = recovery.Delivery, VerdictIdentity = recovery.Verdict,
            LaneEnteredTicks = card.EnteredLaneAt.Ticks } };
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.DeliveryChain,
            AiOperatorDecisionGateway.FactsSubjectKey("delivery", card.Id, facts), DeliveryChain: facts);
        var application = new AiOperatorApplication("engine-recovery", Project, Slug, card.State,
            "engine-recovery-write", payload, AiOperatorDecisionPolicy.Decide(payload), "", 0, 0);
        if (superseded)
            Assert.True(deps.Authority.AcquireRun(TaskKey, "PROJ-FIXTURE", null,
                "runner", "host", 60, "superseding-engine-recovery").Accepted);
        await reconciler.ApplyAsync(application, CancellationToken.None);
        if (superseded)
        {
            Assert.Equal(previous, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
            Assert.DoesNotContain(deps.Timeline.ReadAll(card.FolderPath),
                e => e.Kind == TimelineEventKinds.DeliveryReviewRequested);
            return;
        }
        Assert.NotEqual(previous, deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId);
        await reconciler.ApplyAsync(application, CancellationToken.None);
        Assert.Single(deps.Timeline.ReadAll(card.FolderPath),
            e => e.Kind == TimelineEventKinds.DeliveryReviewRequested);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Engine_recovery_wakes_after_a_completed_receipt_and_reuses_the_delivery()
    {
        var sha = PublishDelivery("engine-retry.txt", "delivery\n");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(sha, guarded: true, initialState: TaskStates.AutoReview, gateRunner: gate);
        var healthyLifecycle = new ReviewAttemptTaskLifecycleService(deps.Authority,
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var unavailableLifecycle = new ReviewAttemptTaskLifecycleService(RebuiltAuthority(),
            deps.Scanner, deps.Timeline, NullLogger<ReviewAttemptTaskLifecycleService>.Instance);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(deps.Merge, deps.Scanner,
            deps.Provenance, deps.Pipeline, deps.Timeline, AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, deps.Authority);
        AutoReviewStrandingWatchdog Watchdog(ReviewAttemptTaskLifecycleService lifecycle) => new(
            deps.Scanner, deps.Authority, lifecycle, null!, null!, deps.Settings, deps.Pipeline, null!,
            deps.Transitions, null!, null!, null!, null!, deps.Configuration,
            NullLogger<AutoReviewStrandingWatchdog>.Instance, coordinator);
        var healthy = Watchdog(healthyLifecycle);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.True((await healthy.IntegrateCurrentDeliveryAsync(card))!.Outcome.IsSuccessfulIntegration());
        PublishIntegratedStaleFixture(deps);
        var integrations = gate.Invocations;
        deps.Configuration["Orchestration:Transitions:OperatorDecision"] = "Engine";
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var applier = new DeferredRecoveryApplier();
        var store = new MonolithOrchestrationStore(Path.Combine(_tempDir, "recovery-engine"), time, applier);
        var gateway = new AiOperatorDecisionGateway(deps.Configuration, store);
        DeliveryChainReconciler Reconciler(AutoReviewStrandingWatchdog watchdog) => new(
            deps.Scanner, deps.Integration, deps.Transitions, deps.Configuration,
            NullLogger<DeliveryChainReconciler>.Instance, decisions: gateway,
            stranding: watchdog, timeline: deps.Timeline);
        async Task<Contract.OrchestrationClaimResponse> ClaimNextAsync()
        {
            Contract.OrchestrationClaimResponse? claimed = null;
            await WaitUntilAsync(() =>
            {
                var next = store.Claim(new Contract.OrchestrationClaimRequest("engine_recovery", "instance",
                    [Contract.OrchestrationStage.OperatorDecision]));
                if (next.Run is null) return false;
                claimed = next;
                return true;
            });
            return claimed!;
        }
        async Task CompleteAsync(Contract.OrchestrationClaimResponse claim, string key)
        {
            var payload = AiOperatorDecisionGateway.ReadPayload(claim.Run!.PayloadJson);
            var decision = AiOperatorDecisionPolicy.Decide(payload);
            Assert.Equal(AiOperatorEffects.RequestFreshDeliveryReview, decision.Effect);
            var completed = await store.CompleteAsync(claim.Run.RunId,
                new Contract.CompleteOrchestrationStageRequest("engine_recovery", "instance",
                    claim.Lease!.LeaseId, claim.Lease.Fence, Contract.OrchestrationStage.OperatorDecision,
                    Contract.OrchestrationAction.Continue, JsonSerializer.Serialize(new { operatorDecision = decision }), key),
                CancellationToken.None);
            Assert.Equal("completed", completed.Status);
        }
        var first = Reconciler(Watchdog(unavailableLifecycle));
        applier.Inner = new DeliveryChainTestApplier(first);
        first.BeginRecovery(deps.Scanner.FindJob(Slug, _watchPath)!);
        await first.RunOnceAsync();
        var firstClaim = await ClaimNextAsync();
        await CompleteAsync(firstClaim, "first-recovery-receipt");
        Assert.Equal("requesting-review", StaleVerdictRecovery.Read(deps.Scanner.FindJob(Slug, _watchPath)!)!.Phase);
        var previous = deps.Authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!.AttemptId;
        time.Advance(TimeSpan.FromSeconds(31));
        var restarted = Reconciler(healthy);
        applier.Inner = new DeliveryChainTestApplier(restarted);
        await restarted.RunOnceAsync();
        var nextClaim = await ClaimNextAsync();
        Assert.NotEqual(firstClaim.Run!.RunId, nextClaim.Run!.RunId);
        Assert.Equal(AiOperatorDecisionGateway.ReadPayload(firstClaim.Run.PayloadJson).SubjectKey,
            AiOperatorDecisionGateway.ReadPayload(nextClaim.Run.PayloadJson).SubjectKey);
        await CompleteAsync(nextClaim, "healthy-recovery-receipt");
        var projection = deps.Authority.GetTaskProjection(TaskKey);
        Assert.NotEqual(previous, projection.CurrentReviewAttempt!.AttemptId);
        Assert.Equal(AttemptLifecycleState.Pending, projection.CurrentReviewAttempt.State);
        Assert.Equal(2, projection.ReviewAttempts.Count);
        Assert.Equal(integrations, gate.Invocations);
    }

    private sealed class DeferredRecoveryApplier : IMonolithOrchestrationApplier
    {
        public IMonolithOrchestrationApplier Inner { get; set; } = null!;
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
            => Inner.ApplyAsync(occurrence, completion, ct);
    }

    private void PublishIntegratedStaleFixture(Deps deps)
    {
        RunGit(_repo, "push", "-q", "origin",
            $"{GitService.IntegrationLaneRef("develop")}:refs/heads/develop");
        deps.Scanner.InvalidateCache();
    }

    private sealed class StaleRecoveryGate : IBuildTestGateRunner
    {
        public bool Healthy { get; set; }
        public bool Capacity { get; set; }
        public int Invocations { get; private set; }
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new BuildTestGateResult(Healthy ? BuildTestGateVerdict.Ok : BuildTestGateVerdict.Fail,
                Healthy ? 0 : 1, 1, "", Healthy ? "passed" : Capacity ? "capacity unavailable" : "toolchain unavailable",
                Healthy || !Capacity, false)
            {
                FailureKind = Healthy ? BuildTestGateFailureKind.None : Capacity
                    ? BuildTestGateFailureKind.Timeout : BuildTestGateFailureKind.Environment,
                ViolatedBudget = !Healthy && Capacity ? new("machine-gate-queue", 120000, 120006, "queue") : null,
                ExpectedSha = request.ExpectedSha, TestedSha = request.ExpectedSha,
                TestSelection = new TestSelectionAudit { Level = TestExecutionLevels.Full,
                    FullSuiteRequired = true, FullSuiteRan = Healthy },
            });
        }
    }
}
