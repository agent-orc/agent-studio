using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// AGT-2993: a compose scenario review step builds about 2.6 GB of images plus
/// BuildKit cache before it runs anything. On 2026-09-28 the scenario and smoke
/// residue filled agent-runner-01 to 95-96 % twice. A review step started on an
/// almost full disk fails inside the build and reads like a product failure,
/// or fills the disk for every other slot on the host. The review executor
/// therefore logs the free space before such a step and refuses to start it
/// below the configured floor, as a typed infrastructure outcome instead.
/// </summary>
internal static class ComposeScenarioDiskAdmission
{
    /// <summary>Review infrastructure classification for a refused compose scenario.</summary>
    internal const string DiskLowClassification = "ComposeScenarioDiskLow";

    /// <summary>Default floor: refuse below 10 % free.</summary>
    internal const int DefaultMinFreePercent = 10;

    /// <summary>
    /// True when a deterministic review command starts a Docker Compose
    /// scenario or smoke: <c>scripts/scenario.sh --target compose</c> (any
    /// level) or <c>scripts/compose-smoke-test.sh</c>, also when wrapped in a
    /// shell <c>-c</c> string.
    /// </summary>
    internal static bool IsComposeScenario(ReviewCommandDto command)
    {
        if (ReviewCommandKinds.IsAgent(command.ExecutionKind))
            return false;
        var tokens = new[] { command.FileName }
            .Concat(command.Arguments)
            .SelectMany(part => part.Split(
                [' ', '\t', '\r', '\n', ';', '&', '|', '(', ')', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        if (tokens.Any(token => IsScript(token, "compose-smoke-test.sh")))
            return true;
        if (!tokens.Any(token => IsScript(token, "scenario.sh")))
            return false;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (string.Equals(tokens[index], "--target=compose", StringComparison.Ordinal))
                return true;
            if (string.Equals(tokens[index], "--target", StringComparison.Ordinal)
                && index + 1 < tokens.Length
                && string.Equals(tokens[index + 1], "compose", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pure admission decision for one compose scenario step. A floor of 0
    /// disables the refusal but keeps the log line; an unreadable disk is
    /// admitted, because the probe failing says nothing about the free space.
    /// </summary>
    internal static ComposeScenarioDiskDecision Decide(
        string path,
        long? freeBytes,
        long? totalBytes,
        int minFreePercent)
    {
        var floor = Math.Clamp(minFreePercent, 0, 100);
        if (freeBytes is not { } free || totalBytes is not { } total || total <= 0 || free < 0)
            return new ComposeScenarioDiskDecision(true, path, null, null, null, floor);
        var percent = Math.Round(100d * free / total, 1);
        var admit = floor == 0 || 100d * free / total >= floor;
        return new ComposeScenarioDiskDecision(admit, path, free, total, percent, floor);
    }

    private static bool IsScript(string token, string name)
        => string.Equals(token, name, StringComparison.Ordinal)
           || token.EndsWith("/" + name, StringComparison.Ordinal)
           || token.EndsWith("\\" + name, StringComparison.Ordinal);
}

internal sealed record ComposeScenarioDiskDecision(
    bool Admit,
    string Path,
    long? FreeBytes,
    long? TotalBytes,
    double? FreePercent,
    int MinFreePercent)
{
    /// <summary>Journal line written before every compose scenario step.</summary>
    public string Describe(string stepId)
        => $"review-compose-scenario-disk step={stepId} path={Path} " +
           $"freeBytes={FreeBytes?.ToString() ?? "unknown"} totalBytes={TotalBytes?.ToString() ?? "unknown"} " +
           $"freePercent={FreePercent?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
           $"minFreePercent={MinFreePercent} decision={(Admit ? "admit" : "refuse")}";

    /// <summary>Operator-facing reason recorded with the refused review attempt.</summary>
    public string RefusalSummary(string stepId, string commandLine)
        => $"Review command '{stepId}' is a compose scenario and was not started: {Path} has " +
           $"{FreePercent?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} % free " +
           $"({FreeBytes} of {TotalBytes} bytes), below the {MinFreePercent} % floor. " +
           "A compose scenario builds about 2.6 GB of images and build cache; free Docker space " +
           "(scripts/docker-scenario-retention.sh) and the review is retried. Nothing about the " +
           $"reviewed change was evaluated: {commandLine}.";
}
