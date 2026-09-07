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

    [Fact]
    public void Classify_PassedStep_HasNoFailure()
    {
        Assert.Null(AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Passed,
            "already-merged",
            "No merge needed.",
            verdictSummary: null));
    }

    [Theory]
    [InlineData(PipelineStepStatus.Skipped)]
    [InlineData(PipelineStepStatus.Failed)]
    public void Classify_GateEnvironmentHold_IsNeverACardFailure(PipelineStepStatus status)
    {
        // AGT-2720: a gate that never reached the first test judged nothing, so no
        // status and no persisted code may turn it into a typed card failure.
        Assert.Null(AcceptedIntegrationFailurePolicy.Classify(
            status,
            IntegrationStepVerdicts.GateEnvironment,
            "The pre-main gate environment failed before the first test.",
            verdictSummary: null,
            persistedCode: AcceptedIntegrationFailureCodes.BuildGateFailed));
        Assert.True(AcceptedIntegrationFailurePolicy.IsGateEnvironmentHold(
            IntegrationStepVerdicts.GateEnvironment));
        Assert.False(AcceptedIntegrationFailurePolicy.IsGateEnvironmentHold("gate-failed"));
    }
}
