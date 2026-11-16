using Xunit;

// clock-independent: catalog prices at an explicit recordedAt.
namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure selection rule behind <c>GET /api/token-pricing/unpriced-models</c>:
/// the diagnostic list includes every workspace model row with an unpriced
/// historical call, including models known only at later dates.
/// </summary>
public class TokenPricingEndpointsTests
{
    private static TokenSummaryByModel Model(string label, int calls, long input, long output, bool inCatalog)
        => new(label, calls, input, output, 0, 0, inCatalog ? 1.23m : 0m, inCatalog, inCatalog);

    [Fact]
    public void CalculateItems_LabelsIdsAndBareAliasesUseTheSameCatalogPrices()
    {
        var at = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var rows = TokenPricingEndpoints.CalculateItems(TokenTestServices.Pricing(), new TokenPriceCalculationRequest([
            new("GPT-6 Sol", 1_000_000, 100_000, 0, 0, at, RecordedCostUsd: 2.5m, RecordedModelPriced: true),
            new("gpt-6-sol", 1_000_000, 100_000, 0, 0, at),
            new("Claude Opus 5.5", 1_000_000, 100_000, 0, 0, at),
            new("sonnet", 1_000_000, 100_000, 0, 0, at),
        ]));
        Assert.All(rows, row => Assert.Equal(TokenEconomy.PriceStatus.Resolved, row.Estimate.Status));
        Assert.Equal(rows[0].Estimate.Total, rows[1].Estimate.Total);
        Assert.Equal(ModelIds.Gpt6Sol, rows[0].Estimate.ModelId);
        Assert.Equal(ModelIds.ClaudeOpus55, rows[2].Estimate.ModelId);
        Assert.Equal(ModelIds.ClaudeSonnet5, rows[3].Estimate.ModelId);
        Assert.Equal(2.5m, rows[0].RecordedCostUsd);
    }

    [Fact]
    public void SelectUnpricedModels_ReturnsRowsWithoutHistoricalPrices()
    {
        var aggregate = TokenSummaryFold.ForWorkspace([
            ("proj-a", new TokenSummary(
                "proj-a", 4, 4, 1_150, 115, 0, 0, 1.23m, AllModelsPriced: false, UnknownModelCount: 1,
                ByModel: [
                    Model("claude-opus-4-7", calls: 1, input: 1_000, output: 100, inCatalog: true),
                    Model("Future Model", calls: 1, input: 100, output: 10, inCatalog: false)
                        with { ModelId = "some-future-model" },
                    Model("Dated Model", calls: 2, input: 50, output: 5, inCatalog: true)
                        with { ModelId = "dated-model", ModelPriced = false, EstimatedApiCostUsd = 0m },
                ],
                FirstActivity: null, LastActivity: null, Disclaimer: TokenCostDisclaimer.Text)),
        ]);

        var unpriced = TokenPricingEndpoints.SelectUnpricedModels(aggregate);

        Assert.Equal(2, unpriced.Count);
        var row = unpriced[0];
        Assert.Equal("some-future-model", row.Model);
        Assert.Equal(1, row.Calls);
        Assert.Equal(110L, row.TotalTokens);
        Assert.Equal(0m, row.RecordedCostUsd);
        Assert.Equal("dated-model", unpriced[1].Model);
    }

    [Fact]
    public void SelectUnpricedModels_EveryModelKnown_ReturnsEmpty()
    {
        var aggregate = TokenSummaryFold.ForWorkspace([
            ("proj-a", new TokenSummary(
                "proj-a", 1, 1, 1_000, 100, 0, 0, 1.23m, AllModelsPriced: true, UnknownModelCount: 0,
                ByModel: [Model("claude-opus-4-7", calls: 1, input: 1_000, output: 100, inCatalog: true)],
                FirstActivity: null, LastActivity: null, Disclaimer: TokenCostDisclaimer.Text)),
        ]);

        Assert.Empty(TokenPricingEndpoints.SelectUnpricedModels(aggregate));
    }
}
