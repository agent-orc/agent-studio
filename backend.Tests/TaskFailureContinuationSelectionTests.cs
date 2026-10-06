using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskFailureContinuationSelectionTests
{
    [Theory]
    [InlineData("started", true)]
    [InlineData("queued", true)]
    [InlineData("saved", false)]
    public void ContinuationDisposition_OnlyRunnableAnswersCountAsStarted(string status, bool started)
    {
        var result = new TaskFailureContinuationResult(202, null, status, "delivery-pending", "AGT-3023",
            "lane-not-runnable");

        Assert.True(result.Accepted);
        Assert.Equal(started, result.Started);
    }

    [Fact]
    public void CurrentFailureStep_DoesNotReviveOldFailureAfterDeliveryIsIntegrated()
    {
        var failed = Failure(DateTime.UtcNow);

        var selected = TaskFailureContinuationEndpoints.CurrentFailureStep(
            [failed], new TaskIntegrationStatus { Status = IntegrationStatuses.Integrated }, null);

        Assert.Null(selected);
    }

    [Fact]
    public void CurrentFailureStep_RejectsFailureBeforeCurrentReviewSubject()
    {
        var completedAt = DateTime.UtcNow;
        var oldFailure = Failure(completedAt.AddMinutes(-2));
        var subject = new ReviewSubjectRecord { CompletedAtUtc = completedAt.AddMinutes(-1) };

        var selected = TaskFailureContinuationEndpoints.CurrentFailureStep(
            [oldFailure], new TaskIntegrationStatus { Status = IntegrationStatuses.Pending }, subject);

        Assert.Null(selected);
    }

    [Fact]
    public void CurrentFailureStep_KeepsFailureOfCurrentDelivery()
    {
        var completedAt = DateTime.UtcNow;
        var currentFailure = Failure(completedAt.AddMinutes(1));
        var subject = new ReviewSubjectRecord { CompletedAtUtc = completedAt };

        var selected = TaskFailureContinuationEndpoints.CurrentFailureStep(
            [Failure(completedAt.AddMinutes(-1)), currentFailure],
            new TaskIntegrationStatus { Status = IntegrationStatuses.Pending }, subject);

        Assert.Same(currentFailure, selected);
    }

    private static PipelineStepExecution Failure(DateTime completedAt) => new()
    {
        StepId = "review-aspects",
        Status = PipelineStepStatus.Failed,
        CompletedAt = completedAt,
        Verdict = "block",
    };
}
