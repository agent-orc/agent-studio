using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptedIntegrationFailurePolicyTests
{
    [Fact]
    public void ExhaustedBranchSyncRefRace_RemainsVisibleAsRefLockInfrastructureFailure()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            "error",
            "Integration branch 'develop' could not be fetched from origin: error: cannot lock ref 'refs/remotes/origin/develop': is at 2222 but expected 1111",
            null,
            AcceptedIntegrationFailureCodes.BranchSyncFailed);

        Assert.NotNull(failure);
        Assert.Equal(AcceptedIntegrationFailureCodes.BranchSyncFailed, failure.Code);
        Assert.Equal(RunFailureClass.Infrastructure, failure.FailureClass);
        Assert.Equal(RunFailureSignatures.GitRefLockRace, failure.FailureSignature);
    }

    public static TheoryData<string, string, string, bool> FailureMatrix => new()
    {
        {
            "conflict",
            "Merge conflict in one file.",
            AcceptedIntegrationFailureCodes.MergeConflict,
            true
        },
        {
            "delivery-gate-failed",
            "The Remote delivery gate rejected the reviewed result.",
            AcceptedIntegrationFailureCodes.DeliveryGateFailed,
            false
        },
        {
            "gate-failed",
            "The build gate blocked the merge.",
            AcceptedIntegrationFailureCodes.BuildGateFailed,
            false
        },
        {
            "error",
            "Release source 'origin/task' must be rebased onto 'main' before the full-suite gate.",
            AcceptedIntegrationFailureCodes.SourceNeedsRebase,
            true
        },
        {
            "agent-round-required",
            "Mechanical rebase changed the delivery commit cardinality.",
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous,
            true
        },
        {
            "error",
            "The accepted task has no stable key for review-subject validation.",
            AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable,
            false
        },
        {
            "error",
            "Review subject RunAttempt 'old' is stale; current RunAttempt is 'new'.",
            AcceptedIntegrationFailureCodes.ReviewSubjectInvalid,
            false
        },
        {
            "error",
            "Could not synchronize the integration branch.",
            AcceptedIntegrationFailureCodes.IntegrationError,
            false
        },
        {
            "no-branch",
            "No task branch to merge.",
            AcceptedIntegrationFailureCodes.NoTaskBranch,
            false
        },
        {
            "lineage-blocked",
            "Integration push blocked: main is not an ancestor of develop yet.",
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            false
        },
        {
            "push-blocked",
            "Push of the integration branch to origin was rejected (remote-rejected); the remote has diverged and needs reconciliation.",
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            false
        },
        {
            "gate-environment-failure",
            "gate environment: the build/test gate failed before verification reached test discovery.",
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            false
        },
    };

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public void Classify_MapsFailureToStableCardState(
        string verdict,
        string reason,
        string expectedCode,
        bool recoveryAvailable)
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            verdict == "no-branch" ? PipelineStepStatus.Skipped : PipelineStepStatus.Failed,
            verdict,
            reason,
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(recoveryAvailable, failure.RebaseRecoveryAvailable);
        Assert.False(string.IsNullOrWhiteSpace(failure.Label));
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }

    /// <summary>
    /// AGT-2995: every typed Error code the merge runner persists survives the
    /// round trip through the pipeline step unchanged, carries its own label,
    /// and keeps the recorded reason as the card detail.
    /// </summary>
    [Theory]
    [InlineData(AcceptedIntegrationFailureCodes.RepositoryRootUnavailable)]
    [InlineData(AcceptedIntegrationFailureCodes.StaleAttempt)]
    [InlineData(AcceptedIntegrationFailureCodes.WorktreeUnavailable)]
    [InlineData(AcceptedIntegrationFailureCodes.BranchSyncFailed)]
    [InlineData(AcceptedIntegrationFailureCodes.LineageBlocked)]
    [InlineData(AcceptedIntegrationFailureCodes.RebaseAttributionFailed)]
    [InlineData(AcceptedIntegrationFailureCodes.IntegrationRefUnresolved)]
    [InlineData(AcceptedIntegrationFailureCodes.GateUnavailable)]
    public void Classify_PersistedErrorCode_IsKeptWithItsOwnLabelAndReason(string code)
    {
        const string reason = "The exact diagnostic the runner recorded.";

        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            "error",
            reason,
            verdictSummary: null,
            persistedCode: code);

        Assert.NotNull(failure);
        Assert.Equal(code, failure.Code);
        Assert.NotEqual("Integration failed", failure.Label);
        Assert.Equal(reason, failure.Reason);
        Assert.False(failure.RebaseRecoveryAvailable);
    }

    [Theory]
    [InlineData("Release source 'origin/task' is not a fast-forward of 'main'.")]
    [InlineData("Source or target branch moved after the pre-main test run; release merge was not attempted.")]
    public void InferErrorCode_ReadsAMovedReleaseLineAsSourceNeedsRebase(string reason)
    {
        Assert.Equal(
            AcceptedIntegrationFailureCodes.SourceNeedsRebase,
            AcceptedIntegrationFailurePolicy.InferErrorCode(reason));
    }

    [Fact]
    public void FailureCodeFor_AnErrorWithoutCode_FallsBackToTheInferredCode()
    {
        var legacy = MergeIntoIntegrationResult.Of(
            MergeIntoIntegrationOutcome.Error,
            error: "Release source 'x' must be rebased onto 'main' before the full-suite gate.");
        var typed = MergeIntoIntegrationResult.Failed(
            AcceptedIntegrationFailureCodes.WorktreeUnavailable,
            "The integration worktree is unavailable.");

        Assert.Equal(AcceptedIntegrationFailureCodes.SourceNeedsRebase, AcceptedIntegrationFailureCodes.For(legacy));
        Assert.Equal(AcceptedIntegrationFailureCodes.WorktreeUnavailable, AcceptedIntegrationFailureCodes.For(typed));
        Assert.Null(AcceptedIntegrationFailureCodes.For(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged)));
        Assert.Null(AcceptedIntegrationFailureCodes.For(
            MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.PushedForReview)));
    }

    [Fact]
    public void ParkReason_ForAnError_ReadsTheCodeNotTheBareOutcome()
    {
        var failed = MergeIntoIntegrationResult.Failed(
            AcceptedIntegrationFailureCodes.SourceNeedsRebase,
            "Release source must be rebased onto 'main'.");

        Assert.Equal("integration: source-needs-rebase", RemoteDeliveryParkReason.For(failed));
        Assert.Equal(
            "automatic recovery budget used: 2/2",
            RemoteDeliveryParkReason.For(failed with { AutomaticRecoveryDetail = "automatic recovery budget used: 2/2" }));
        Assert.Null(RemoteDeliveryParkReason.For(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged)));
    }

    [Fact]
    public void Classify_PassedStep_HasNoFailure()
    {
        Assert.Null(AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Passed,
            "already-merged",
            "No merge needed.",
            verdictSummary: null));
    }

    /// <summary>
    /// AGT-2749: the 2026-09-06 overload night's gate-run budget and git-fetch
    /// timeout reasons must classify as infrastructure so the acceptance rail
    /// requeues instead of parking the card.
    /// </summary>
    [Theory]
    [InlineData(
        "gate-failed",
        "dotnet test ... violated gate-run budget (limit=1800000ms, consumed=1800488ms, phase=verification)",
        RunFailureClass.Infrastructure,
        RunFailureSignatures.GateBudgetExceeded)]
    [InlineData(
        "error",
        "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
        RunFailureClass.Infrastructure,
        RunFailureSignatures.GitNetworkTimeout)]
    [InlineData(
        "error",
        "Delivery branch 'task/agt-2713' could not be fetched: git operation timed out after 30 seconds",
        RunFailureClass.Infrastructure,
        RunFailureSignatures.GitNetworkTimeout)]
    public void Classify_AttributesTheSeptember6IncidentsToInfrastructure(
        string verdict,
        string reason,
        RunFailureClass expectedClass,
        string expectedSignature)
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            verdict,
            reason,
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(expectedClass, failure.FailureClass);
        Assert.Equal(expectedSignature, failure.FailureSignature);
        Assert.NotEqual(RunFailureClass.Product, failure.FailureClass);
    }

    [Fact]
    public void Classify_WithNoRecognizableEvidence_StaysUnknown()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            "error",
            "Something unexpected happened.",
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(RunFailureClass.Unknown, failure.FailureClass);
        Assert.Equal(RunFailureSignatures.Unclassified, failure.FailureSignature);
    }

    [Fact]
    public void Classify_TornGateNugetCache_IsInfrastructureWithAStableSignature()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            "gate-environment-failure",
            @"NuGet.targets(198,5): error : Could not find file " +
            @"'C:\Temp\agentstudio-preparation-cache\.runs\run-1\nuget\example\1.0\example.1.0.nupkg'.",
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, failure.Code);
        Assert.Equal(RunFailureClass.Infrastructure, failure.FailureClass);
        Assert.Equal(RunFailureSignatures.GatePreparationCacheTorn, failure.FailureSignature);
    }
}
