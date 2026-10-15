namespace AgentStudio.OrchestrationDecisions;

public sealed record CardRunLedgerResult(CardRunLedgerV1 Ledger, ProgressDefinitionV1 Progress);

public sealed record DecisionUsageTotals(
    DecisionTokenUsageV1 Tokens, IReadOnlyList<string> ProviderCallIds, int ExcludedDuplicateFrames);

/// <summary>
/// AGT-W68 RB-S1 as AGT-W73 decision grounds: one deduplicated per-card run
/// ledger and the progress definition. Usage is deduplicated by provider call
/// id before aggregation; rows without one use the run, timestamp, model and
/// counter fingerprint. A raw OpenAI input total is reduced by its cached
/// subset before pricing, and an unknown price stays unpriced instead of zero.
/// </summary>
public static class CardRunLedgerPolicy
{
    public const string ProgressVersion = "rb-s1.v1";
    public const string ProgressDefinition =
        "A round made progress when its result tree differs from its start tree, "
        + "or when its result tree has a passing verification that the previous round's tree did not have.";

    public static DecisionTokenUsageV1 Normalize(long input, long cachedInput, long cacheWrite,
        long output, bool inputIncludesCached)
    {
        input = Math.Max(0, input);
        cachedInput = Math.Max(0, cachedInput);
        return new(inputIncludesCached ? Math.Max(0, input - cachedInput) : input,
            cachedInput, Math.Max(0, cacheWrite), Math.Max(0, output));
    }

    public static DecisionTokenUsageV1 Normalize(RunLedgerRowV1 row)
        => Normalize(row.InputTokens, row.CachedInputTokens, row.CacheWriteTokens,
            row.OutputTokens, row.InputIncludesCached);

    public static string Identity(RunLedgerRowV1 row)
        => !string.IsNullOrWhiteSpace(row.ProviderCallId)
            ? "call:" + row.ProviderCallId.Trim()
            : string.Join(':', "fp", row.RunId ?? string.Empty, row.At.ToUniversalTime().Ticks,
                (row.Model ?? string.Empty).Trim().ToLowerInvariant(), row.InputTokens,
                row.CachedInputTokens, row.CacheWriteTokens, row.OutputTokens);

    /// <param name="price">Host pricing seam, called with the already normalized usage.</param>
    /// <param name="verifiedTrees">Result tree SHAs with a passing verification, oldest first.</param>
    public static CardRunLedgerResult Build(
        IReadOnlyList<RunLedgerRowV1> rows,
        IReadOnlyList<RunLedgerRoundInputV1> rounds,
        IReadOnlyList<string> verifiedTrees,
        Func<RunLedgerRowV1, DecisionTokenUsageV1, DecisionPriceV1> price)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = DecisionTokenUsageV1.Zero;
        var duplicateTokens = DecisionTokenUsageV1.Zero;
        decimal priced = 0, duplicateUsd = 0;
        int counted = 0, duplicates = 0, unpriced = 0;
        long unpricedTokens = 0;
        var unpricedModels = new SortedSet<string>(StringComparer.Ordinal);
        var unpricedStatuses = new HashSet<string>(StringComparer.Ordinal);
        var perRound = new Dictionary<string, (DecisionTokenUsageV1 Tokens, decimal Usd, int Unpriced)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(item => item.At).ThenBy(Identity, StringComparer.Ordinal))
        {
            var usage = Normalize(row);
            var cost = price(row, usage);
            if (!seen.Add(Identity(row)))
            {
                duplicates++;
                duplicateTokens = duplicateTokens.Add(usage);
                if (cost.Status == DecisionPriceStatuses.Priced) duplicateUsd += cost.EstimatedUsd ?? 0;
                continue;
            }
            counted++;
            tokens = tokens.Add(usage);
            var known = cost.Status == DecisionPriceStatuses.Priced && cost.EstimatedUsd is not null;
            if (known) priced += cost.EstimatedUsd!.Value;
            else
            {
                unpriced++;
                unpricedTokens += usage.Total;
                unpricedModels.Add(string.IsNullOrWhiteSpace(row.Model) ? "unknown" : row.Model.Trim());
                unpricedStatuses.Add(cost.Status);
            }
            if (row.RunId is { Length: > 0 } runId)
            {
                var current = perRound.GetValueOrDefault(runId, (Tokens: DecisionTokenUsageV1.Zero, Usd: 0m, Unpriced: 0));
                perRound[runId] = (current.Tokens.Add(usage),
                    current.Usd + (known ? cost.EstimatedUsd!.Value : 0), current.Unpriced + (known ? 0 : 1));
            }
        }

        var status = counted == 0 ? DecisionPriceStatuses.NoUsage
            : unpriced == 0 ? DecisionPriceStatuses.Priced
            : unpriced < counted ? DecisionPriceStatuses.Partial
            : unpricedStatuses.Contains(DecisionPriceStatuses.UnknownModel) ? DecisionPriceStatuses.UnknownModel
            : DecisionPriceStatuses.NoPriceForDate;
        var quality = new List<string>();
        if (counted == 0) quality.Add("no-usage");
        if (duplicates > 0) quality.Add("duplicates-excluded");
        if (unpriced > 0) quality.Add("unpriced-rows");
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.ProviderCallId))) quality.Add("fingerprint-identity");

        var verified = verifiedTrees.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = rounds.OrderBy(round => round.StartedAt).ThenBy(round => round.Fence).ToArray();
        var built = new List<RunLedgerRoundV1>(ordered.Length);
        string? previousResult = null;
        foreach (var round in ordered)
        {
            var (progress, reason) = RoundProgress(round, previousResult, verified);
            var usage = perRound.GetValueOrDefault(round.AttemptId, (Tokens: DecisionTokenUsageV1.Zero, Usd: 0m, Unpriced: 0));
            built.Add(new(round.AttemptId, round.Fence, round.StartedAt, round.EndedAt, round.Outcome,
                round.BaseSha, round.ResultSha, progress, reason, usage.Tokens, usage.Usd, usage.Unpriced));
            previousResult = round.ResultSha ?? previousResult;
        }

        var lastAccepted = Array.FindLastIndex(ordered, round => round.AcceptedDelivery);
        var delivery = ordered.Skip(lastAccepted + 1).ToArray();
        var withoutProgress = built.AsEnumerable().Reverse().TakeWhile(round => !round.Progress).Count();
        var ledger = new CardRunLedgerV1(rows.Count, counted, duplicates, duplicateTokens, duplicateUsd,
            tokens, priced, unpriced, unpricedTokens, unpricedModels.ToArray(), status,
            quality.Count == 0 ? "clean" : string.Join(',', quality), built);
        var progressDefinition = new ProgressDefinitionV1(ProgressVersion, ProgressDefinition,
            verifiedTrees.Count == 0 ? null : verifiedTrees[^1], delivery.Length, withoutProgress,
            delivery.Count(round => round.Exhaustion));
        return new(ledger, progressDefinition);
    }

    /// <summary>
    /// Totals for one decision call. Frames with the same provider call id are
    /// one call however often the runner reports them.
    /// </summary>
    public static DecisionUsageTotals Deduplicate(IReadOnlyList<DecisionUsageFrameV1> frames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        var tokens = DecisionTokenUsageV1.Zero;
        var excluded = 0;
        foreach (var frame in frames)
        {
            if (!string.IsNullOrWhiteSpace(frame.ProviderCallId))
            {
                if (!seen.Add(frame.ProviderCallId.Trim()))
                {
                    excluded++;
                    continue;
                }
                ids.Add(frame.ProviderCallId.Trim());
            }
            tokens = tokens.Add(Normalize(frame.InputTokens, frame.CachedInputTokens,
                frame.CacheWriteTokens, frame.OutputTokens, frame.InputIncludesCached));
        }
        return new(tokens, ids, excluded);
    }

    private static (bool Progress, string Reason) RoundProgress(RunLedgerRoundInputV1 round,
        string? previousResult, IReadOnlySet<string> verified)
    {
        if (string.IsNullOrWhiteSpace(round.ResultSha)) return (false, "no-result-tree");
        if (!string.IsNullOrWhiteSpace(round.BaseSha)
            && !string.Equals(round.BaseSha, round.ResultSha, StringComparison.OrdinalIgnoreCase))
            return (true, "result-tree-differs-from-start");
        var previouslyVerified = string.Equals(previousResult, round.ResultSha, StringComparison.OrdinalIgnoreCase);
        return verified.Contains(round.ResultSha) && !previouslyVerified
            ? (true, "newly-verified-tree")
            : (false, "unchanged-tree-without-new-verification");
    }
}
