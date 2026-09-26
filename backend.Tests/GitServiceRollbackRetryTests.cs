using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

public sealed class GitServiceRollbackRetryTests
{
    [Fact]
    public void TimedOutRollback_RetriesExactlyOnceWithLongerBudget()
    {
        var budgets = new List<TimeSpan>();
        var warnings = 0;
        var start = new ProcessStartInfo("git");

        var result = GitService.RunRollbackGitWithRetry(
            start,
            (actualStart, timeout) =>
            {
                Assert.Same(start, actualStart);
                budgets.Add(timeout);
                return budgets.Count == 1
                    ? new GitProcessResult(-1, "", "timeout", GitProcessFailureKind.TimedOut)
                    : new GitProcessResult(0, "ok", "", GitProcessFailureKind.None);
            },
            () => warnings++);

        Assert.Equal(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120) }, budgets);
        Assert.Equal(1, warnings);
        Assert.True(result.Success);
    }

    [Fact]
    public void FailedSecondRollback_DoesNotRetryAgain()
    {
        var calls = 0;

        var result = GitService.RunRollbackGitWithRetry(
            new ProcessStartInfo("git"),
            (_, _) =>
            {
                calls++;
                return new GitProcessResult(-1, "", "timeout", GitProcessFailureKind.TimedOut);
            },
            () => { });

        Assert.Equal(2, calls);
        Assert.Equal(GitProcessFailureKind.TimedOut, result.FailureKind);
    }
}
