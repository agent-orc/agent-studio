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
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateRun,
            GateVerdict = "Fail",
            GateFailed = true,
            Reason = "red",
        };

        var projected = IntegrationVerificationProjection.Resolve(record, Step(PipelineStepStatus.Passed, "merged"), [], Sha, "develop");

        Assert.NotNull(projected);
        Assert.Equal(IntegrationVerificationStates.Unverified, projected!.State);
        Assert.Equal(Sha, projected.Sha);
        Assert.True(projected.GateFailed);
    }

    [Theory]
    [InlineData(PipelineStepStatus.Passed, "merged", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Passed, "merged-after-rebase", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Passed, "already-merged", true, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Passed, "already-merged", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Passed, "already-on-integration-branch", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "gate-failed", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "conflict", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Failed, "operator-override", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Skipped, "no-branch", false, IntegrationVerificationStates.Unverified)]
    [InlineData(PipelineStepStatus.Pending, null, false, IntegrationVerificationStates.Unverified)]
    public void Projection_LegacyCardsReadTheirLastMergeStep(
        PipelineStepStatus status, string? verdict, bool gateVerdict, string? expected)
    {
        var step = Step(status, verdict) with
        {
            GateVerdictSource = gateVerdict ? GateVerdictSource.Executed : null,
        };

        var projected = IntegrationVerificationProjection.Resolve(null, step, [], Sha, "develop");

        Assert.Equal(expected, projected?.State);
    }

    [Fact]
    public void Projection_WithoutStepOrExactTreeRecord_IsUnverified()
    {
        Assert.False(IntegrationVerificationStates.PermitsCompletion(null));
        Assert.False(IntegrationVerificationStates.PermitsCompletion(
            new TaskIntegrationVerification { State = IntegrationVerificationStates.Verified }));
        Assert.Equal(IntegrationVerificationStates.Unverified,
            IntegrationVerificationProjection.Resolve(null, null, [], Sha, "develop").State);
        var verified = IntegrationVerificationProjection.Resolve(
            null,
            null,
            [new TaskIntegrationRecord { Classification = IntegrationRecordClasses.IntegratedVerified, IntegrationSha = Sha, IntegrationBranch = "develop" }],
            Sha,
            "develop");
        Assert.Equal(IntegrationVerificationStates.Verified, verified?.State);
    }

    private const string Delivery = "dddddddddddddddddddddddddddddddddddddddd";
    private const string LaterTip = "abcdef0123456789abcdef0123456789abcdef01";

    private static IntegrationVerificationRecord VerifiedOnTreeA(params string[] deliveryShas) => new()
    {
        State = IntegrationVerificationStates.Verified,
        Sha = Sha,
        IntegrationBranch = "develop",
        Evidence = IntegrationVerificationEvidence.GateRun,
        GateVerdict = nameof(BuildTestGateVerdict.Ok),
        Reason = "The gate ran once on the current branch tip 0123456 and returned Ok.",
        DeliveryShas = [.. deliveryShas],
    };

    /// <summary>
    /// The tip advanced past the tree the gate passed on. The branch still
    /// carries that tree and the record covers this card's delivery, so the
    /// verdict stands for that exact tree; re-gating every completed card on
    /// each tip change would let an unrelated red tip reopen them.
    /// </summary>
    [Theory]
    [InlineData(Delivery)]
    [InlineData("ddddddd")]
    public void Projection_VerifiedTreeTheBranchStillCarries_StaysVerifiedForThatTree(string currentDelivery)
    {
        var projected = IntegrationVerificationProjection.Resolve(
            VerifiedOnTreeA(Delivery), null, [], LaterTip, "origin/develop", currentDelivery, sha => sha == Sha);

        Assert.Equal(IntegrationVerificationStates.Verified, projected.State);
        Assert.Equal(Sha, projected.Sha);
        Assert.Contains(LaterTip, projected.Reason);
        Assert.True(IntegrationVerificationStates.PermitsCompletion(projected));
    }

    public static TheoryData<string, IntegrationVerificationRecord, string?, bool> StaleCarryCases => new()
    {
        // The branch was rewritten: tree A is no longer in its history.
        { "rewritten", VerifiedOnTreeA(Delivery), Delivery, false },
        // A newer delivery replaced the one tree A contained.
        { "newer-delivery", VerifiedOnTreeA(Delivery), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", true },
        // A record from before DeliveryShas cannot name the delivery it covered.
        { "legacy-record", VerifiedOnTreeA(), Delivery, true },
        // No current delivery identity to compare.
        { "no-delivery", VerifiedOnTreeA(Delivery), null, true },
        // An abbreviation too short to identify a commit.
        { "short-delivery", VerifiedOnTreeA(Delivery), "dddd", true },
        // Only a verified verdict is carried; a failed one is not evidence.
        { "unverified", VerifiedOnTreeA(Delivery) with { State = IntegrationVerificationStates.Unverified, GateFailed = true }, Delivery, true },
        // Another branch's verdict is not this branch's evidence.
        { "other-branch", VerifiedOnTreeA(Delivery) with { IntegrationBranch = "main" }, Delivery, true },
    };

    [Theory]
    [MemberData(nameof(StaleCarryCases))]
    public void Projection_VerifiedRecordOnAnotherTree_IsCarriedOnlyForTheSameDeliveryOnTheSameHistory(
        string scenario, IntegrationVerificationRecord record, string? currentDelivery, bool branchCarriesTreeA)
    {
        var projected = IntegrationVerificationProjection.Resolve(
            record, null, [], LaterTip, "develop", currentDelivery, sha => branchCarriesTreeA && sha == Sha);

        Assert.True(IntegrationVerificationStates.Unverified == projected.State, scenario);
        Assert.Equal(LaterTip, projected.Sha);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(projected), scenario);
    }

    [Fact]
    public void Projection_WithoutBranchHistory_MatchesTheExactTipOnly()
    {
        var projected = IntegrationVerificationProjection.Resolve(
            VerifiedOnTreeA(Delivery), null, [], LaterTip, "develop", Delivery, branchCarries: null);

        Assert.Equal(IntegrationVerificationStates.Unverified, projected.State);
    }

    [Fact]
    public void Projection_StaleVerifiedRecordForTreeA_DoesNotCompleteTreeB()
    {
        var treeB = "abcdef0123456789abcdef0123456789abcdef01";
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = Sha,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateRun,
        };

        var projected = IntegrationVerificationProjection.Resolve(record, null, [], treeB, "develop");

        Assert.Equal(IntegrationVerificationStates.Unverified, projected.State);
        Assert.Equal(treeB, projected.Sha);
        Assert.Contains(Sha, projected.Reason);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(projected));
    }

    [Fact]
    public void Projection_StaleFileForTreeA_DoesNotMaskVerifiedIntegrationRecordForTreeB()
    {
        var treeB = "abcdef0123456789abcdef0123456789abcdef01";
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = Sha,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateRun,
        };
        var integrationRecords = new[]
        {
            new TaskIntegrationRecord
            {
                Classification = IntegrationRecordClasses.IntegratedVerified,
                IntegrationSha = treeB,
                IntegrationBranch = "develop",
            },
        };

        var projected = IntegrationVerificationProjection.Resolve(
            record, null, integrationRecords, treeB, "develop");

        Assert.Equal(IntegrationVerificationStates.Verified, projected.State);
        Assert.Equal(treeB, projected.Sha);
        Assert.Equal(IntegrationVerificationEvidence.IntegrationRecord, projected.Evidence);
        Assert.True(IntegrationVerificationStates.PermitsCompletion(projected));
    }

    [Fact]
    public void Projection_UnverifiedFileForCurrentTree_DoesNotMaskLaterVerifiedIntegrationRecord()
    {
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Unverified,
            Sha = Sha,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateRun,
            GateVerdict = "Fail",
            GateFailed = true,
            Reason = "An earlier gate failed.",
        };
        var integrationRecords = new[]
        {
            new TaskIntegrationRecord
            {
                Classification = IntegrationRecordClasses.IntegratedVerified,
                IntegrationSha = Sha,
                IntegrationBranch = "develop",
            },
        };

        var projected = IntegrationVerificationProjection.Resolve(
            record, null, integrationRecords, Sha, "develop");

        Assert.Equal(IntegrationVerificationStates.Verified, projected.State);
        Assert.Equal(Sha, projected.Sha);
        Assert.Equal(IntegrationVerificationEvidence.IntegrationRecord, projected.Evidence);
        Assert.False(projected.GateFailed);
        Assert.True(IntegrationVerificationStates.PermitsCompletion(projected));
    }

    [Fact]
    public void Projection_UnverifiedFileIsNotOverruledByAnotherBranchRecord()
    {
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Unverified,
            Sha = Sha,
            IntegrationBranch = "develop",
            GateFailed = true,
            Reason = "The gate failed.",
        };
        var integrationRecords = new[]
        {
            new TaskIntegrationRecord
            {
                Classification = IntegrationRecordClasses.IntegratedVerified,
                IntegrationSha = Sha,
                IntegrationBranch = "main",
            },
        };

        var projected = IntegrationVerificationProjection.Resolve(
            record, null, integrationRecords, Sha, "develop");

        Assert.Equal(IntegrationVerificationStates.Unverified, projected.State);
        Assert.True(projected.GateFailed);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(projected));
    }

    [Fact]
    public void Projection_PreDevelopVerificationForSameTree_DoesNotVerifyMain()
    {
        var record = new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = Sha,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateReceipt,
        };
        var integrationRecords = new[]
        {
            new TaskIntegrationRecord
            {
                Classification = IntegrationRecordClasses.IntegratedVerified,
                IntegrationSha = Sha,
                IntegrationBranch = "develop",
            },
        };

        var projected = IntegrationVerificationProjection.Resolve(
            record, null, integrationRecords, Sha, "main");

        Assert.Equal(IntegrationVerificationStates.Unverified, projected.State);
        Assert.Equal(Sha, projected.Sha);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(projected));
        Assert.False(IntegrationVerificationProjection.NamesVerifiedTree(integrationRecords, Sha, "main"));
    }

    [Theory]
    [InlineData("develop", "refs/heads/develop", true)]
    [InlineData("origin/develop", "develop", true)]
    [InlineData("develop", "main", false)]
    [InlineData(null, "develop", false)]
    public void SameBranch_NormalizesRefSpellingWithoutAcceptingAnotherBranch(
        string? recorded, string current, bool expected)
    {
        Assert.Equal(expected, IntegrationVerificationProjection.SameBranch(recorded, current));
    }

    [Fact]
    public void NamesVerifiedTree_RequiresTheExactIntegrationSha()
    {
        TaskIntegrationRecord[] records =
        [
            new() { Classification = IntegrationRecordClasses.IntegratedVerified, CommitShas = [Sha] },
            new() { Classification = IntegrationRecordClasses.IntegratedHistorical, IntegrationSha = Sha },
        ];

        Assert.False(IntegrationVerificationProjection.NamesVerifiedTree(records, Sha, "develop"));
        Assert.True(IntegrationVerificationProjection.NamesVerifiedTree(
            [.. records, new TaskIntegrationRecord { Classification = IntegrationRecordClasses.IntegratedVerified, IntegrationSha = Sha, IntegrationBranch = "develop" }],
            Sha,
            "develop"));
    }

    [Theory]
    [InlineData(null, false, TaskStates.Completed, "Retry")]
    [InlineData(IntegrationVerificationStates.Verified, false, TaskStates.Completed, "Finalize")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.Completed, "Retry")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.HumanReview, "Retry")]
    [InlineData(IntegrationVerificationStates.Unverified, true, TaskStates.Completed, "ReturnToReview")]
    [InlineData(IntegrationVerificationStates.Unverified, false, TaskStates.Archive, "Ignore")]
    // An archived card is never reopened, even when a gate failed on its tree.
    [InlineData(IntegrationVerificationStates.Unverified, true, TaskStates.Archive, "Ignore")]
    [InlineData(null, false, TaskStates.Archive, "Ignore")]
    [InlineData(IntegrationVerificationStates.Verified, false, TaskStates.Archive, "Finalize")]
    public void Backstop_FinalizesAMergedCardOnlyWhenVerificationPermitsIt(
        string? state, bool gateFailed, string lane, string expected)
    {
        var verification = state is null
            ? null
            : new TaskIntegrationVerification { State = state, Sha = Sha, GateFailed = gateFailed };

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
        var now = DateTimeOffset.UnixEpoch;
        var integration = new TaskIntegrationStatus { Status = status, IntegrationBranch = "develop" };

        var unverified = AcceptanceRailPolicy.Decide(
            card,
            integration with { Verification = new TaskIntegrationVerification { State = IntegrationVerificationStates.Unverified } },
            0,
            options,
            now);
        var unknown = AcceptanceRailPolicy.Decide(card, integration, 0, options, now);
        var verified = AcceptanceRailPolicy.Decide(
            card,
            integration with { Verification = new TaskIntegrationVerification { State = IntegrationVerificationStates.Verified, Sha = Sha } },
            0,
            options,
            now);

        Assert.Equal(AcceptanceRailAction.Ignore, unverified.Action);
        Assert.Equal(AcceptanceRailAction.Ignore, unknown.Action);
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
