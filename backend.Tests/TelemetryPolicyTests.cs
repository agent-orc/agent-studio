using AgentStudio.Telemetry;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Direct matrix tests of the pure telemetry policies (IP-15).</summary>
public sealed class TelemetryPolicyTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private static readonly long T0 = TelemetryAdmissionPolicy.UnixNanos(Clock.GetUtcNow());
    private const long Second = MetricBucketPolicy.NanosPerSecond;
    private static readonly TelemetryOptions Options = new();

    [Fact]
    public void Gauge_and_sum_points_add_one_observation_and_keep_cumulative_state()
    {
        var prior = new CumulativeState(1, 2, 3, 4);
        var (contribution, state) = MetricBucketPolicy.Contribute(Sample(MetricKind.Gauge, MetricTemporality.Unspecified, 7.5, 1, T0), prior);

        Assert.Equal(new BucketContribution(1, 7.5, 7.5, 7.5, 7.5, T0), contribution);
        Assert.Same(prior, state);
    }

    [Fact]
    public void Delta_histogram_adds_count_and_sum_with_reported_min_and_max()
    {
        var sample = Sample(MetricKind.Histogram, MetricTemporality.Delta, 100, 4, T0) with { Min = 10, Max = 40 };

        var (contribution, state) = MetricBucketPolicy.Contribute(sample, null);

        Assert.Equal(new BucketContribution(4, 100, 10, 40, 25, T0), contribution);
        Assert.Null(state);
    }

    [Fact]
    public void Empty_delta_histogram_adds_nothing()
        => Assert.Null(MetricBucketPolicy.Contribute(Sample(MetricKind.Histogram, MetricTemporality.Delta, 0, 0, T0), null).Contribution);

    [Fact]
    public void First_cumulative_point_only_sets_the_baseline_unless_the_series_just_started()
    {
        var old = Sample(MetricKind.Histogram, MetricTemporality.Cumulative, 100, 10, T0, start: T0 - 600 * Second);
        var fresh = Sample(MetricKind.Histogram, MetricTemporality.Cumulative, 30, 3, T0, start: T0 - 30 * Second);

        var (baseline, baselineState) = MetricBucketPolicy.Contribute(old, null);
        var (whole, _) = MetricBucketPolicy.Contribute(fresh, null);

        Assert.Null(baseline);
        Assert.Equal(new CumulativeState(T0 - 600 * Second, 10, 100, T0), baselineState);
        Assert.Equal(new BucketContribution(3, 30, 10, 10, 10, T0), whole);
    }

    [Fact]
    public void Cumulative_increase_becomes_a_delta()
    {
        var prior = new CumulativeState(T0 - 600 * Second, 10, 100, T0);
        var next = Sample(MetricKind.Summary, MetricTemporality.Cumulative, 140, 14, T0 + 60 * Second, start: T0 - 600 * Second);

        var (contribution, state) = MetricBucketPolicy.Contribute(next, prior);

        Assert.Equal(new BucketContribution(4, 40, 10, 10, 10, T0 + 60 * Second), contribution);
        Assert.Equal(new CumulativeState(T0 - 600 * Second, 14, 140, T0 + 60 * Second), state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void New_start_time_or_falling_count_is_a_reset(bool newStart)
    {
        var prior = new CumulativeState(T0 - 600 * Second, 10, 100, T0);
        var next = Sample(MetricKind.Histogram, MetricTemporality.Cumulative, 30, 2, T0 + 120 * Second,
            start: newStart ? T0 + 90 * Second : T0 - 600 * Second);

        var (contribution, state) = MetricBucketPolicy.Contribute(next, prior);

        Assert.Equal(new BucketContribution(2, 30, 15, 15, 15, T0 + 120 * Second), contribution);
        Assert.Equal(2, state!.Count);
    }

    [Fact]
    public void Out_of_order_cumulative_point_is_ignored()
    {
        var prior = new CumulativeState(T0 - 600 * Second, 10, 100, T0);
        var late = Sample(MetricKind.Histogram, MetricTemporality.Cumulative, 90, 9, T0 - 60 * Second, start: T0 - 600 * Second);

        var (contribution, state) = MetricBucketPolicy.Contribute(late, prior);

        Assert.Null(contribution);
        Assert.Same(prior, state);
    }

    [Fact]
    public void Bucket_start_floors_to_the_minute_and_the_hour()
    {
        var time = T0 + 3_725 * Second + 5;
        Assert.Equal(Clock.GetUtcNow().ToUnixTimeSeconds() + 3_720, MetricBucketPolicy.BucketStart(time, MetricResolution.Minute));
        Assert.Equal(Clock.GetUtcNow().ToUnixTimeSeconds() + 3_600, MetricBucketPolicy.BucketStart(time, MetricResolution.Hour));
    }

    [Theory]
    // range hours, requested step, expected resolution, expected step
    [InlineData(1, null, MetricResolution.Minute, 60)]
    [InlineData(24, null, MetricResolution.Minute, 360)]
    [InlineData(1, 90L, MetricResolution.Minute, 120)]
    [InlineData(48, 3600L, MetricResolution.Hour, 3600)]
    [InlineData(48, 5000L, MetricResolution.Hour, 7200)]
    public void Plan_picks_resolution_and_rounds_the_step(int hours, long? step, MetricResolution resolution, long expectedStep)
    {
        var now = Clock.GetUtcNow();
        var plan = MetricBucketPolicy.Plan(now, now.AddHours(-hours), now, step, Options, out var error);

        Assert.Null(error);
        Assert.Equal(resolution, plan!.Resolution);
        Assert.Equal(expectedStep, plan.StepSeconds);
        Assert.Equal(now.ToUnixTimeSeconds(), plan.ToSeconds);
    }

    [Fact]
    public void Plan_reads_hourly_aggregates_before_the_minute_retention()
    {
        var now = Clock.GetUtcNow();
        var plan = MetricBucketPolicy.Plan(now, now.AddDays(-20), now.AddDays(-19), 60, Options, out _);

        Assert.Equal(MetricResolution.Hour, plan!.Resolution);
        Assert.Equal(3600, plan.StepSeconds);
    }

    [Theory]
    [InlineData(0, 60L, "after")]
    [InlineData(-1, 0L, "positive")]
    [InlineData(-24 * 13, 60L, "more than")]
    public void Plan_rejects_empty_ranges_bad_steps_and_too_many_points(int fromHours, long step, string message)
    {
        var now = Clock.GetUtcNow();
        Assert.Null(MetricBucketPolicy.Plan(now, now.AddHours(fromHours), now, step, Options, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void Admission_rejects_points_outside_the_retention_window_and_far_future_points()
    {
        var now = Clock.GetUtcNow();
        var points = new[]
        {
            Sample(MetricKind.Gauge, MetricTemporality.Unspecified, 1, 1, TelemetryAdmissionPolicy.UnixNanos(now.AddDays(-89))),
            Sample(MetricKind.Gauge, MetricTemporality.Unspecified, 2, 1, TelemetryAdmissionPolicy.UnixNanos(now.AddDays(-91))),
            Sample(MetricKind.Gauge, MetricTemporality.Unspecified, 3, 1, TelemetryAdmissionPolicy.UnixNanos(now.AddMinutes(9))),
            Sample(MetricKind.Gauge, MetricTemporality.Unspecified, 4, 1, TelemetryAdmissionPolicy.UnixNanos(now.AddMinutes(11))),
        };

        var admitted = TelemetryAdmissionPolicy.AdmitMetrics(new OtlpDecodeResult<MetricSample>(points, 1, "earlier"), now, Options);

        Assert.Equal([1d, 3d], admitted.Items.Select(item => item.Value));
        Assert.Equal(3, admitted.Rejected);
        Assert.Equal("earlier", admitted.RejectionMessage);
    }

    [Fact]
    public void Log_admission_uses_the_log_retention()
    {
        var now = Clock.GetUtcNow();
        TelemetryLogRecord Log(DateTimeOffset at) => new(TelemetryAdmissionPolicy.UnixNanos(at), 9, "INFO", "x", TelemetryAttributes.Empty, null, null, null);

        var admitted = TelemetryAdmissionPolicy.AdmitLogs(
            new OtlpDecodeResult<TelemetryLogRecord>([Log(now.AddDays(-6)), Log(now.AddDays(-8))], 0, null), now, Options);

        Assert.Single(admitted.Items);
        Assert.Equal(1, admitted.Rejected);
        Assert.Equal(TelemetryAdmissionPolicy.OutsideWindowMessage, admitted.RejectionMessage);
    }

    [Theory]
    [InlineData("error", 17)]
    [InlineData("WARNING", 13)]
    [InlineData("info", 9)]
    [InlineData("21", 21)]
    [InlineData("25", null)]
    [InlineData("loud", null)]
    public void Severity_names_and_numbers_parse(string text, int? expected)
        => Assert.Equal(expected, TelemetrySeverity.Parse(text));

    [Fact]
    public void Log_cursor_round_trips_and_rejects_foreign_text()
    {
        var cursor = new LogCursor(T0, 42);
        Assert.Equal(cursor, LogCursor.Decode(cursor.Encode()));
        Assert.Null(LogCursor.Decode("not a cursor"));
        Assert.Null(LogCursor.Decode(Convert.ToBase64String("1:x"u8.ToArray())));
    }

    [Fact]
    public void Query_attribute_filters_reject_keys_that_could_escape_the_json_path()
    {
        Assert.Null(TelemetryQueryParser.Attributes([KeyValuePair.Create("attr.a\"b", (string?)"x")], out var error));
        Assert.Contains("invalid key", error);
        var parsed = TelemetryQueryParser.Attributes(
            [KeyValuePair.Create("attr.http.route", (string?)"/cart"), KeyValuePair.Create("name", (string?)"ignored")], out error);
        Assert.Equal(new Dictionary<string, string> { ["http.route"] = "/cart" }, parsed);
    }

    private static MetricSample Sample(MetricKind kind, MetricTemporality temporality, double value, double count, long time, long start = 0)
        => new("m", "", kind, temporality, TelemetryAttributes.Empty, time, start, value, count, null, null);
}
