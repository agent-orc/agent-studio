using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class ChatTurnMetadataTests
{
    [Fact]
    public void Chat_receipt_prices_uncached_and_cached_input_once_and_round_trips()
    {
        var queued = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var usage = new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol", InputTokens = 100, CacheReadTokens = 900,
            OutputTokens = 50, ReasoningTokens = 20, InputIncludesCached = true,
        };
        var result = new OrchestratorDecisionResult(true, "ok", usage.Model!, usage,
            "provider-thread", null) { CliType = "codex" };

        var metadata = ChatTurnMetadataFactory.Create(result, null, queued,
            queued.AddSeconds(2), queued.AddSeconds(5), "medium");
        var expected = TokenPricing.Estimate(usage.Model, 100, 50, 900, 0, queued.AddSeconds(5));
        var doubled = TokenPricing.Estimate(usage.Model, 1000, 50, 900, 0, queued.AddSeconds(5));
        Assert.True(expected.ModelKnown);
        Assert.Equal(expected.Total, metadata.Cost);
        Assert.NotEqual(doubled.Total, metadata.Cost);
        Assert.Equal(2000, metadata.QueueMs);
        Assert.Equal(3000, metadata.DurationMs);
        Assert.Equal("provider-thread", metadata.ProviderSessionId);
        Assert.Equal("chat-turn", metadata.UsageClass);
        Assert.StartsWith("TokenEconomy ", metadata.PriceCatalogueVersion);

        var turn = new OrchestratorChatTurn { Role = "orchestrator", Metadata = metadata, TokenUsage = usage };
        var copy = JsonSerializer.Deserialize<OrchestratorChatTurn>(JsonSerializer.Serialize(turn));
        Assert.Equal(metadata, copy?.Metadata);
        Assert.Equal(20, copy?.TokenUsage?.ReasoningTokens);
    }

    [Fact]
    public void Unknown_model_keeps_cost_unknown()
    {
        var now = DateTime.UtcNow;
        var result = new OrchestratorDecisionResult(true, "ok", "unknown-model",
            new OrchestratorTokenUsage { Model = "unknown-model", InputTokens = 1 }, null, null);
        var receipt = ChatTurnMetadataFactory.Create(result, null, now, now, now, null);
        Assert.Null(receipt.Cost);
        Assert.Null(receipt.Currency);
        Assert.Equal(1, receipt.InputTokens);
    }
}
