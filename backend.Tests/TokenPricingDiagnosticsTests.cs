using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenPricingDiagnosticsTests
{
    [Fact]
    public void Build_ListsUnknownMissingAndNoPriceGapsAcrossSources()
    {
        var projectSummary = Summary(
            Model("sonnet", modelId: "sonnet", calls: 2, tokens: 120),
            Model("(unknown)", modelId: "(unknown)", calls: 3, tokens: 230),
            Model("GPT-5 Codex", modelId: "gpt-5-codex", calls: 4, tokens: 0),
            Model("zero-token-unknown", modelId: "zero-token-unknown", calls: 1, tokens: 0));
        var adHoc = AdHoc(
            AdHocModel("sonnet", modelId: "sonnet", calls: 1, tokens: 50),
            AdHocModel("Claude Sonnet 5", modelId: "claude-sonnet-5", calls: 5, tokens: 500));

        var diagnostic = TokenPricingDiagnostics.Build([("Project A", projectSummary)], adHoc);

        Assert.Equal("TokenEconomy", diagnostic.CatalogProvider);
        Assert.StartsWith("0.3.3", diagnostic.CatalogVersion, StringComparison.Ordinal);
        Assert.Equal(["Project A"], diagnostic.ScannedProjects);
        Assert.Equal(
            [TokenPricingDiagnostics.ProjectRuntimeSource, TokenPricingDiagnostics.AdHocSource],
            diagnostic.ScannedSources);
        Assert.Equal(5, diagnostic.ScannedModelCount);
        Assert.Equal(4, diagnostic.UnpricedModelCount);
        Assert.Equal(11, diagnostic.UnpricedCallCount);
        Assert.Equal(3, diagnostic.UnknownModelCount);
        Assert.Equal(7, diagnostic.UnknownCallCount);

        var missing = Assert.Single(diagnostic.UnknownModels,
            model => model.Status == TokenPricingGapStatuses.MissingModelId);
        Assert.Null(missing.ModelId);
        Assert.Equal(3, missing.Calls);
        Assert.Equal(230, missing.TotalTokens);
        Assert.Equal(["Project A"], missing.Projects);

        var unknown = Assert.Single(diagnostic.UnknownModels,
            model => model.ModelId == "sonnet");
        Assert.Equal(TokenEconomy.PriceStatus.UnknownModel.ToString(), unknown.Status);
        Assert.Equal("sonnet", unknown.ModelId);
        Assert.Equal(3, unknown.Calls);
        Assert.Equal(170, unknown.TotalTokens);
        Assert.Equal(["Project A"], unknown.Projects);
        Assert.Equal(
            [TokenPricingDiagnostics.AdHocSource, TokenPricingDiagnostics.ProjectRuntimeSource],
            unknown.Sources);

        Assert.DoesNotContain(diagnostic.UnknownModels, model => model.ModelId == "gpt-5-codex");
        Assert.DoesNotContain(diagnostic.UnknownModels, model => model.ModelId == "claude-sonnet-5");
        var noPrice = Assert.Single(diagnostic.UnpricedModels,
            model => model.ModelId == "gpt-5-codex");
        Assert.Equal(TokenEconomy.PriceStatus.NoPriceForDate.ToString(), noPrice.Status);
        Assert.Equal(4, noPrice.Calls);
        Assert.Equal(0, noPrice.TotalTokens);
        var zeroToken = Assert.Single(diagnostic.UnknownModels,
            model => model.ModelId == "zero-token-unknown");
        Assert.Equal(1, zeroToken.Calls);
        Assert.Equal(0, zeroToken.TotalTokens);
    }

    [Fact]
    public void Build_DisplayNamesResolveThroughTokenEconomyCatalog()
    {
        var summary = Summary(
            Model("GPT-5.5", modelId: null, calls: 1, tokens: 100),
            Model("Claude Sonnet 5", modelId: null, calls: 1, tokens: 100),
            Model("Claude Sonnet 4.6", modelId: null, calls: 1, tokens: 100));

        var diagnostic = TokenPricingDiagnostics.Build([("Project A", summary)]);

        Assert.Empty(diagnostic.UnknownModels);
        Assert.Empty(diagnostic.UnpricedModels);
        Assert.Equal(0, diagnostic.UnknownModelCount);
        Assert.Equal(0, diagnostic.UnpricedModelCount);
        Assert.Equal(0, diagnostic.UnknownCallCount);
    }

    private static TokenSummary Summary(params TokenSummaryByModel[] models)
        => new(
            Project: "Project A",
            OrchestratorEntries: models.Sum(model => model.Calls),
            OrchestratorLlmCalls: models.Sum(model => model.Calls),
            TotalInputTokens: models.Sum(model => model.InputTokens),
            TotalOutputTokens: 0,
            TotalCacheReadTokens: 0,
            TotalCacheCreationTokens: 0,
            EstimatedApiCostUsd: 0,
            AllModelsPriced: false,
            UnknownModelCount: 0,
            ByModel: models,
            FirstActivity: null,
            LastActivity: null,
            Disclaimer: TokenSummaryService.DefaultDisclaimer);

    private static TokenSummaryByModel Model(
        string model,
        string? modelId,
        int calls,
        long tokens)
        => new(
            Model: model,
            Calls: calls,
            InputTokens: tokens,
            OutputTokens: 0,
            CacheReadTokens: 0,
            CacheCreationTokens: 0,
            EstimatedApiCostUsd: 0,
            ModelPriced: TokenPricing.Estimate(
                modelId ?? model,
                tokens,
                0,
                0,
                0,
                new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc)).ModelKnown,
            ModelInCatalog: TokenPricing.ModelInCatalog(modelId ?? model),
            ModelId: modelId);

    private static AdHocUsageAggregate AdHoc(params AdHocUsageByModel[] models)
        => new(
            Calls: models.Sum(model => model.Calls),
            InputTokens: models.Sum(model => model.InputTokens),
            OutputTokens: 0,
            CacheReadTokens: 0,
            CacheCreationTokens: 0,
            EstimatedApiCostUsd: 0,
            AllModelsPriced: false,
            BySource: [],
            ByDay: [],
            ByModel: models,
            LogPath: "(bus)",
            LogSizeBytes: 0,
            LogModifiedAt: null,
            Disclaimer: AdHocUsageService.DefaultDisclaimer);

    private static AdHocUsageByModel AdHocModel(
        string model,
        string? modelId,
        int calls,
        long tokens)
        => new(
            Model: model,
            Calls: calls,
            InputTokens: tokens,
            OutputTokens: 0,
            CacheReadTokens: 0,
            CacheCreationTokens: 0,
            EstimatedApiCostUsd: 0,
            ModelPriced: TokenPricing.Estimate(
                modelId ?? model,
                tokens,
                0,
                0,
                0,
                new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc)).ModelKnown,
            ModelId: modelId);
}
