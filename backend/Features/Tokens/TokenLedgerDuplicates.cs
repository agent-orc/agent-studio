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
/// Collapsing preserves the order of the surviving rows and returns the input
/// unchanged when it holds no duplicates, so fixtures without duplicates stay
/// byte-comparable with <c>orchestrator.jsonl</c>.
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
    /// Collapse identical receipt calls of one task. Remote receipts carry the
    /// run attempt in <see cref="TaskTokenCall.ParticipantId"/>
    /// (<c>agent:remote-runner:&lt;attempt&gt;</c>), so that id is the run
    /// dimension here.
    /// </summary>
    public static IReadOnlyList<TaskTokenCall> CollapseCalls(
        IReadOnlyList<TaskTokenCall> calls,
        out TokenLedgerCollapseCount collapsed)
        => Collapse(calls, call => CallFingerprint(call, includeParticipant: true), out collapsed);

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

    public static Fingerprint CallIdentityFingerprint(TaskTokenCall call)
        => CallFingerprint(call, includeParticipant: false) with
        {
            ParticipantId = UsageIdentity(call) ?? string.Empty,
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
            : ModelMetadataRegistry.NormalizeId(model)?.Trim().ToLowerInvariant() ?? string.Empty;

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
