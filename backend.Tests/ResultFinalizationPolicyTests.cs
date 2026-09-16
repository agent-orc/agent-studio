using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2850 acceptance, decided without any process, clock, or filesystem: a
/// degraded or deferred Result summary must never look like a result that did
/// not arrive. Every branch acknowledges; only the summary side differs.
/// </summary>
public sealed class ResultFinalizationPolicyTests
{
    [Theory]
    [InlineData(false, false, ResultSummaryAdmission.NotRequested)]
    [InlineData(false, true, ResultSummaryAdmission.NotRequested)]
    [InlineData(true, false, ResultSummaryAdmission.Attempt)]
    [InlineData(true, true, ResultSummaryAdmission.Defer)]
    public void Admission_matrix_defers_only_while_the_host_is_throttled(
        bool finalizeRequested,
        bool hostThrottled,
        ResultSummaryAdmission expected)
        => Assert.Equal(
            expected,
            ResultFinalizationPolicy.Admit(finalizeRequested, hostThrottled));

    [Fact]
    public void An_artifacts_only_upload_finalizes_nothing_and_owes_nothing()
    {
        var plan = ResultFinalizationPolicy.Plan(ResultSummaryDelivery.NotRequested, null);

        Assert.False(plan.Generated);
        Assert.False(plan.CommitStatusDocument);
        Assert.False(plan.ScheduleRetry);
        Assert.Null(plan.ResultDocumentStatus);
        Assert.Null(plan.TimelineSummary);
    }

    [Fact]
    public void A_generated_summary_travels_with_the_acknowledgement_and_owes_no_retry()
    {
        var plan = ResultFinalizationPolicy.Plan(ResultSummaryDelivery.Generated, null);

        Assert.True(plan.Generated);
        Assert.True(plan.CommitStatusDocument);
        Assert.False(plan.ScheduleRetry);
        Assert.Equal("generated", plan.ResultDocumentStatus);
        Assert.Null(plan.TimelineSummary);
    }

    [Fact]
    public void A_throttled_host_reads_as_summary_pending_on_the_card_timeline()
    {
        var plan = ResultFinalizationPolicy.Plan(
            ResultSummaryDelivery.Pending,
            ResultSummaryPendingReasons.LoadThrottle);

        Assert.False(plan.Generated);
        Assert.False(plan.CommitStatusDocument);
        Assert.True(plan.ScheduleRetry);
        Assert.Equal("pending:load-throttle", plan.ResultDocumentStatus);
        Assert.Equal("Summary pending (load throttle)", plan.TimelineSummary);
    }

    [Fact]
    public void A_summary_still_running_past_the_budget_is_pending_not_missing()
    {
        var plan = ResultFinalizationPolicy.Plan(ResultSummaryDelivery.Pending, null);

        Assert.Equal("pending:generation-in-flight", plan.ResultDocumentStatus);
        Assert.Equal("Summary pending (generation in flight)", plan.TimelineSummary);
        Assert.True(plan.ScheduleRetry);
    }

    [Fact]
    public void A_degraded_summary_keeps_its_error_and_still_schedules_a_retry()
    {
        var plan = ResultFinalizationPolicy.Plan(
            ResultSummaryDelivery.Degraded,
            "A task was canceled.");

        Assert.False(plan.Generated);
        Assert.False(plan.CommitStatusDocument);
        Assert.True(plan.ScheduleRetry);
        Assert.Equal("degraded:A task was canceled.", plan.ResultDocumentStatus);
        Assert.Equal("Summary pending (degraded: A task was canceled.)", plan.TimelineSummary);
    }

    [Fact]
    public void A_degraded_summary_without_an_error_names_the_spent_budget()
        => Assert.Equal(
            "degraded:summary retry budget exhausted",
            ResultFinalizationPolicy.Plan(ResultSummaryDelivery.Degraded, "   ").ResultDocumentStatus);

    [Fact]
    public void An_unbounded_provider_error_is_trimmed_before_it_reaches_the_wire()
    {
        var plan = ResultFinalizationPolicy.Plan(
            ResultSummaryDelivery.Degraded,
            new string('x', ResultFinalizationPolicy.MaxReasonChars + 500));

        Assert.Equal(
            "degraded:" + new string('x', ResultFinalizationPolicy.MaxReasonChars),
            plan.ResultDocumentStatus);
    }
}
