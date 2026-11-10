namespace AgentRunner;

/// <summary>Recognizes CLI refusals that happen before an aspect model can grade a delivery.</summary>
internal static class ReviewCliRefusalPolicy
{
    internal const string InputTooLarge = "ReviewInputTooLarge";
    internal const string ArgumentRejected = "ReviewCliArgumentRejected";
    internal const string TurnRefused = "ReviewCliTurnRefused";

    internal static string? Classify(AgentStudio.TaskServer.Contracts.ReviewCommandDto command,
        ProcessResult process)
    {
        if (!AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind)
            || process.Success) return null;
        var output = process.StdOut + "\n" + process.StdErr;
        if (output.Contains("input_too_large", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Input exceeds the maximum length", StringComparison.OrdinalIgnoreCase))
            return InputTooLarge;
        if (output.Contains("turn/start failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("turn refused", StringComparison.OrdinalIgnoreCase)
            || output.Contains("turn rejected", StringComparison.OrdinalIgnoreCase))
            return TurnRefused;
        if (output.Contains("unknown argument", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase)
            || output.Contains("invalid argument", StringComparison.OrdinalIgnoreCase)
            || output.Contains("error: argument", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            || output.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
            || output.Contains("requires a value", StringComparison.OrdinalIgnoreCase))
            return ArgumentRejected;
        return null;
    }
}
