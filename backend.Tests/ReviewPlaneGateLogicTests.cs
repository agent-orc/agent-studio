using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

// clock-independent: the fixed delivery times only order queue entries and never meet "now".

/// <summary>
/// AGT-3100: the decisions behind the MachineBound
/// <c>AutoReviewRestartDrillTests</c>, the <c>Monolith_v1_review_plane_*</c>
/// rows of <c>RemoteRunnerEndToEndTests</c> and the AGT-3091
/// <c>ReviewSettlementReissueRaceTests</c>, without a process, socket or git
/// repository. Every gate filters <c>Category!=MachineBound</c>, so those tests
/// went red unnoticed when the behaviour below changed on purpose; this class
/// keeps the same decisions on the gate.
/// </summary>
public sealed class ReviewPlaneGateLogicTests
{
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string CitedPath = "backend/Features/Runner/ProjectRunner.cs";
    private static readonly DateTimeOffset DeliveredAt = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    // ---- Report classification (AGT-2916) ----------------------------------

    /// <summary>
    /// A semantic block charges the card only when its aspect is a planned
    /// agent aspect, it names the checked evidence and the missing gap, and the
    /// evidence cites a path the delivery changed. Every other block degrades to
    /// a concern and the report settles as Pass.
    /// </summary>
    [Theory]
    [InlineData(true, CitedPath, "Preserve pending state.", "ProductFailure", "block")]
    [InlineData(false, CitedPath, "Preserve pending state.", "Pass", "block")]
    [InlineData(true, "backend/Features/Other.cs", "Preserve pending state.", "Pass", "concerns")]
    [InlineData(true, CitedPath, "none", "Pass", "concerns")]
    public void A_semantic_block_charges_the_card_only_on_a_planned_aspect_citing_a_changed_path(
        bool planned, string changedPath, string missing, string expectedOutcome, string expectedStatus)
    {
        var plan = planned ? CodeQualityPlan() : null;
        var report = Report("ProductFailure", changedPath,
            new Contract.ReviewVerdictDto("code-quality", "block", "RemoteAspectVerdict",
                "Infra crash recovery loses pending state", CitedPath, missing));

        var normalized = NormalizeLikeTheReportEndpoint(report, plan);

        Assert.Equal(expectedOutcome, normalized.Outcome);
        Assert.Equal(expectedStatus, Assert.Single(normalized.Verdicts).Status);
    }

    /// <summary>
    /// The infrastructure rows of the classification test: an aspect reply the
    /// runner could not parse is a retry of that aspect, never a coding round.
    /// </summary>
    [Theory]
    [InlineData("review:unparseable", Contract.ReviewFollowUpAction.RetryAspect)]
    [InlineData("ReviewInfra", Contract.ReviewFollowUpAction.RetryAspect)]
    [InlineData("RemoteAspectVerdict", Contract.ReviewFollowUpAction.ReviewFindingRound)]
    public void An_unparseable_aspect_reply_retries_the_aspect_instead_of_reopening_the_card(
        string classification, Contract.ReviewFollowUpAction expected)
    {
        var decision = Contract.ReviewFollowUpPolicy.Decide(
            [new Contract.ReviewFollowUpFinding("code-quality", "block", "Reviewer unavailable.",
                CitedPath, "Preserve pending state.", classification)],
            concernRoundsUsed: 0,
            maxConcernRounds: 1);

        Assert.Equal(expected, decision.Action);
    }

    // ---- Claim admission and report admission of sealed plans --------------

    /// <summary>
    /// A plan <c>RemoteReviewPlanBuilder</c> builds is sealed as library
    /// version 1. The registration the end-to-end fixture sent before the
    /// review library lacks exactly the keys that left its attempts unclaimable
    /// (claim status <c>empty</c>, reason <c>unclaimable-plan-requirements</c>).
    /// </summary>
    [Fact]
    public void A_sealed_library_plan_is_claimable_only_by_a_current_review_registration()
    {
        var plan = Contract.ReviewLibraryStepPolicy.Seal(CodeQualityPlan(), ResultSha);
        var preLibrary = new HashSet<string>(StringComparer.Ordinal)
        {
            Contract.ReviewCapabilities.ReviewExecutor,
            Contract.ReviewCapabilities.BaselineComparison,
            Contract.ReviewCapabilities.DependencyPreparation,
            Contract.ReviewCapabilities.GitMaterialization,
            Contract.ReviewCapabilities.SemanticReview,
        };
        var current = new HashSet<string>(preLibrary, StringComparer.Ordinal)
        {
            Contract.ReviewCapabilities.LibraryStepV1,
            Contract.CapabilityProtocol.CliExecution(CliTypes.Codex),
            Contract.CapabilityProtocol.ProviderAuthentication(CliTypes.Codex),
        };

        Assert.False(Contract.ReviewLibraryStepPolicy.Supports(plan, preLibrary));
        Assert.Equal(
            new[]
            {
                Contract.CapabilityProtocol.CliExecution(CliTypes.Codex),
                Contract.CapabilityProtocol.ProviderAuthentication(CliTypes.Codex),
                Contract.ReviewCapabilities.LibraryStepV1,
            },
            Contract.ReviewLibraryStepPolicy.MissingCapabilities(plan, preLibrary));
        Assert.True(Contract.ReviewLibraryStepPolicy.Supports(plan, current));
    }

    /// <summary>
    /// A Pass on a sealed plan must account for every required step with the
    /// step's own envelope; a failure outcome may report without evidence.
    /// </summary>
    [Fact]
    public void A_pass_on_a_sealed_plan_needs_evidence_for_every_required_step()
    {
        var plan = Contract.ReviewLibraryStepPolicy.Seal(CodeQualityPlan(), ResultSha);
        var evidence = plan.Commands.Select(command => new Contract.ReviewCommandEvidenceDto(
                command.StepId, command.Aspect, command.FileName, command.Arguments,
                ResultSha, ResultSha, new string('c', 40),
                DeliveredAt.UtcDateTime, DeliveredAt.UtcDateTime, 0, null,
                new string('d', 64), new string('e', 64),
                ExecutionKind: command.ExecutionKind,
                LibraryStep: command.LibraryStep))
            .ToArray();

        Assert.False(Contract.ReviewLibraryStepPolicy.ValidReport(plan, [], "Pass"));
        Assert.True(Contract.ReviewLibraryStepPolicy.ValidReport(plan, [], "ReviewInfra"));
        Assert.True(Contract.ReviewLibraryStepPolicy.ValidReport(plan, evidence, "Pass"));
        Assert.False(Contract.ReviewLibraryStepPolicy.ValidReport(
            plan, [evidence[0] with { LibraryStep = null }], "Pass"));
    }

    // ---- Retry budget versus cause breaker (AGT-W57) -----------------------

    /// <summary>
    /// Three identical infrastructure failures are one cause: the third opens
    /// the breaker and parks the card instead of scheduling another retry.
    /// The bounded retry chain therefore only runs out on distinct causes.
    /// </summary>
    [Fact]
    public void Three_identical_infrastructure_failures_open_the_breaker_and_distinct_ones_keep_retrying()
    {
        var thresholds = CauseBreakerPolicy.Clamp(
            CauseBreakerPolicy.DefaultAttemptThreshold,
            CauseBreakerPolicy.DefaultCardThreshold,
            CauseBreakerPolicy.DefaultWindowHours);
        var now = DeliveredAt.UtcDateTime;
        var identical = Enumerable.Range(0, 3)
            .Select(attempt => new CauseBreakerCount("AGT-RUNNER-E2E", now.AddMinutes(attempt)))
            .ToArray();

        Assert.Equal(CauseBreakerAction.Retry,
            CauseBreakerPolicy.Decide(false, identical[..2], thresholds, now.AddMinutes(2)).Action);
        Assert.Equal(CauseBreakerAction.Open,
            CauseBreakerPolicy.Decide(false, identical, thresholds, now.AddMinutes(2)).Action);

        string[] causes = ["SnapshotUnavailable", "PreparationFailed", "BaselineUnavailable", "CommandStalled"];
        var fingerprints = causes
            .Select(cause => CauseFingerprintPolicy.Compute(
                "ReviewInfra", cause, "The immutable snapshot was unavailable.", null, CauseFingerprintPolicy.NoToolchain).Value)
            .ToArray();
        Assert.Equal(causes.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- Integration lane (AGT-2996) ---------------------------------------

    /// <summary>
    /// The lane, not the checkout's develop, follows origin. A local-only
    /// develop commit is no input at all; an unpublished lane merge that origin
    /// moved past is a divergence and parks the card as branch-sync-failed.
    /// </summary>
    [Fact]
    public void An_unpublished_lane_merge_that_origin_moved_past_parks_as_branch_sync_failed()
    {
        Assert.Equal(
            IntegrationLaneSyncAction.Diverged,
            IntegrationLanePolicy.DecideSync(new IntegrationLaneSyncState(
                "lane", "published", LaneContainsPublished: false, PublishedContainsLane: false)));
        Assert.Equal(
            "integration: branch-sync-failed",
            RemoteDeliveryParkReason.For(MergeIntoIntegrationResult.Failed(
                AcceptedIntegrationFailureCodes.BranchSyncFailed,
                "Integration branch 'develop' diverged from origin.")));
    }

    // ---- AGT-3091: the coordinator re-checks at dequeue --------------------

    [Fact]
    public async Task A_queued_review_driven_delivery_that_lost_its_admission_completes_without_integrating()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var integrated = new List<string>();
        var admissible = new HashSet<string>(StringComparer.Ordinal) { "reissued", "still-owed" };
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                lock (integrated) integrated.Add(request.JobId);
                if (request.JobId == "blocker")
                {
                    entered.TrySetResult();
                    await hold.Task;
                }
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('c', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            isAdmissibleDelivery: request =>
            {
                lock (admissible) return admissible.Contains(request.JobId);
            });

        // A batch member carries no ReviewAttemptId and is never re-checked.
        var blocker = coordinator.EnqueueAsync(Request("blocker", minute: 0, reviewAttemptId: null));
        await entered.Task.WaitAsync(Bounded);
        var reissued = coordinator.EnqueueAsync(Request("reissued", minute: 1, "review-reissued"));
        var stillOwed = coordinator.EnqueueAsync(Request("still-owed", minute: 2, "review-still-owed"));
        var batchMember = coordinator.EnqueueAsync(Request("batch-member", minute: 3, reviewAttemptId: null));

        // The card is reissued while the blocker holds the gate.
        lock (admissible) admissible.Remove("reissued");
        hold.SetResult();

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await blocker.WaitAsync(Bounded)).Outcome);
        var refused = await reissued.WaitAsync(Bounded);
        Assert.Equal(MergeIntoIntegrationOutcome.Error, refused.Outcome);
        Assert.StartsWith(RemoteDeliveryIntegrationCoordinator.ReviewedDeliverySupersededError, refused.Error);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await stillOwed.WaitAsync(Bounded)).Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await batchMember.WaitAsync(Bounded)).Outcome);
        lock (integrated) Assert.Equal(new[] { "blocker", "still-owed", "batch-member" }, integrated);
    }

    /// <summary>
    /// A recovery sweep never joins an integration another caller drives for
    /// the same card; another card, or the same card once that integration
    /// completed, is enqueued as usual.
    /// </summary>
    [Fact]
    public async Task A_sweep_does_not_join_an_integration_of_the_same_card_that_is_still_in_flight()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                if (request.JobId == "report-driven" && !hold.Task.IsCompleted)
                {
                    entered.TrySetResult();
                    await hold.Task;
                }
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('f', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);

        var reportDriven = coordinator.EnqueueAsync(Request("report-driven", minute: 0, "review-report"));
        await entered.Task.WaitAsync(Bounded);

        Assert.False(coordinator.TryEnqueueUnlessInFlight(
            Request("report-driven", minute: 0, "review-report"), discardCompletedReplay: false, out var joined));
        Assert.Null(joined);
        Assert.True(coordinator.TryEnqueueUnlessInFlight(
            Request("other-card", minute: 1, "review-other"), discardCompletedReplay: false, out var other));

        hold.SetResult();
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await reportDriven.WaitAsync(Bounded)).Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await other!.WaitAsync(Bounded)).Outcome);
        Assert.True(coordinator.TryEnqueueUnlessInFlight(
            Request("report-driven", minute: 0, "review-report"), discardCompletedReplay: true, out var replay));
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await replay!.WaitAsync(Bounded)).Outcome);
    }

    /// <summary>The order in which <c>V1ReviewPlaneEndpoints</c> normalizes a report before it settles.</summary>
    private static Contract.ReviewReportRequest NormalizeLikeTheReportEndpoint(
        Contract.ReviewReportRequest request, Contract.ReviewPlanDto? plan)
    {
        request = Contract.ReviewCommandVerdictPolicy.NormalizeReport(request, plan);
        request = Contract.ReviewVerdictCitationPolicy.NormalizeReport(
            request,
            plan?.Commands
                .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
                .Select(command => command.Aspect)
            ?? []);
        return Contract.ReviewReportDiagnosisPolicy.Normalize(request, plan);
    }

    private static Contract.ReviewPlanDto CodeQualityPlan() => new(
        [
            new Contract.ReviewCommandDto(
                "aspect-code-quality",
                "code-quality",
                CliTypes.Codex,
                [],
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                Prompt: "Review the code quality of the delivery.",
                CliType: CliTypes.Codex),
        ],
        ["code-quality"],
        LibraryVersion: Contract.ReviewLibraryStepPolicy.Version);

    private static Contract.ReviewReportRequest Report(
        string outcome, string changedPath, Contract.ReviewVerdictDto verdict)
        => new(
            "review-runner",
            "review-host:4243",
            "lease",
            1,
            "report",
            outcome,
            null,
            "Code quality blocked the delivery.",
            new Contract.ReviewWorkspaceProofDto(
                "repo", ResultSha, ResultSha, new string('b', 40), false, false, new string('c', 64), "ns",
                ChangedPaths: [changedPath]),
            new Contract.ReviewEnvironmentDto(
                "review-host", "review-runner", "review-host:4243", "linux", "x64", "10.0",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [],
            [],
            [verdict]);

    private static RemoteDeliveryIntegrationRequest Request(string jobId, int minute, string? reviewAttemptId)
        => new(
            "Fixture",
            jobId,
            Path.Combine("project-store", TaskStates.AutoReview, jobId),
            "project-store",
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            DeliveredAt.AddMinutes(minute),
            reviewAttemptId);
}
