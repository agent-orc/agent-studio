namespace AgentStudio.Runner;

/// <summary>Studio's wire adapter for the chat library's turn metadata contract.</summary>
public sealed record ChatTurnMetadata
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? ProviderThreadId { get; init; }
    public string? Host { get; init; }
    public DateTime? QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public decimal? Cost { get; init; }
    public string? Currency { get; init; }
    public string? PriceCatalogueVersion { get; init; }
    public ChatTurnMetadataCapabilities Capabilities { get; init; } = new();

    public static ChatTurnMetadata Create(
        string? model, string? effort, string? providerThreadId, string? host,
        DateTime? queuedAt, DateTime? startedAt, DateTime? finishedAt,
        OrchestratorTokenUsage? usage, string? cliType)
    {
        var priced = usage is null ? null : TokenPricing.Estimate(
            usage.Model ?? model, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, usage.CacheCreationTokens, finishedAt);
        return new ChatTurnMetadata
        {
            Model = usage?.Model ?? model,
            Effort = effort,
            ProviderThreadId = providerThreadId,
            Host = host,
            QueuedAt = queuedAt,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            InputTokens = usage?.InputTokens,
            CachedInputTokens = usage?.CacheReadTokens,
            OutputTokens = usage?.OutputTokens,
            ReasoningTokens = usage?.ReasoningTokens,
            Cost = priced?.ModelKnown == true ? priced.Total : null,
            Currency = priced?.ModelKnown == true ? priced.PriceBasis?.Currency : null,
            PriceCatalogueVersion = priced?.ModelKnown == true
                ? $"TokenEconomy/{typeof(TokenEconomy.ModelPriceCatalog).Assembly.GetName().Version}"
                : null,
            Capabilities = new ChatTurnMetadataCapabilities(
                Tokens: usage is not null,
                Cost: priced?.ModelKnown == true,
                ReasoningTokens: usage?.ReasoningTokens is not null,
                QueueDuration: queuedAt.HasValue && startedAt.HasValue,
                RunDuration: startedAt.HasValue && finishedAt.HasValue,
                ProviderThreadId: !string.IsNullOrWhiteSpace(providerThreadId)),
        };
    }
}

public sealed record ChatTurnMetadataCapabilities(
    bool Tokens = false,
    bool Cost = false,
    bool ReasoningTokens = false,
    bool QueueDuration = false,
    bool RunDuration = false,
    bool ProviderThreadId = false);
