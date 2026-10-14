namespace AgentStudio.Tokens;

/// <summary>
/// Registers the token usage and pricing area, stage by stage. The bus
/// store, bus aggregation cache, and job metadata cache it reads from are
/// owned by their own features.
/// </summary>
public static class TokenUsageServiceCollectionExtensions
{
    public static IServiceCollection AddTokenUsage(this IServiceCollection services)
    {
        // Read: the merged project ledger and the ad-hoc bus source.
        services.AddSingleton<ProjectTokenReceiptReader>();
        services.AddSingleton<ProjectTokenLedger>();
        services.AddSingleton<BusAdHocUsageReader>();

        // Price: one service owns all provider calls and per-entry timestamps.
        services.AddSingleton<ITokenPriceProvider, TokenEconomyPriceProvider>();
        services.AddSingleton<ITokenPricing, TokenPricingService>();
        services.AddSingleton<TokenPricingDriftMonitor>();

        // Project: aggregation facade, response caches, and report services.
        services.AddSingleton<TokenSummaryCacheStore>();
        services.AddSingleton<WorkspaceTokensCacheStore>();
        services.AddSingleton<ITokenAggregator, TokenAggregationService>();
        services.AddSingleton<BetterCandidateUsageReportService>();

        return services;
    }
}
