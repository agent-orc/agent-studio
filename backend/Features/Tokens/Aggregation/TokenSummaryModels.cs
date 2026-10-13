namespace AgentStudio.Tokens;

/// <summary>
/// Per-project token and cost rollup. Token amounts are recorded counts;
/// <see cref="EstimatedApiCostUsd"/> is a theoretical list-price comparison,
/// not a bill. Subscription quota lives at <c>/api/cli/quota</c>.
/// </summary>
public sealed record TokenSummary(
    string Project,
    int OrchestratorEntries,
    int OrchestratorLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    int UnknownModelCount,
    IReadOnlyList<TokenSummaryByModel> ByModel,
    string? FirstActivity,
    string? LastActivity,
    string Disclaimer);

/// <summary>
/// One model row of a project summary or the workspace aggregate.
/// <see cref="Model"/> is the display label; <see cref="ModelId"/> and
/// <see cref="Cli"/> come from <see cref="ModelIdentity"/>. Rows read from a
/// cache written before those fields existed resolve them from the label.
/// </summary>
public sealed record TokenSummaryByModel(
    string Model,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool ModelPriced,
    bool ModelInCatalog)
{
    public string ModelId { get; init; } = ModelIdentity.Resolve(Model)?.ModelId ?? Model;
    public string? Cli { get; init; } = ModelIdentity.Resolve(Model)?.Cli;
}

/// <summary>Workspace-wide rollup across every watched project.</summary>
public sealed record TokenSummaryAggregate(
    int Projects,
    int OrchestratorEntries,
    int OrchestratorLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    IReadOnlyList<TokenSummaryByModel> ByModel,
    IReadOnlyList<TokenSummaryByProject> ByProject,
    string FetchedAt,
    string? FirstActivity,
    string? LastActivity,
    string Disclaimer);

public sealed record TokenSummaryByProject(
    string Project,
    int OrchestratorLlmCalls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd);
