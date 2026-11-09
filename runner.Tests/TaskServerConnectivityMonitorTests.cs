using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class TaskServerConnectivityMonitorTests
{
    [Fact]
    public void Day_long_outage_logs_transitions_and_hourly_summaries_instead_of_every_poll()
    {
        var logs = new List<string>();
        var monitor = new TaskServerConnectivityMonitor(logs.Add);
        var started = new DateTime(2026, 8, 1, 15, 30, 0, DateTimeKind.Utc);

        for (var attempt = 0; attempt < 24 * 60 * 2; attempt++)
        {
            monitor.RecordFailure(
                started.AddSeconds(attempt * 30),
                "review claim poll",
                new HttpRequestException("connection refused"),
                TimeSpan.FromSeconds(30),
                activeSlots: 0);
        }

        Assert.Equal(25, logs.Count);
        Assert.Single(logs, line => line.Contains("status=unreachable", StringComparison.Ordinal));
        Assert.Single(logs, line => line.Contains("status=escalated", StringComparison.Ordinal));
        Assert.Equal(23, logs.Count(line => line.Contains("status=still-unreachable", StringComparison.Ordinal)));
        Assert.Equal(TaskServerConnectivityStates.Unreachable, monitor.Snapshot.Status);
        Assert.NotNull(monitor.Snapshot.EscalatedAt);
        Assert.Equal(24 * 60 * 2, monitor.Snapshot.ConsecutiveFailures);
    }

    [Fact]
    public void Recovery_clears_the_acute_state_and_is_logged_once()
    {
        var logs = new List<string>();
        var monitor = new TaskServerConnectivityMonitor(logs.Add);
        var started = new DateTime(2026, 8, 1, 15, 30, 0, DateTimeKind.Utc);
        monitor.RecordFailure(
            started,
            "review claim poll",
            new HttpRequestException("connection refused"),
            TimeSpan.FromSeconds(5),
            activeSlots: 2);

        Assert.True(monitor.RecordSuccess(started.AddMinutes(7), "review claim poll"));
        Assert.False(monitor.RecordSuccess(started.AddMinutes(8), "review claim poll"));

        Assert.Equal(2, logs.Count);
        Assert.Contains("status=recovered", logs[1], StringComparison.Ordinal);
        Assert.Equal(TaskServerConnectivityStates.Reachable, monitor.Snapshot.Status);
        Assert.Equal(0, monitor.Snapshot.ConsecutiveFailures);
        Assert.Null(monitor.Snapshot.FailureStartedAt);
        Assert.Equal(started.AddMinutes(7), monitor.Snapshot.LastRecoveredAt);
    }

    [Fact]
    public void Failure_keeps_status_and_request_as_separate_fields()
    {
        var monitor = new TaskServerConnectivityMonitor(_ => { });
        var now = new DateTime(2026, 8, 1, 15, 30, 0, DateTimeKind.Utc);
        monitor.RecordFailure(now, "claim poll", new TaskServerException(524, "origin timeout"),
            TimeSpan.FromSeconds(5), 0);
        Assert.Equal(524, monitor.Snapshot.HttpStatus);
        Assert.Equal("POST /api/runner/claim", monitor.Snapshot.Request);
        Assert.Equal(now, monitor.Snapshot.FailureStartedAt);
        monitor.RecordSuccess(now.AddSeconds(1), "claim poll");
        Assert.Null(monitor.Snapshot.HttpStatus);
        Assert.Null(monitor.Snapshot.Request);
    }

    [Theory]
    [InlineData(5, 1, 5)]
    [InlineData(5, 6, 30)]
    [InlineData(30, 6, 60)]
    [InlineData(0, 1, 1)]
    public void Retry_delay_is_bounded(int pollSeconds, int attempt, int expectedSeconds)
        => Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            TaskServerConnectivityMonitor.RetryDelay(pollSeconds, attempt));
}
