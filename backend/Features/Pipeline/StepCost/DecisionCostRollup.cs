namespace AgentStudio.Pipeline;

/// <summary>
/// One side of the "what did deciding cost" comparison. <see cref="PricedCostUsd"/>
/// sums only priced executions; <see cref="UnpricedTokens"/> and
/// <see cref="UnpricedRuns"/> state the share whose model has no catalogue
/// price, so a reader never mistakes an unpriced amount for zero.
/// </summary>
public sealed record DecisionCostBucket(
    long Tokens,
    decimal PricedCostUsd,
    long UnpricedTokens,
    int Runs,
    int UnpricedRuns);

/// <summary>
/// Cost of deciding against the cost of the agent runs, for one card or one
/// project. <see cref="Deciding"/> folds every orchestrator-kind step (the
/// decisions between steps); <see cref="AgentRuns"/> the core agent run;
/// <see cref="Other"/> everything else (aspect reviews, drift, analysis,
/// deterministic tools). Measurement only.
/// </summary>
public sealed record DecisionCostRollup(
    DecisionCostBucket Deciding,
    DecisionCostBucket AgentRuns,
    DecisionCostBucket Other)
{
    public static DecisionCostRollup Empty { get; } = new DecisionCostAccumulator().Build();
}

/// <summary>Mutable builder for <see cref="DecisionCostRollup"/>; not thread-safe.</summary>
public sealed class DecisionCostAccumulator
{
    private readonly Bucket _deciding = new();
    private readonly Bucket _agentRuns = new();
    private readonly Bucket _other = new();

    /// <summary>Count one execution of a step and add its tokens and price.</summary>
    public void AddExecution(StepKind kind, long tokens, TokenCostEstimate? estimate)
    {
        var bucket = BucketFor(kind);
        bucket.Runs++;
        AddTokens(bucket, tokens, estimate);
    }

    /// <summary>Add tokens that belong to an already counted execution (ledger calls).</summary>
    public void AddTokens(StepKind kind, long tokens, TokenCostEstimate? estimate)
        => AddTokens(BucketFor(kind), tokens, estimate);

    /// <summary>Add priced ledger tokens whose amount was recorded at call time.</summary>
    public void AddPricedTokens(StepKind kind, long tokens, decimal costUsd)
    {
        var bucket = BucketFor(kind);
        bucket.Tokens += tokens;
        bucket.Cost += costUsd;
    }

    public DecisionCostRollup Build() => new(_deciding.Build(), _agentRuns.Build(), _other.Build());

    /// <summary>Which side of the comparison a step kind belongs to.</summary>
    public static string SideOf(StepKind kind) => kind switch
    {
        StepKind.Orchestrator => "deciding",
        StepKind.Core => "agent-runs",
        _ => "other",
    };

    private Bucket BucketFor(StepKind kind) => kind switch
    {
        StepKind.Orchestrator => _deciding,
        StepKind.Core => _agentRuns,
        _ => _other,
    };

    private static void AddTokens(Bucket bucket, long tokens, TokenCostEstimate? estimate)
    {
        if (tokens <= 0) return;
        bucket.Tokens += tokens;
        if (estimate is { ModelKnown: true })
        {
            bucket.Cost += estimate.Total;
        }
        else
        {
            bucket.UnpricedTokens += tokens;
            bucket.UnpricedRuns++;
        }
    }

    private sealed class Bucket
    {
        public long Tokens;
        public decimal Cost;
        public long UnpricedTokens;
        public int Runs;
        public int UnpricedRuns;

        public DecisionCostBucket Build() => new(
            Tokens,
            Math.Round(Cost, 6, MidpointRounding.AwayFromZero),
            UnpricedTokens,
            Runs,
            UnpricedRuns);
    }
}
