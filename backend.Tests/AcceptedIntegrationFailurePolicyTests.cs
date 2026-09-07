using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptedIntegrationFailurePolicyTests
{
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
            false
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
    /// AGT-2749: the same code carries different classes. The 2026-09-06 reasons
    /// below parked cards as product failures although no verdict about the
    /// change existed.
    /// </summary>
    [Theory]
    [InlineData(
        "gate-failed",
        "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
        RunFailureClass.Infrastructure,
        RunFailureSignatures.GitNetworkTimeout)]
    [InlineData(
        "gate-failed",
        "dotnet test agent-taskboard.sln violated gate-run budget (limit=1800000ms, consumed=1800488ms)",
        RunFailureClass.Infrastructure,
        RunFailureSignatures.GateBudgetExceeded)]
    [InlineData(
        "error",
        "The Codex weekly quota is exhausted.",
        RunFailureClass.Quota,
        RunFailureSignatures.CliQuotaExhausted)]
    [InlineData(
        "gate-failed",
        "TaskStateMachineTests.MoveJob_RejectsUnknownLane failed: error CS0103 in the fixture",
        RunFailureClass.Product,
        RunFailureSignatures.CompilerError)]
    [InlineData(
        "conflict",
        "Merge conflict in shared.txt.",
        RunFailureClass.Unknown,
        RunFailureSignatures.Unclassified)]
    public void Classify_AttributesTheFailureToTheChangeOrToTheHost(
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
    }

    [Fact]
    public void Classify_KeepsRebaseRecoveryForAHostClassifiedConflict()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            "conflict",
            "Merge conflict in shared.txt after git operation timed out after 30 seconds.",
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(AcceptedIntegrationFailureCodes.MergeConflict, failure.Code);
        Assert.True(failure.RebaseRecoveryAvailable);
        Assert.Equal(RunFailureClass.Infrastructure, failure.FailureClass);
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
}
