namespace AgentStudio.Tokens;

/// <summary>
/// The per-model fold shared by the project summary and the workspace
/// aggregate. A contribution is either one priced call or one project's
/// model row; both key on the <see cref="ModelIdentity"/> id of the model
/// value they carry, so labels, aliases, and dated snapshots of one model
/// land in one row on every level.
/// </summary>
internal sealed class ModelUsageFold
{
    public const string UnknownModel = "(unknown)";

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _buckets.Count;

    public void AddCall(OrchestratorTokenUsage usage, TokenCostEstimate cost)
        => Add(usage.Model, calls: 1, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens,
            usage.CacheCreationTokens, cost.Total, cost.ModelKnown,
            inCatalog: cost.Status != TokenEconomy.PriceStatus.UnknownModel);

    public void AddRow(TokenSummaryByModel row)
        => Add(row.ModelId, row.Calls, row.InputTokens, row.OutputTokens, row.CacheReadTokens,
            row.CacheCreationTokens, row.EstimatedApiCostUsd, row.ModelPriced, row.ModelInCatalog);

    /// <summary>Rows, busiest (input + output) first.</summary>
    public IReadOnlyList<TokenSummaryByModel> Rows()
        => _buckets.Values
            .OrderByDescending(bucket => bucket.Input + bucket.Output)
            .Select(bucket => new TokenSummaryByModel(
                Model: bucket.Label,
                Calls: bucket.Calls,
                InputTokens: bucket.Input,
                OutputTokens: bucket.Output,
                CacheReadTokens: bucket.CacheRead,
                CacheCreationTokens: bucket.CacheCreation,
                EstimatedApiCostUsd: bucket.Cost,
                ModelPriced: !bucket.AnyUnpriced,
                ModelInCatalog: !bucket.AnyUnknownModel)
            {
                ModelId = bucket.ModelId,
                Cli = ModelIdentity.Resolve(bucket.ModelId)?.Cli,
            })
            .ToList();

    private void Add(string? model, int calls, long input, long output, long cacheRead, long cacheCreation,
        decimal cost, bool priced, bool inCatalog)
    {
        var identity = ModelIdentity.Resolve(model);
        var key = identity?.ModelId ?? UnknownModel;
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(key, identity?.DisplayLabel ?? UnknownModel);
            _buckets[key] = bucket;
        }
        bucket.Calls += calls;
        bucket.Input += input;
        bucket.Output += output;
        bucket.CacheRead += cacheRead;
        bucket.CacheCreation += cacheCreation;
        bucket.Cost += cost;
        if (!priced) bucket.AnyUnpriced = true;
        if (!inCatalog) bucket.AnyUnknownModel = true;
    }

    private sealed class Bucket(string modelId, string label)
    {
        public string ModelId { get; } = modelId;
        public string Label { get; } = label;
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreation;
        public decimal Cost;
        public bool AnyUnpriced;
        public bool AnyUnknownModel;
    }
}
