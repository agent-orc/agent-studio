namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Spend already charged to one cap scope. <paramref name="Tokens"/> holds the
/// measured usage of settled calls plus the requested envelope of open ones.
/// </summary>
public sealed record DecisionSpendSnapshot(int Calls, DecisionTokenUsageV1 Tokens, decimal PricedUsd)
{
    public static DecisionSpendSnapshot Empty { get; } = new(0, DecisionTokenUsageV1.Zero, 0);

    public long InputTokens => Tokens.UncachedInput + Tokens.CachedInput + Tokens.CacheWrite;
}

/// <summary>
/// AGT-W73 cost protection for decision calls. A card occurrence is charged to
/// its card: 24 calls and 5 USD priced spend. An occurrence without a card (the
/// Fluid capacity periodic host check) is charged to its host-day instead: 48
/// scheduled plus 12 event calls, 180,000 input and 90,000 output tokens and
/// 10 USD priced spend. Every call is bounded to 8,000 input and 1,000 output
/// tokens; a call whose measured usage exceeds its envelope is not accepted
/// as a decision. A null reason admits the call.
/// </summary>
public static class DecisionCostCapPolicy
{
    public const int MaxCallsPerCard = 24;
    public const decimal MaxPricedUsdPerCard = 5m;
    public const int MaxScheduledCallsPerHostDay = 48;
    public const int MaxEventCallsPerHostDay = 12;
    public const long MaxInputTokensPerHostDay = 180_000;
    public const long MaxOutputTokensPerHostDay = 90_000;
    public const decimal MaxPricedUsdPerHostDay = 10m;

    private static readonly HashSet<string> ScheduledTriggerKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "periodic",
        "scheduled",
    };

    public static bool ChargesCard(GroundsScopeV1 scope) => scope.Kind == "card";

    public static bool IsScheduled(GroundsTriggerV1 trigger) => ScheduledTriggerKinds.Contains(trigger.Kind);

    /// <summary>The host-day key of a non-card occurrence: host id (or "local") and UTC date.</summary>
    public static string HostDayKey(GroundsScopeV1 scope, DateTime at)
        => $"{(string.IsNullOrWhiteSpace(scope.HostId) ? "local" : scope.HostId.Trim())}:{at.ToUniversalTime():yyyy-MM-dd}";

    /// <summary>Requested envelope with a non-positive value meaning the per-call cap.</summary>
    public static (int Input, int Output) Envelope(int requestedInput, int requestedOutput)
        => (requestedInput > 0 ? requestedInput : DecisionGroundsPolicy.MaxCallInputTokens,
            requestedOutput > 0 ? requestedOutput : DecisionGroundsPolicy.MaxCallOutputTokens);

    public static string? CallEnvelopeReason(int input, int output)
        => input > DecisionGroundsPolicy.MaxCallInputTokens || output > DecisionGroundsPolicy.MaxCallOutputTokens
            ? "requested per-call token envelope exceeds the decision cap"
            : null;

    public static string? CardReason(DecisionSpendSnapshot card, decimal estimatedUsd = 0)
        => card.Calls >= MaxCallsPerCard ? $"card call cap of {MaxCallsPerCard} decision calls reached"
            : card.PricedUsd >= MaxPricedUsdPerCard
              || card.PricedUsd + estimatedUsd > MaxPricedUsdPerCard
                ? $"card priced decision spend cap of {MaxPricedUsdPerCard} USD reached"
                : null;

    /// <summary>
    /// Call counts are per bucket; tokens and spend are shared by both buckets.
    /// The new call's envelope must still fit, so the day never overshoots.
    /// </summary>
    public static string? HostDayReason(DecisionSpendSnapshot scheduled, DecisionSpendSnapshot events,
        bool scheduledTrigger, int input, int output, decimal estimatedUsd = 0)
    {
        if (scheduledTrigger && scheduled.Calls >= MaxScheduledCallsPerHostDay)
            return $"host-day cap of {MaxScheduledCallsPerHostDay} scheduled decision calls reached";
        if (!scheduledTrigger && events.Calls >= MaxEventCallsPerHostDay)
            return $"host-day cap of {MaxEventCallsPerHostDay} event decision calls reached";
        if (scheduled.InputTokens + events.InputTokens + input > MaxInputTokensPerHostDay)
            return $"host-day cap of {MaxInputTokensPerHostDay} decision input tokens reached";
        if (scheduled.Tokens.Output + events.Tokens.Output + output > MaxOutputTokensPerHostDay)
            return $"host-day cap of {MaxOutputTokensPerHostDay} decision output tokens reached";
        return scheduled.PricedUsd + events.PricedUsd >= MaxPricedUsdPerHostDay
            || scheduled.PricedUsd + events.PricedUsd + estimatedUsd > MaxPricedUsdPerHostDay
            ? $"host-day priced decision spend cap of {MaxPricedUsdPerHostDay} USD reached"
            : null;
    }

    /// <summary>Non-null when measured usage broke the call envelope the job was admitted with.</summary>
    public static string? MeasuredOverrunReason(DecisionTokenUsageV1 tokens, int input, int output)
    {
        var measuredInput = tokens.UncachedInput + tokens.CachedInput + tokens.CacheWrite;
        return measuredInput > input || tokens.Output > output
            ? $"call-token-cap-exceeded: measured {measuredInput} input and {tokens.Output} output tokens "
              + $"against {input} and {output}"
            : null;
    }
}
