using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tests;

internal static class TokenTestServices
{
    public static ITokenPricing Pricing() => new TokenPricingService(new TokenEconomyPriceProvider());

    public static TokenAggregationService Aggregator(AgentMessageBusStore store, IConfiguration config,
        JobStatsMetadataCache jobs, ProjectTokenReceiptReader receipts, TaskIndexCache? indexCache = null)
        => new(new BusAggregationCache(store), store, config,
            new ProjectTokenLedger(store, config, receipts, indexCache), jobs,
            new BusAdHocUsageReader(store, config),
            new TokenSummaryCacheStore(config, NullLogger<TokenSummaryCacheStore>.Instance), Pricing());
}
