using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReportReviewPolicyTests
{
    [Theory]
    [InlineData(true, "pass", false, false, ReportReviewAction.HumanReview, "Report review finished")]
    [InlineData(true, "concerns", false, false, ReportReviewAction.HumanReview, "Report review finished")]
    [InlineData(true, "block", false, false, ReportReviewAction.HumanReview, "Report review finished")]
    [InlineData(false, null, true, false, ReportReviewAction.Wait, "report review waits")]
    [InlineData(false, null, false, true, ReportReviewAction.Escalate, "could not run")]
    [InlineData(false, null, false, false, ReportReviewAction.Escalate, "produced no verdict")]
    public void Settle_routes_each_report_outcome(bool ran, string? status, bool waiting,
        bool reportMissing, ReportReviewAction expected, string phrase)
    {
        var decision = ReportReviewPolicy.Settle(new ReportReviewOutcomeFacts(
            ran, status, waiting, reportMissing, $"Verdict {status ?? "none"}."));

        Assert.Equal(expected, decision.Action);
        Assert.Contains(phrase, decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verdict", decision.Reason);
    }
}
