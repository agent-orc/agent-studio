namespace AgentStudio.Runner;

/// <summary>Builds the durable chat receipt from provider usage and the existing price seam.</summary>
public static class ChatTurnMetadataFactory
{
    public static ChatTurnMetadata Create(
        OrchestratorDecisionResult result,
        RemoteChatWorkResult? remote,
        DateTime queuedAt,
        DateTime startedAt,
        DateTime finishedAt,
        string? effort)
    {
        var usage = result.TokenUsage;
        var model = usage?.Model ?? result.Model;
        var estimate = usage is null ? null : TokenPricing.Estimate(
            model, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, usage.CacheCreationTokens, finishedAt);
        var version = typeof(TokenEconomy.ModelPriceCatalog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            ?? typeof(TokenEconomy.ModelPriceCatalog).Assembly.GetName().Version?.ToString();

        return new ChatTurnMetadata
        {
            ProviderSessionId = remote?.ProviderSessionId ?? result.CapturedSessionId,
            Host = remote?.ExecutionContext?.HostName ?? Environment.MachineName,
            Model = model,
            Effort = usage?.ThinkingLevel ?? effort,
            CliType = result.CliType ?? CliTypes.Codex,
            QueuedAt = remote?.QueuedAt ?? queuedAt,
            StartedAt = remote?.StartedAt ?? startedAt,
            FinishedAt = remote?.FinishedAt ?? finishedAt,
            InputTokens = usage?.InputTokens,
            CachedInputTokens = usage?.CacheReadTokens,
            OutputTokens = usage?.OutputTokens,
            ReasoningTokens = usage?.ReasoningTokens,
            Cost = estimate?.ModelKnown == true ? estimate.Total : null,
            Currency = estimate?.ModelKnown == true ? estimate.PriceBasis?.Currency : null,
            PriceCatalogueVersion = estimate?.ModelKnown == true ? $"TokenEconomy {version}" : null,
        };
    }
}
