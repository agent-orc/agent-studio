using AgentStudio.Cli;

namespace AgentStudio.Runner;

internal readonly record struct RestartContinuityTerminal(
    bool LostAcrossRestart,
    bool ChargeReissueBudget,
    bool AllowAutomaticRetry);

internal static class RestartContinuityPolicy
{
    public static RestartContinuityTerminal Evaluate(IEnumerable<CliOutputLine> output)
    {
        var lost = output.Any(line =>
            line.Text.Contains("[run-lost-across-restart]", StringComparison.Ordinal));
        return lost
            ? new RestartContinuityTerminal(true, false, false)
            : new RestartContinuityTerminal(false, true, true);
    }
}
