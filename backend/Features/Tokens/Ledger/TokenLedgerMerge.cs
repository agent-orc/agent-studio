namespace AgentStudio.Tokens;

/// <summary>
/// Merges a later ledger source into an earlier one, counting a call that
/// both sources hold once. Two entries are the same call when job, instant,
/// and the four token counters match; each earlier entry absorbs at most one
/// later twin. The result is ordered by timestamp.
/// </summary>
public static class TokenLedgerMerge
{
    public static IReadOnlyList<OrchestratorLogEntry> WithoutDuplicates(
        IReadOnlyList<OrchestratorLogEntry> historical,
        IReadOnlyList<OrchestratorLogEntry> receipts)
    {
        var remainingHistoricalKeys = new Dictionary<TokenEntryKey, int>();
        foreach (var entry in historical)
        {
            if (entry.TokenUsage is null) continue;
            var key = TokenEntryKey.From(entry);
            remainingHistoricalKeys.TryGetValue(key, out var count);
            remainingHistoricalKeys[key] = count + 1;
        }

        var merged = new List<OrchestratorLogEntry>(historical.Count + receipts.Count);
        merged.AddRange(historical);
        foreach (var receipt in receipts)
        {
            if (receipt.TokenUsage is not null)
            {
                var key = TokenEntryKey.From(receipt);
                if (remainingHistoricalKeys.TryGetValue(key, out var count) && count > 0)
                {
                    remainingHistoricalKeys[key] = count - 1;
                    continue;
                }
            }
            merged.Add(receipt);
        }
        return merged.OrderBy(entry => entry.Ts).ToList();
    }

    private readonly record struct TokenEntryKey(
        string JobId,
        long TimestampTicks,
        int Input,
        int Output,
        int CacheRead,
        int CacheCreation)
    {
        public static TokenEntryKey From(OrchestratorLogEntry entry)
        {
            var usage = entry.TokenUsage!;
            return new TokenEntryKey(
                entry.JobId ?? string.Empty,
                entry.Ts.ToUniversalTime().Ticks,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheCreationTokens);
        }
    }
}
