using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3002 - direct matrix tests for the contained-delivery rule, its card
/// projection, and the two completion paths that read it (the
/// accepted-integration backstop and the acceptance rail).
/// </summary>
public sealed class IntegrationVerificationPolicyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged)]
    [InlineData(MergeIntoIntegrationOutcome.MergedAfterRebase)]
    [InlineData(MergeIntoIntegrationOutcome.GateFailed)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict)]
    public void NonContainedOutcome_IsNotThisRulesDecision(MergeIntoIntegrationOutcome outcome)
    {
        var decision = Decide(outcome);

        Assert.Equal(IntegrationVerificationAction.NotApplicable, decision.Action);
    }

    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyMerged)]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch)]
    public void ContainedWithoutEvidence_RecordsUnverifiedAndRunsTheGate(MergeIntoIntegrationOutcome outcome)
    {
        var decision = Decide(outcome);

        Assert.Equal(IntegrationVerificationAction.RunGate, decision.Action);
        Assert.Equal(IntegrationVerificationStates.Unverified, decision.State);
        Assert.Contains(Sha[..7], decision.Reason);
        Assert.False(decision.GateFailed);
    }

    [Fact]
    public void ContainedWithUnresolvableSha_NeverCompletesAndBlamesNoBranch()
    {
        var decision = Decide(sha: null);

        Assert.Equal(IntegrationVerificationAction.GateUnresolved, decision.Action);
        Assert.Equal(IntegrationVerificationStates.Unverified, decision.State);
        Assert.False(decision.GateFailed);
    }

    [Theory]
    [InlineData(BuildTestGateVerdict.Ok)]
    [InlineData(BuildTestGateVerdict.NotApplicable)]
    public void GreenReceiptForTheExactTree_CompletesWithoutAGateRun(BuildTestGateVerdict verdict)
    {
        var decision = Decide(receipt: Gate(verdict));

        Assert.Equal(IntegrationVerificationAction.CompleteVerified, decision.Action);
        Assert.Equal(IntegrationVerificationStates.Verified, decision.State);
        Assert.Equal(IntegrationVerificationEvidence.GateReceipt, decision.Evidence);
    }

    [Fact]
    public void RedReceiptForTheExactTree_IsTheOnceRunVerdict()
    {
        // The observed incident: the only gate that ran on the pushed tree failed.
        var decision = Decide(receipt: Gate(BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Code));

        Assert.Equal(IntegrationVerificationAction.FailUnverified, decision.Action);
        Assert.Equal(IntegrationVerificationEvidence.GateReceipt, decision.Evidence);
        Assert.True(decision.GateFailed);
    }

    [Theory]
    [InlineData(BuildTestGateFailureKind.Environment)]
    [InlineData(BuildTestGateFailureKind.Timeout)]
    [InlineData(BuildTestGateFailureKind.MissingSource)]
    public void ReceiptWithoutATreeVerdict_DoesNotSettleTheTree(BuildTestGateFailureKind kind)
    {
        var decision = Decide(receipt: Gate(BuildTestGateVerdict.Fail, kind));

        Assert.Equal(IntegrationVerificationAction.RunGate, decision.Action);
    }

    [Fact]
    public void VerifiedIntegrationRecord_CompletesWithoutAGateRun()
    {
        var decision = Decide(verifiedRecord: true);

        Assert.Equal(IntegrationVerificationAction.CompleteVerified, decision.Action);
        Assert.Equal(IntegrationVerificationEvidence.IntegrationRecord, decision.Evidence);
    }

    [Theory]
    [InlineData(BuildTestGateVerdict.Ok)]
    [InlineData(BuildTestGateVerdict.NotApplicable)]
    public void GreenGateRun_Completes(BuildTestGateVerdict verdict)
    {
        var decision = Decide(gateRun: Gate(verdict));

        Assert.Equal(IntegrationVerificationAction.CompleteVerified, decision.Action);
        Assert.Equal(IntegrationVerificationEvidence.GateRun, decision.Evidence);
    }

    [Fact]
    public void RedGateRun_FailsUnverifiedAndOpensTheBranchCause()
    {
        var decision = Decide(gateRun: Gate(BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Code, "3 tests failed"));

        Assert.Equal(IntegrationVerificationAction.FailUnverified, decision.Action);
        Assert.Equal(IntegrationVerificationStates.Unverified, decision.State);
        Assert.Contains("3 tests failed", decision.Reason);
        Assert.True(decision.GateFailed);
    }

    [Fact]
    public void GateRunStoppedOnItsHost_IsUnresolvedNotABranchVerdict()
    {
        var decision = Decide(gateRun: Gate(BuildTestGateVerdict.Fail, BuildTestGateFailureKind.Environment));

        Assert.Equal(IntegrationVerificationAction.GateUnresolved, decision.Action);
        Assert.False(decision.GateFailed);
    }

    [Fact]
    public void Projection_LaneRecordWinsOverTheMergeStep()
    {
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Unverified,
            Sha = Sha,
            Evidence = IntegrationVerificationEvidence.GateRun,
            GateVerdict = "Fail",
            GateFailed = true,
            Reason = "red",
        };

        var projected = IntegrationVerificationProjection.Resolve(record, Step(PipelineStepStatus.Passed, "merged"), []);

        Assert.NotNull(projected);
        Assert.Equal(IntegrationVerificationStates.Unverified, projected!.State);
        Assert.Equal(Sha, projected.Sha);
        Assert.True(projected.GateFailed);
    }

    [Theory]
    [InlineData(PipelineStepStatus.Passed, "merged", false, IntegrationVerificationStates.Verified)]
    [InlineData(PipelineStepStatus.Passed, "merged-after-rebase", false, IntegrationVerificationStates.Verified)]
    [InlineData(PipelineStepStatus.Passed, "already-merged", true, IntegrationVerificationStates.Verified)]
    [InlineData(PipelineStepStatus.Passed, "already-merged", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Passed, "already-on-integration-branch", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "gate-failed", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "conflict", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "operator-override", false, null)]
    [InlineData(PipelineStepStatus.Skipped, "no-branch", false, null)]
    [InlineData(PipelineStepStatus.Pending, null, false, null)]
    public void Projection_LegacyCardsReadTheirLastMergeStep(
        PipelineStepStatus status, string? verdict, bool gateVerdict, string? expected)
    {
        var step = Step(status, verdict) with
        {
            GateVerdictSource = gateVerdict ? GateVerdictSource.Executed : null,
        };

        var projected = IntegrationVerificationProjection.Resolve(null, step, []);

        Assert.Equal(expected, projected?.State);
    }

    [Fact]
    public void Projection_WithoutStepOrRecord_IsUnknownNotUnverified()
    {
        Assert.Null(IntegrationVerificationProjection.Resolve(null, null, []));
        var verified = IntegrationVerificationProjection.Resolve(
            null,
            null,
            [new TaskIntegrationRecord { Classification = IntegrationRecordClasses.IntegratedVerified }]);
        Assert.Equal(IntegrationVerificationStates.Verified, verified?.State);
    }

    [Fact]
    public void NamesVerifiedTree_RequiresTheExactIntegrationSha()
    {
        TaskIntegrationRecord[] records =
        [
            new() { Classification = IntegrationRecordClasses.IntegratedVerified, CommitShas = [Sha] },
            new() { Classification = IntegrationRecordClasses.IntegratedHistorical, IntegrationSha = Sha },
        ];

        Assert.False(IntegrationVerificationProjection.NamesVerifiedTree(records, Sha));
        Assert.True(IntegrationVerificationProjection.NamesVerifiedTree(
            [.. records, new TaskIntegrationRecord { Classification = IntegrationRecordClasses.IntegratedVerified, IntegrationSha = Sha }],
            Sha));
    }

    [Theory]
    [InlineData(null, false, TaskStates.Completed, "Finalize")]
    [InlineData(IntegrationVerificationStates.Verified, false, TaskStates.Completed, "Finalize")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.Completed, "Retry")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.HumanReview, "Retry")]
    [InlineData(IntegrationVerificationStates.Unverified, true, TaskStates.Completed, "ReturnToReview")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.Archive, "Ignore")]
    public void Backstop_FinalizesAMergedCardOnlyWhenVerificationPermitsIt(
        string? state, bool gateFailed, string lane, string expected)
    {
        var verification = state is null
            ? null
            : new TaskIntegrationVerification { State = state, GateFailed = gateFailed };

        var decision = TaskIntegrationStatusService.ResolveMergedRecovery(
            new TaskInfo { Id = "card", State = lane },
            verification,
            Step(PipelineStepStatus.Passed, "already-merged"));

        Assert.Equal(expected, decision.Action.ToString());
    }

    [Theory]
    [InlineData(IntegrationStatuses.Integrated)]
    [InlineData(IntegrationStatuses.MergedLocally)]
    public void AcceptanceRail_DoesNotAcceptAContainedButUnverifiedDelivery(string status)
    {
        var card = new TaskInfo
        {
            Id = "rail-card",
            Key = "AGT-1",
            TaskKey = "fixture::rail-card",
            State = TaskStates.HumanReview,
            Mode = TaskModes.Coding,
            TaskType = TaskTypes.Chore,
        };
        var options = new AcceptanceRailOptions(true, TimeSpan.FromMinutes(3), 2, 3, new HashSet<string>());
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var integration = new TaskIntegrationStatus { Status = status, IntegrationBranch = "develop" };

        var unverified = AcceptanceRailPolicy.Decide(
            card,
            integration with { Verification = new TaskIntegrationVerification { State = IntegrationVerificationStates.Unverified } },
            0,
            options,
            now);
        var verified = AcceptanceRailPolicy.Decide(
            card,
            integration with { Verification = new TaskIntegrationVerification { State = IntegrationVerificationStates.Verified } },
            0,
            options,
            now);

        Assert.Equal(AcceptanceRailAction.Ignore, unverified.Action);
        Assert.Equal("integrated-unverified", unverified.Reason);
        Assert.Equal(AcceptanceRailAction.Accept, verified.Action);
    }

    [Fact]
    public void CauseCardClassification_DedupesPerBranchAcrossCardsAndVerdicts()
    {
        var first = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            "IntegrationUnverified", "GateFailed", 1, 10,
            "unverified integration branch develop", "The gate ran once on the current branch tip abc1234: 3 tests failed"));
        var second = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            "IntegrationUnverified", "GateFailed", 1, 10,
            "unverified integration branch develop", "command not found: the gate on def5678 failed differently"));
        var other = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            "IntegrationUnverified", "GateFailed", 1, 10,
            "unverified integration branch main", "3 tests failed"));

        Assert.NotNull(first);
        Assert.Equal(FailureDomains.Product, first!.Domain);
        Assert.Equal("integration/unverified-branch", first.FailureClass);
        Assert.Equal(first.Fingerprint, second!.Fingerprint);
        Assert.NotEqual(first.Fingerprint, other!.Fingerprint);
    }

    private static IntegrationVerificationDecision Decide(
        MergeIntoIntegrationOutcome outcome = MergeIntoIntegrationOutcome.AlreadyMerged,
        string? sha = Sha,
        BuildTestGateResult? receipt = null,
        bool verifiedRecord = false,
        BuildTestGateResult? gateRun = null)
        => IntegrationVerificationPolicy.Decide(new IntegrationVerificationFacts(
            outcome, sha, receipt, verifiedRecord, gateRun));

    private static BuildTestGateResult Gate(
        BuildTestGateVerdict verdict,
        BuildTestGateFailureKind kind = BuildTestGateFailureKind.None,
        string reason = "gate reason")
        => new(verdict, verdict == BuildTestGateVerdict.Ok ? 0 : 1, 10, string.Empty, reason, true, false)
        {
            ExpectedSha = Sha,
            TestedSha = Sha,
            FailureKind = kind,
        };

    private static PipelineStepExecution Step(PipelineStepStatus status, string? verdict) => new()
    {
        StepId = PipelineCatalogue.MergeIntoDevelopStepId,
        Kind = StepKind.Tool,
        Status = status,
        Verdict = verdict,
    };
}
