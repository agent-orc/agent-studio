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
}
