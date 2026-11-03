using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationPushBackstopPolicyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(PipelineStepStatus.Passed, null, true, (int)PendingPushRecoveryDisposition.Push)]
    [InlineData(PipelineStepStatus.Passed, PipelineStepStatus.Pending, true, (int)PendingPushRecoveryDisposition.Push)]
    [InlineData(PipelineStepStatus.Passed, PipelineStepStatus.Passed, true, (int)PendingPushRecoveryDisposition.Push)]
    [InlineData(PipelineStepStatus.Passed, PipelineStepStatus.Skipped, true, (int)PendingPushRecoveryDisposition.Push)]
    [InlineData(PipelineStepStatus.Passed, PipelineStepStatus.Failed, true, (int)PendingPushRecoveryDisposition.Clear)]
    [InlineData(PipelineStepStatus.Failed, null, true, (int)PendingPushRecoveryDisposition.Ignore)]
    [InlineData(PipelineStepStatus.Passed, null, false, (int)PendingPushRecoveryDisposition.Ignore)]
    public void Recovery_requires_exact_approved_merge_and_rechecks_origin_after_prior_success(
        PipelineStepStatus mergeStatus, PipelineStepStatus? pushStatus,
        bool matchingSha, int expected)
    {
        var marker = new PendingIntegrationPush("AGT-1", Sha, null, null,
            "develop", null, DateTimeOffset.UnixEpoch);
        var merge = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Status = mergeStatus,
            ApprovedIntegrationSha = matchingSha ? Sha : new string('b', 40),
            CompletedAt = DateTime.UnixEpoch.AddMinutes(5),
        };
        var push = pushStatus is null ? null : new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Status = pushStatus.Value,
            StartedAt = DateTime.UnixEpoch.AddMinutes(6),
        };

        Assert.Equal((PendingPushRecoveryDisposition)expected,
            IntegrationPushBackstopHostedService.DecideMarker(marker, merge, push));
    }

    [Fact]
    public void Older_failed_push_does_not_cancel_a_new_approved_merge()
    {
        var marker = new PendingIntegrationPush("AGT-1", Sha, null, null,
            "develop", null, DateTimeOffset.UnixEpoch);
        var merge = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Status = PipelineStepStatus.Passed,
            ApprovedIntegrationSha = Sha,
            CompletedAt = DateTime.UnixEpoch.AddMinutes(5),
        };
        var oldPush = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Status = PipelineStepStatus.Failed,
            StartedAt = DateTime.UnixEpoch.AddMinutes(1),
        };

        Assert.Equal(PendingPushRecoveryDisposition.Push,
            IntegrationPushBackstopHostedService.DecideMarker(marker, merge, oldPush));
    }
}
