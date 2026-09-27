using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Builds a durable chat receipt from provider usage and measured execution times.</summary>
public static class ChatTurnMetadata
{
    public static ChatTurnMetadataDto Create(
        string? cliType, string? model, string? effort, string? sessionId,
        string? host, DateTime? queuedAt, DateTime? startedAt, DateTime? finishedAt,
        OrchestratorTokenUsage? usage, long? reasoningTokens = null)
    {
        decimal? cost = null;
        string? currency = null;
        string? version = null;
        if (usage is not null)
        {
            version = $"TokenEconomy/{typeof(TokenEconomy.ModelPriceCatalog).Assembly.GetName().Version?.ToString() ?? "unknown"}";
            try
            {
                var estimate = TokenPricing.Estimate(usage.Model ?? model,
                    usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens,
                    finishedAt);
                if (estimate.ModelKnown)
                {
                    cost = estimate.Total;
                    currency = estimate.PriceBasis?.Currency;
                }
            }
            catch (Exception ex) { SilentCatch.Note(ex, "ChatTurnMetadata: pricing unavailable"); }
        }
        return new ChatTurnMetadataDto(cliType, usage?.Model ?? model, effort,
            sessionId, host, queuedAt, startedAt, finishedAt,
            usage?.InputTokens, usage?.CacheReadTokens, usage?.OutputTokens,
            reasoningTokens, usage?.CacheCreationTokens, cost, currency, version);
    }
}
