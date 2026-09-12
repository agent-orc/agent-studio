using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchReviewPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-12T14:00:00Z");

    [Theory]
    [InlineData("2026-06-14T14:00:00Z", true)]
    [InlineData("2026-06-15T14:00:01Z", false)]
    public void IsDue_AppliesConfiguredAgeThreshold(string reviewedAt, bool expected)
    {
        Assert.Equal(expected, WorkbenchReviewPolicy.IsDue(
            DateTimeOffset.Parse(reviewedAt), Now, 90, []));
    }

    [Fact]
    public void IsDue_WhenEveryRelatedCardReachedCompletedAfterReview()
    {
        var reviewedAt = DateTimeOffset.Parse("2026-09-10T14:00:00Z");
        var completedAfter = new WorkbenchReviewRelatedCard(
            true, TaskStates.Completed, DateTime.Parse("2026-09-11T14:00:00Z").ToUniversalTime());
        var completedBefore = completedAfter with
        {
            EnteredLaneAtUtc = DateTime.Parse("2026-09-09T14:00:00Z").ToUniversalTime(),
        };

        Assert.True(WorkbenchReviewPolicy.IsDue(reviewedAt, Now, 90, [completedAfter]));
        Assert.False(WorkbenchReviewPolicy.IsDue(reviewedAt, Now, 90, [completedAfter, completedBefore]));
        Assert.False(WorkbenchReviewPolicy.IsDue(reviewedAt, Now, 90,
            [completedAfter with { State = TaskStates.HumanReview }]));
    }
}
