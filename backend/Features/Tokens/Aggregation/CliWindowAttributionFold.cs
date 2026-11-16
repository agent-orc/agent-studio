namespace AgentStudio.Tokens;

/// <summary>Stored CLI receipts inside one provider quota interval.</summary>
public sealed record CliWindowAttribution(int Calls, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheCreationTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

public static class CliWindowAttributionFold
{
    public static CliWindowAttribution Sum(IEnumerable<OrchestratorLogEntry> entries, string cliType,
        DateTime fromUtc, DateTime toUtc)
    {
        var calls = 0;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        foreach (var entry in entries)
        {
            var usage = entry.TokenUsage;
            var at = entry.Ts.ToUniversalTime();
            if (usage is null || at < fromUtc || at >= toUtc
                || !string.Equals(usage.CliType, cliType, StringComparison.OrdinalIgnoreCase)) continue;
            calls++;
            input += usage.InputTokens;
            output += usage.OutputTokens;
            cacheRead += usage.CacheReadTokens;
            cacheWrite += usage.CacheCreationTokens;
        }
        return new CliWindowAttribution(calls, input, output, cacheRead, cacheWrite);
    }
}
