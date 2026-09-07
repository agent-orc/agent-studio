using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenPricingDiagnosticsTests
{
    [Fact]
    public void Build_SeparatesUnknownModelsFromKnownModelsWithoutPrices()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var workspace = TokenSummaryService.AggregateSummaries([
            ("Demo", TokenSummaryService.Summarize("Demo", [
                Entry(ModelIds.Gpt55, 1_000_000, 100_000, at),
                Entry("future-model-without-price", 20_000, 2_000, at),
            ])),
        ]);
        var adHoc = AdHocUsageService.Aggregate(
            [new AdHocUsageRecord
            {
                Ts = at,
                Source = AdHocUsageSources.TitleGeneration,
                Model = ModelIds.Gpt5Codex,
            }],
            logPath: "adhoc.jsonl",
            logSizeBytes: 0,
            logModifiedAt: at);

        var diagnostic = TokenPricingDiagnostics.Build(workspace, adHoc, at);

        Assert.Equal("TokenEconomy", diagnostic.Provider);
        Assert.Equal(3, diagnostic.RecordedModelCount);
        Assert.Equal(1, diagnostic.PricedModelCount);
        Assert.Equal(2, diagnostic.UnpricedModelCount);
        Assert.Equal(1, diagnostic.UnknownModelCount);
        Assert.Equal("future-model-without-price", Assert.Single(diagnostic.UnknownModels).ModelId);
        Assert.Equal(
            nameof(TokenEconomy.PriceStatus.UnknownModel),
            Assert.Single(diagnostic.UnknownModels).ResolutionStatus);

        var knownWithoutPrice = diagnostic.UnpricedModels.Single(model => model.ModelId == ModelIds.Gpt5Codex);
        Assert.True(knownWithoutPrice.ModelInCatalog);
        Assert.Equal(nameof(TokenEconomy.PriceStatus.NoPriceForDate), knownWithoutPrice.ResolutionStatus);
        Assert.False(knownWithoutPrice.HasRecordedTokens);
        Assert.Equal(["ad-hoc"], knownWithoutPrice.Sources);
    }

    [Fact]
    public void Build_IncludesModelRecordedOnlyByZeroTokenTaskReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "token-pricing-zero-receipt-" + Guid.NewGuid().ToString("N"));
        var taskFolder = Path.Combine(root, "tasks", "002", "AGT-2752");
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            Directory.CreateDirectory(taskFolder);
            File.WriteAllText(Path.Combine(taskFolder, "task.json"), JsonSerializer.Serialize(new
            {
                id = "AGT-2752",
                tokenSummary = new TaskTokenSummary
                {
                    Calls = 1,
                    LastModel = "future-zero-token-model",
                    LastUpdate = at,
                    AllModelsPriced = false,
                },
            }));

            var receipt = new ProjectTokenReceiptReader().Read(root);
            var summary = TokenSummaryService.Summarize("Demo", receipt.Entries);
            var workspace = TokenSummaryService.AggregateSummaries([("Demo", summary)]);

            var model = Assert.Single(TokenPricingDiagnostics.Build(workspace, checkedAt: at).UnknownModels);
            Assert.Equal("future-zero-token-model", model.ModelId);
            Assert.Equal(1, model.Calls);
            Assert.False(model.HasRecordedTokens);
            Assert.Equal(["project-lifetime"], model.Sources);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Build_ReportsIncompleteLifetimeCoverage()
    {
        var summary = TokenSummaryService.Summarize("Demo", []) with
        {
            CoverageStatus = "partial",
            CoverageWarning = "One task receipt could not be read.",
        };
        var workspace = TokenSummaryService.AggregateSummaries([("Demo", summary)]);

        var diagnostic = TokenPricingDiagnostics.Build(workspace);

        Assert.Equal("partial", diagnostic.CoverageStatus);
        Assert.Equal(["Demo: One task receipt could not be read."], diagnostic.CoverageWarnings);
    }

    private static OrchestratorLogEntry Entry(
        string model,
        int input,
        int output,
        DateTime at)
        => new()
        {
            Ts = at,
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.General,
            Summary = "diagnostic fixture",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = input,
                OutputTokens = output,
            },
        };
}
