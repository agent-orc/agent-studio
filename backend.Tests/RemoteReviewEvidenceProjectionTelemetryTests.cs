using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteReviewEvidenceProjectionTelemetryTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Summarize_EmptyWindow_ReturnsZeroRateAndNullMedian()
    {
        var summary = RemoteReviewEvidenceProjectionTelemetry.Summarize([], Now, TimeSpan.FromMinutes(30));

        Assert.Equal(0, summary.DrainRatePerMinute);
        Assert.Null(summary.MedianDurationMs);
        Assert.Equal(0, summary.SampleCount);
    }

    [Fact]
    public void Summarize_ExcludesSamplesOutsideTheTrailingWindow()
    {
        var samples = new[]
        {
            Sample(Now.AddMinutes(-45), 1000, succeeded: true),
            Sample(Now.AddMinutes(-10), 2000, succeeded: true),
            Sample(Now.AddMinutes(-5), 3000, succeeded: true),
        };

        var summary = RemoteReviewEvidenceProjectionTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(30));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2500, summary.MedianDurationMs);
    }

    [Fact]
    public void Summarize_FailedProjectionsCountTowardDurationAndDrainRate()
    {
        // Unlike the auto-review queue (where a deferral re-enters the
        // queue), a failed projection here is re-enqueued by the worker
        // itself as a new sample later - this sample already left the
        // in-flight slot, so it counts as drained.
        var samples = new[]
        {
            Sample(Now.AddMinutes(-5), 1000, succeeded: true),
            Sample(Now.AddMinutes(-5), 3000, succeeded: false),
        };

        var summary = RemoteReviewEvidenceProjectionTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2000, summary.MedianDurationMs);
        Assert.Equal(0.2, summary.DrainRatePerMinute, precision: 3);
    }

    [Fact]
    public void Summarize_MedianOfEvenCountAveragesTheTwoMiddleValues()
    {
        var samples = new[] { 4000.0, 1000.0, 2000.0, 3000.0 }
            .Select(ms => Sample(Now.AddMinutes(-1), ms, succeeded: true))
            .ToArray();

        var summary = RemoteReviewEvidenceProjectionTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2500, summary.MedianDurationMs);
    }

    [Fact]
    public void RecordCompletion_EvictsOldestSampleBeyondCapacity()
    {
        var telemetry = new RemoteReviewEvidenceProjectionTelemetry(capacity: 2);
        telemetry.RecordCompletion(Now.AddMinutes(-3), TimeSpan.FromSeconds(1), succeeded: true);
        telemetry.RecordCompletion(Now.AddMinutes(-2), TimeSpan.FromSeconds(2), succeeded: true);
        telemetry.RecordCompletion(Now.AddMinutes(-1), TimeSpan.FromSeconds(3), succeeded: true);

        var summary = telemetry.Summarize(Now, TimeSpan.FromMinutes(30));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2500, summary.MedianDurationMs);
    }

    private static RemoteReviewEvidenceProjectionSample Sample(DateTime completedAtUtc, double elapsedMs, bool succeeded)
        => new(completedAtUtc, elapsedMs, succeeded);
}
