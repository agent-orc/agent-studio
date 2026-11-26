namespace AgentStudio.Tokens;

/// <summary>
/// Pure duplicate policy for the token ledger (AGT-3012). One recorded usage
/// is identified by its run, job, timestamp, canonical model and the four
/// token counts. New remote receipt participants also carry a per-session or
/// per-turn usage identity, allowing equal-sized turns in one millisecond to
/// remain distinct. Historical rows without that identity use the best
/// available fingerprint; readers keep the first occurrence.
/// </summary>
/// <remarks>
/// Bus collapse preserves input order and returns the input unchanged when
/// there are no duplicates. Receipt calls preserve input order; within a duplicate group the
/// earliest recorded attribution wins when a source identity was replayed.
/// </remarks>
internal static class TokenLedgerDuplicates
{
    /// <summary>
    /// Collapse identical bus messages. The run id and participant are part of
    /// the identity, so two runs that happen to report the same counts at the
    /// same instant stay separate.
    /// </summary>
    public static IReadOnlyList<AgentMessage> CollapseMessages(
        IReadOnlyList<AgentMessage> messages,
        out TokenLedgerCollapseCount collapsed)
        => Collapse(messages, message => message.Tokens is { } tokens
            ? new Fingerprint(
                message.JobId ?? string.Empty,
                message.RunId ?? string.Empty,
                message.ParticipantId ?? string.Empty,
                message.CreatedAt.ToUniversalTime().Ticks,
                CanonicalModel(tokens.Model),
                tokens.Input,
                tokens.Output,
                tokens.CacheRead ?? 0,
                tokens.CacheWrite ?? 0)
            : null, out collapsed);

    /// <summary>
    /// Collapse receipt calls of one task. Source-identified remote usage can
    /// be replayed under a different attempt, inferred date or fallback model.
    /// Legacy calls without a source identity retain the full run fingerprint.
    /// </summary>
    public static IReadOnlyList<TaskTokenCall> CollapseCalls(
        IReadOnlyList<TaskTokenCall> calls,
        out TokenLedgerCollapseCount collapsed)
    {
        collapsed = TokenLedgerCollapseCount.None;
        var winners = new Dictionary<Fingerprint, int>();
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            var key = IsRemoteUsage(call)
                ? CallIdentityFingerprint(call) : CallFingerprint(call, includeParticipant: true);
            if (!winners.TryGetValue(key, out var previous))
            {
                winners.Add(key, index);
                continue;
            }
            collapsed = collapsed.Add(key.TotalTokens);
            if (call.Ts < calls[previous].Ts) winners[key] = index;
        }
        if (collapsed.Entries == 0) return calls;
        var keptIndices = winners.Values.ToHashSet();
        return calls.Where((_, index) => keptIndices.Contains(index)).ToList();
    }

    /// <summary>
    /// Fingerprint of one receipt call. <paramref name="includeParticipant"/>
    /// false yields the usage-only identity the receipt writer uses to detect
    /// the same usage re-reported under a later run attempt.
    /// </summary>
    public static Fingerprint CallFingerprint(TaskTokenCall call, bool includeParticipant)
        => new(
            string.Empty,
            string.Empty,
            includeParticipant ? call.ParticipantId ?? string.Empty : string.Empty,
            call.Ts.ToUniversalTime().Ticks,
            CanonicalModel(call.Model),
            call.InputTokens,
            call.OutputTokens,
            call.CacheReadTokens,
            call.CacheCreationTokens);

    public static string? UsageIdentity(TaskTokenCall call)
    {
        const string marker = ":usage:";
        var participant = call.ParticipantId;
        var index = participant?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        return index < 0 ? null : participant![(index + marker.Length)..];
    }

    private static bool IsRemoteUsage(TaskTokenCall call)
        => call.ParticipantId?.StartsWith(TokenUsageHost.RemoteRunnerParticipantPrefix, StringComparison.Ordinal) == true
           && UsageIdentity(call) is not null;

    private static bool IsRemoteTurn(TaskTokenCall call)
        => call.ParticipantId?.StartsWith(TokenUsageHost.RemoteRunnerParticipantPrefix, StringComparison.Ordinal) == true
           && UsageIdentity(call)?.StartsWith("turn:", StringComparison.Ordinal) == true;

    public static Fingerprint CallIdentityFingerprint(TaskTokenCall call)
        => CallFingerprint(call, includeParticipant: false) with
        {
            ParticipantId = UsageIdentity(call) ?? string.Empty,
            // A source turn is the same log position even when a later import
            // assigned the file's new date and the card's new fallback model.
            // Retain time of day and all counters to avoid merging rotated logs
            // merely because their line positions coincide.
            TimestampTicks = IsRemoteTurn(call) ? call.Ts.TimeOfDay.Ticks
                : IsRemoteUsage(call) ? 0 : call.Ts.ToUniversalTime().Ticks,
            Model = IsRemoteTurn(call) ? string.Empty : CanonicalModel(call.Model),
        };

    public static long TotalTokens(TaskTokenCall call)
        => call.InputTokens + call.OutputTokens + call.CacheReadTokens + call.CacheCreationTokens;

    public static long TotalTokens(AgentMessage message)
        => message.Tokens is { } tokens
            ? tokens.Input + tokens.Output + (tokens.CacheRead ?? 0) + (tokens.CacheWrite ?? 0)
            : 0;

    private static IReadOnlyList<T> Collapse<T>(
        IReadOnlyList<T> items,
        Func<T, Fingerprint?> fingerprint,
        out TokenLedgerCollapseCount collapsed)
    {
        collapsed = TokenLedgerCollapseCount.None;
        var seen = new HashSet<Fingerprint>();
        List<T>? kept = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (fingerprint(item) is not { } key || seen.Add(key))
            {
                kept?.Add(item);
                continue;
            }

            // First duplicate: materialize the prefix that survived so far.
            kept ??= items.Take(index).ToList();
            collapsed = collapsed.Add(key.TotalTokens);
        }
        return kept ?? items;
    }

    private static string CanonicalModel(string? model)
        => string.IsNullOrWhiteSpace(model)
            ? string.Empty
            : ModelIdentity.RegistryId(model).Trim().ToLowerInvariant();

    internal readonly record struct Fingerprint(
        string JobId,
        string RunId,
        string ParticipantId,
        long TimestampTicks,
        string Model,
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite)
    {
        public long TotalTokens => Input + Output + CacheRead + CacheWrite;
    }
}

/// <summary>How many ledger rows and tokens a collapse removed.</summary>
public readonly record struct TokenLedgerCollapseCount(int Entries, long Tokens)
{
    public static TokenLedgerCollapseCount None => default;

    public TokenLedgerCollapseCount Add(long tokens) => new(Entries + 1, Tokens + tokens);

    public TokenLedgerCollapseCount Add(TokenLedgerCollapseCount other)
        => new(Entries + other.Entries, Tokens + other.Tokens);
}
