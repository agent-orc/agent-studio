using Xunit;
using AgentStudio.Runner;

// clock-independent: catalog prices at an explicit recordedAt.
namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure selection rule behind <c>GET /api/token-pricing/unpriced-models</c>:
/// the diagnostic list includes every workspace model row with an unpriced
/// historical call, including models known only at later dates.
/// </summary>
public class TokenPricingEndpointsTests
{
    [Fact]
    public void Workspace_one_shot_without_price_appears_in_footer_and_week_list()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var calendar = UsageCockpitProjection.Calendar(now, "Etc/UTC", DayOfWeek.Monday);
        var receiptAt = DateTime.SpecifyKind(new DateTime(2026, 11, 16, 0, 30, 0), DateTimeKind.Unspecified);
        var receipt = new OrchestratorLogEntry { Ts = receiptAt, JobId = "one-shot", TokenUsage =
            new OrchestratorTokenUsage { Model = "future-one-shot", InputTokens = 37, OutputTokens = 5 } };
        var sources = new[] { new UsageCostInput(null, "Unattributed one-shot jobs", [receipt],
            new ProjectTokenDataFreshness { Status = "complete" }) };

        Assert.Equal(1, UsageCockpitProjection.Cost(TokenTestServices.Pricing(), calendar, sources, now).UnpricedWeekRuns);
        var row = Assert.Single(TokenPricingEndpoints.SelectUnpricedModels(TokenTestServices.Pricing(), calendar, sources));
        Assert.Equal("future-one-shot", row.Model);
        Assert.Equal(1, row.Calls);
        Assert.Equal(42, row.TotalTokens);
    }
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
    public void Week_list_excludes_old_calls_and_priced_calls_of_the_same_model()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var calendar = UsageCockpitProjection.Calendar(now, "Etc/UTC", DayOfWeek.Monday);
        var sources = new[] { new UsageCostInput("P1", "One", [
            Receipt("mixed-model", now.AddDays(-8), 100),
            Receipt("mixed-model", now.AddDays(-1), 20),
            Receipt("mixed-model", now, 5),
        ], new ProjectTokenDataFreshness { Status = "complete" }) };
        var rows = TokenPricingEndpoints.SelectUnpricedModels(new DatePricing(now), calendar, sources);
        var row = Assert.Single(rows);
        Assert.Equal("mixed-model", row.Model);
        Assert.Equal(1, row.Calls);
        Assert.Equal(20, row.TotalTokens);
    }

    [Fact]
    public void Workspace_bus_attribution_keeps_unpriced_one_shot_in_project_row()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var calendar = UsageCockpitProjection.Calendar(now, "Etc/UTC", DayOfWeek.Monday);
        var bus = new WorkspaceBusUsage(new Dictionary<string, IReadOnlyList<OrchestratorLogEntry>>
        { ["P1"] = [Receipt("future-bus-model", now, 17)] }, []);
        var sources = new[] { new UsageCostInput("P1", "One", bus.MergeInto("P1", []),
            new ProjectTokenDataFreshness { Status = "complete" }) };
        Assert.Equal(1, UsageCockpitProjection.Cost(TokenTestServices.Pricing(), calendar, sources, now).UnpricedWeekRuns);
        Assert.Equal(17, Assert.Single(TokenPricingEndpoints.SelectUnpricedModels(
            TokenTestServices.Pricing(), calendar, sources)).TotalTokens);
    }

    private static OrchestratorLogEntry Receipt(string model, DateTime at, int input)
        => new() { Ts = at, TokenUsage = new OrchestratorTokenUsage { Model = model, InputTokens = input } };

    private sealed class DatePricing(DateTime pricedAt) : ITokenPricing
    {
        private readonly ITokenPricing _base = TokenTestServices.Pricing();
        public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
            => _base.Estimate("gpt-5.6-sol", inputTokens, outputTokens, cacheReadTokens,
                cacheCreationTokens, recordedAt) with { ModelKnown = recordedAt >= pricedAt };

        public IReadOnlyList<PricedTokenEntry> Price(IReadOnlyList<OrchestratorLogEntry> ledger)
            => ledger.Select(entry => new PricedTokenEntry(entry, entry.TokenUsage is { } usage
                ? Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, entry.Ts) : null)).ToList();
    }
}
