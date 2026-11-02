using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3409 pure decisions for the workspace repository: how long a
/// <c>git add</c> may run, and when a commit refused on a lock is tried again.
/// </summary>
public sealed class WorkspaceArtifactLockPolicyTests
{
    [Theory]
    [InlineData(0L, 30)]
    [InlineData(2L * 1024 * 1024, 31)]
    [InlineData(41_600_575L, 49.8)]
    [InlineData(1024L * 1024 * 1024, 542)]
    [InlineData(5L * 1024 * 1024 * 1024, 600)]
    [InlineData(-1L, 30)]
    public void Add_timeout_grows_with_the_bytes_to_hash_up_to_the_cap(long bytes, double expectedSeconds)
    {
        var timeout = WorkspaceAddTimeoutPolicy.For(
            bytes,
            WorkspaceAddTimeoutPolicy.DefaultBase,
            WorkspaceAddTimeoutPolicy.DefaultBytesPerSecond,
            WorkspaceAddTimeoutPolicy.DefaultMaximum);

        Assert.Equal(expectedSeconds, timeout.TotalSeconds, precision: 1);
    }

    [Fact]
    public void Add_timeout_for_an_unknown_size_is_the_cap_and_the_cap_never_undercuts_the_base()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), WorkspaceAddTimeoutPolicy.For(
            null, TimeSpan.FromSeconds(30), 1024, TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromSeconds(30), WorkspaceAddTimeoutPolicy.For(
            10L * 1024 * 1024, TimeSpan.FromSeconds(30), 1024, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(1, 7, 5)]
    [InlineData(2, 7, 15)]
    [InlineData(3, 7, 30)]
    [InlineData(4, 7, 60)]
    [InlineData(5, 7, 120)]
    [InlineData(6, 7, 300)]
    [InlineData(7, 7, 600)]
    [InlineData(9, 10, 600)]
    public void Lock_retry_waits_longer_each_round(int round, int maxRounds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ArtifactCommitLockRetryPolicy.DelayBefore(round, maxRounds));

    [Theory]
    [InlineData(0, 7)]
    [InlineData(8, 7)]
    [InlineData(1, 0)]
    public void Lock_retry_stops_when_the_rounds_are_spent(int round, int maxRounds)
        => Assert.Null(ArtifactCommitLockRetryPolicy.DelayBefore(round, maxRounds));

    [Fact]
    public void Lock_retry_schedule_outlasts_the_stale_lock_threshold()
    {
        var total = Enumerable.Range(1, ArtifactCommitLockRetryPolicy.DefaultRounds)
            .Select(round => ArtifactCommitLockRetryPolicy.DelayBefore(round, ArtifactCommitLockRetryPolicy.DefaultRounds)!.Value)
            .Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);

        Assert.True(total > GitStaleLockPolicy.DefaultThreshold);
    }

    [Theory]
    [InlineData("git-add: fatal: Unable to create '/srv/ws/.git/index.lock': File exists.", true)]
    [InlineData("git-commit: fatal: Unable to create '/srv/ws/.git/HEAD.lock': File exists.", true)]
    [InlineData("git-add: Another git process seems to be running in this repository", true)]
    [InlineData("git-add: git operation timed out after 30 seconds", false)]
    [InlineData("git-commit: fatal: bad object HEAD", false)]
    public void Only_a_lock_refusal_is_retried(string error, bool expected)
    {
        Assert.Equal(expected, ArtifactCommitLockRetryPolicy.IsLockFailure(WorkspaceArtifactCommitResult.Failed("x", error)));
        Assert.False(ArtifactCommitLockRetryPolicy.IsLockFailure(WorkspaceArtifactCommitResult.Skipped(error)));
    }
}
