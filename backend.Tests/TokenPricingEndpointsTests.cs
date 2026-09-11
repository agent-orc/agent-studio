using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure selection rule behind <c>GET /api/token-pricing/unpriced-models</c>:
/// the diagnostic list is exactly the workspace aggregate's rows the pinned
/// TokenEconomy catalog does not recognize at all, so a catalog gap is
/// visible instead of only showing up as a silent "Unknown" cost (AGT-2752).
/// </summary>
public class TokenPricingEndpointsTests
{
    private static TokenSummaryByModel Model(string label, int calls, long input, long output, bool inCatalog)
        => new(label, calls, input, output, 0, 0, inCatalog ? 1.23m : 0m, inCatalog, inCatalog);

    [Fact]
    public void SelectUnpricedModels_ReturnsOnlyRowsAbsentFromCatalog()
    {
        var aggregate = TokenSummaryService.AggregateSummaries([
            ("proj-a", new TokenSummary(
                "proj-a", 2, 2, 1_100, 100, 0, 0, 1.23m, AllModelsPriced: false, UnknownModelCount: 1,
                ByModel: [
                    Model("claude-opus-4-7", calls: 1, input: 1_000, output: 100, inCatalog: true),
                    Model("some-future-model", calls: 1, input: 100, output: 10, inCatalog: false),
                ],
                FirstActivity: null, LastActivity: null, Disclaimer: TokenSummaryService.DefaultDisclaimer)),
        ]);

        var unpriced = TokenPricingEndpoints.SelectUnpricedModels(aggregate);

        var row = Assert.Single(unpriced);
        Assert.Equal("some-future-model", row.Model);
        Assert.Equal(1, row.Calls);
        Assert.Equal(110L, row.TotalTokens);
        Assert.Equal(0m, row.RecordedCostUsd);
    }

    [Fact]
    public void SelectUnpricedModels_EveryModelKnown_ReturnsEmpty()
    {
        var aggregate = TokenSummaryService.AggregateSummaries([
            ("proj-a", new TokenSummary(
                "proj-a", 1, 1, 1_000, 100, 0, 0, 1.23m, AllModelsPriced: true, UnknownModelCount: 0,
                ByModel: [Model("claude-opus-4-7", calls: 1, input: 1_000, output: 100, inCatalog: true)],
                FirstActivity: null, LastActivity: null, Disclaimer: TokenSummaryService.DefaultDisclaimer)),
        ]);

        Assert.Empty(TokenPricingEndpoints.SelectUnpricedModels(aggregate));
    }
}
