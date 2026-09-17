using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture breaker for loop-inventory entry
/// <c>completion.run-timeout-salvage-continuation</c>. A run that ends without a
/// recognized terminal outcome but transferred a salvage commit may open one
/// automatic continuation round, but a repeat in the same delivery generation
/// terminates in <c>5e-escalated</c>.
/// </summary>
public sealed class RunTimeoutContinuationBreakerTest
{
    [Fact]
    public void Budget_AllowsExactlyOneAutomaticContinuation()
    {
        Assert.Equal(1, RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds);
        Assert.Equal(
            RunTimeoutSalvageAction.StartContinuation,
            RunTimeoutSalvageContinuationPolicy.Decide(
                RunTimeoutSalvageContinuationPolicy.NonTerminalOutcome,
                hasSalvageCommit: true,
                automaticContinuationRoundsUsed: 0));
        Assert.Equal(
            RunTimeoutSalvageAction.Escalate,
            RunTimeoutSalvageContinuationPolicy.Decide(
                RunTimeoutSalvageContinuationPolicy.NonTerminalOutcome,
                hasSalvageCommit: true,
                automaticContinuationRoundsUsed: 1));
    }

    [Fact]
    public void WithoutASalvageCommit_TheLoopNeverOpens()
    {
        Assert.Equal(
            RunTimeoutSalvageAction.Escalate,
            RunTimeoutSalvageContinuationPolicy.Decide(
                RunTimeoutSalvageContinuationPolicy.NonTerminalOutcome,
                hasSalvageCommit: false,
                automaticContinuationRoundsUsed: 0));
    }

    [Fact]
    public void TerminalAgentStatements_NeverOpenThisLoop()
    {
        foreach (var outcome in new[] { "done", "noop", "blocked", "needsinput", "environmentfailure" })
        {
            Assert.Equal(
                RunTimeoutSalvageAction.None,
                RunTimeoutSalvageContinuationPolicy.Decide(
                    outcome,
                    hasSalvageCommit: true,
                    automaticContinuationRoundsUsed: 0));
        }
    }
}
