using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the workspace-wide fold in <c>TokenSummaryService.AggregateSummaries</c>:
/// per-project <c>byModel</c> rows fold into one workspace row per canonical
/// model id, even when different projects' receipts recorded different label
/// variants of the same model (AGT-2752 — the AGT-2740 receipt/label drift
/// pattern for already-persisted receipts).
/// </summary>
public class TokenSummaryAggregateTests
{
    private static TokenSummaryByModel Model(
        string label, int calls, long input, long output, decimal cost = 0m,
        bool priced = true, bool inCatalog = true)
        => new(label, calls, input, output, 0, 0, cost, priced, inCatalog);

    private static (string Name, TokenSummary Summary) Project(string name, params TokenSummaryByModel[] models)
        => (name, new TokenSummary(
            Project: name,
            OrchestratorEntries: models.Sum(m => m.Calls),
            OrchestratorLlmCalls: models.Sum(m => m.Calls),
            TotalInputTokens: models.Sum(m => m.InputTokens),
            TotalOutputTokens: models.Sum(m => m.OutputTokens),
            TotalCacheReadTokens: 0,
            TotalCacheCreationTokens: 0,
            EstimatedApiCostUsd: models.Sum(m => m.EstimatedApiCostUsd),
            AllModelsPriced: models.All(m => m.ModelPriced),
            UnknownModelCount: models.Count(m => !m.ModelInCatalog),
            ByModel: models,
            FirstActivity: null,
            LastActivity: null,
            Disclaimer: TokenSummaryService.DefaultDisclaimer));

    [Fact]
    public void AggregateSummaries_DisplayNameAndCanonicalIdOfSameModel_FoldIntoOneRow()
    {
        // Project A recorded a receipt before the AGT-2740 persistence fix
        // (display name "GPT-5.5"); project B recorded one after it
        // (canonical id "gpt-5.5"). Both must land in the same workspace row.
        var a = Project("proj-a", Model("GPT-5.5", calls: 2, input: 1_000, output: 100, cost: 0.01m));
        var b = Project("proj-b", Model("gpt-5.5", calls: 3, input: 2_000, output: 200, cost: 0.02m));

        var agg = TokenSummaryService.AggregateSummaries([a, b]);

        var row = Assert.Single(agg.ByModel);
        Assert.Equal(5, row.Calls);
        Assert.Equal(3_000L, row.InputTokens);
        Assert.Equal(300L, row.OutputTokens);
        Assert.Equal(0.03m, row.EstimatedApiCostUsd);
        Assert.Equal("GPT-5.5", row.Model);
    }

    [Fact]
    public void AggregateSummaries_DatedAliasOfKnownModel_FoldsIntoCanonicalRow()
    {
        // A receipt that persisted the catalog's dated alias instead of
        // either the display name or the bare canonical id must still fold
        // into the same row as the bare id.
        var a = Project("proj-a", Model("gpt-5.5-2026-04-23", calls: 1, input: 500, output: 50));
        var b = Project("proj-b", Model("gpt-5.5", calls: 1, input: 500, output: 50));

        var agg = TokenSummaryService.AggregateSummaries([a, b]);

        var row = Assert.Single(agg.ByModel);
        Assert.Equal(2, row.Calls);
        Assert.Equal(1_000L, row.InputTokens);
    }

    [Fact]
    public void AggregateSummaries_UnrelatedUnknownModels_StayAsSeparateRows()
    {
        // Two genuinely different, uncataloged models must not collapse into
        // one row just because neither resolves to a canonical id.
        var a = Project("proj-a", Model("totally-unknown-model-a", calls: 1, input: 100, output: 10, priced: false, inCatalog: false));
        var b = Project("proj-b", Model("totally-unknown-model-b", calls: 1, input: 200, output: 20, priced: false, inCatalog: false));

        var agg = TokenSummaryService.AggregateSummaries([a, b]);

        Assert.Equal(2, agg.ByModel.Count);
        Assert.Contains(agg.ByModel, m => m.Model == "totally-unknown-model-a");
        Assert.Contains(agg.ByModel, m => m.Model == "totally-unknown-model-b");
    }
}
