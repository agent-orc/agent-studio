using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteEnvironmentPreparationRetryTests
{
    [Theory]
    [InlineData(true, false, "claimed", null, false, true)]
    [InlineData(true, false, "launching", null, false, false)]
    [InlineData(true, false, "running", 42, true, false)]
    [InlineData(true, true, "claimed", null, false, false)]
    [InlineData(false, false, "claimed", null, false, false)]
    public void Only_a_durable_prelaunch_salvage_failure_uses_the_typed_release(
        bool durable, bool reattach, string phase, int? processId,
        bool processStarted, bool expected)
    {
        Assert.Equal(expected, RemoteTaskRunner.ShouldReleasePrelaunchSalvageFailure(
            durable, reattach, phase, processId,
            processStarted ? DateTime.UtcNow : null));
    }

    [Fact]
    public void Prelaunch_salvage_release_keeps_a_stable_fingerprint_when_branch_changes()
    {
        var first = new WorktreeSalvageException("/work/task", "salvage/run-1",
            new InvalidOperationException("fatal: permission denied"));
        var second = new WorktreeSalvageException("/work/task", "salvage/run-2",
            new InvalidOperationException("fatal: permission denied"));

        var one = RemoteTaskRunner.PrelaunchSalvageRelease(first, "runner-01");
        var two = RemoteTaskRunner.PrelaunchSalvageRelease(second, "runner-01");

        Assert.Equal("runner-salvage-failed", one.Outcome);
        Assert.Equal(one.Detail, two.Detail);
        Assert.Contains("fatal: permission denied", one.Detail);
        Assert.DoesNotContain("salvage/run-1", one.Detail);
    }

    [Fact]
    public async Task Clone_or_worktree_failure_retries_three_times_then_escalates()
    {
        var attempts = 0;
        var logs = new List<string>();

        var failure = await Assert.ThrowsAsync<RemoteEnvironmentPreparationException>(() =>
            RemoteTaskRunner.RetryEnvironmentPreparationAsync<string>(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("clone authentication failed");
                },
                logs.Add,
                CancellationToken.None,
                (_, _) => Task.CompletedTask));

        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, attempts);
        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, failure.Attempts);
        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, logs.Count);
        Assert.All(logs, line => Assert.Contains("remote-environment-preparation-failed", line));
    }

    [Fact]
    public async Task Successful_retry_returns_without_consuming_remaining_attempts()
    {
        var attempts = 0;

        var result = await RemoteTaskRunner.RetryEnvironmentPreparationAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("transient fetch failure");
                return Task.FromResult("develop");
            },
            _ => { },
            CancellationToken.None,
            (_, _) => Task.CompletedTask);

        Assert.Equal("develop", result);
        Assert.Equal(2, attempts);
    }
}
