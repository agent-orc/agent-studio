using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the fake price provider ignores time; timestamps are only forwarded data.
public sealed class TokenPricingInjectionTests
{
    [Fact]
    public void PipelineCosts_UseTheInjectedProvider()
    {
        var provider = new RecordingProvider();
        var calculator = new PipelineCostCalculator(new TokenPricingService(provider));
        var record = new PipelineExecutionRecord
        {
            StartedAt = DateTime.UnixEpoch,
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality",
                    Kind = StepKind.Aspect,
                    Model = "model-from-test",
                    InputTokens = 20,
                    OutputTokens = 10,
                },
            },
        };

        var summary = calculator.Summarize(record);

        Assert.Equal(17m, summary.TotalCostUsd);
        Assert.NotEmpty(provider.Models);
        Assert.All(provider.Models, model => Assert.Equal("model-from-test", model));
    }

    private sealed class RecordingProvider : ITokenPriceProvider
    {
        public List<string?> Models { get; } = [];

        public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
        {
            Models.Add(modelId);
            return new TokenCostEstimate(10m, 7m, 0m, 0m, 17m, modelId ?? "", true,
                TokenEconomy.PriceStatus.Resolved, null);
        }
    }
}
