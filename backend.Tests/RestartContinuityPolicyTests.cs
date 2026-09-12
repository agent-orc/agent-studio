using AgentStudio.Cli;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RestartContinuityPolicyTests
{
    [Fact]
    public void Lost_worker_is_terminal_without_reissue_budget_or_automatic_retry()
    {
        var terminal = RestartContinuityPolicy.Evaluate([
            new CliOutputLine
            {
                Stream = "system",
                Text = "[taskboard] [run-lost-across-restart] worker generation absent",
            },
        ]);

        Assert.True(terminal.LostAcrossRestart);
        Assert.False(terminal.ChargeReissueBudget);
        Assert.False(terminal.AllowAutomaticRetry);
    }
}
