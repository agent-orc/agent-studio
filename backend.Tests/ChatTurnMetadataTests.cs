using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ChatTurnMetadataTests
{
    [Fact]
    public void Prices_normalized_cached_input_once_and_preserves_timing()
    {
        var queued = new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);
        var metadata = ChatTurnMetadata.Create("gpt-5.6-sol", "medium", "thread-1", "runner-01",
            queued, queued.AddSeconds(3), queued.AddSeconds(8), new OrchestratorTokenUsage
            {
                Model = "gpt-5.6-sol", InputTokens = 20, CacheReadTokens = 80,
                OutputTokens = 10, ReasoningTokens = 4, InputIncludesCached = true,
            }, "codex");

        var expected = TokenPricing.Estimate("gpt-5.6-sol", 20, 10, 80, 0, queued.AddSeconds(8));
        Assert.Equal(expected.Total, metadata.Cost);
        Assert.Equal(20, metadata.InputTokens);
        Assert.Equal(80, metadata.CachedInputTokens);
        Assert.Equal(4, metadata.ReasoningTokens);
        Assert.Equal("thread-1", metadata.ProviderThreadId);
        Assert.Equal("runner-01", metadata.Host);
        Assert.True(metadata.Capabilities.QueueDuration);
        Assert.True(metadata.Capabilities.RunDuration);
        Assert.StartsWith("TokenEconomy/", metadata.PriceCatalogueVersion);
    }

    [Fact]
    public void Task_server_turn_dto_round_trips_chat_metadata()
    {
        var metadata = new OrchestratorChatMetadataDto(
            "gpt-5.6-sol", "medium", "thread-1", "runner-01",
            DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(-4), DateTime.UtcNow,
            20, 80, 10, 4, 0.002m, "USD", "TokenEconomy/0.3.5");
        var dto = new OrchestratorContextTurnDto("turn-1", DateTime.UtcNow,
            "orchestrator", "Hello", Metadata: metadata);
        var roundTrip = JsonSerializer.Deserialize<OrchestratorContextTurnDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(metadata, roundTrip?.Metadata);
    }

    [Fact]
    public void Missing_catalog_price_stays_unknown_while_tokens_and_durations_remain_available()
    {
        var started = new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);
        var metadata = ChatTurnMetadata.Create("unlisted-model", null, null, "runner-01",
            started.AddSeconds(-2), started, started.AddSeconds(4),
            new OrchestratorTokenUsage { InputTokens = 5, OutputTokens = 2 }, "codex");
        Assert.Null(metadata.Cost);
        Assert.Null(metadata.Currency);
        Assert.True(metadata.Capabilities.Tokens);
        Assert.False(metadata.Capabilities.Cost);
        Assert.True(metadata.Capabilities.QueueDuration);
        Assert.True(metadata.Capabilities.RunDuration);
    }

    [Fact]
    public void Known_model_before_first_price_has_no_chat_cost_or_cost_capability()
    {
        const string model = "gpt-5-codex";
        var firstPrice = TokenPricing.Catalog[model].History.Min(price => price.ValidFrom);
        var finished = firstPrice.AddTicks(-1);
        var metadata = ChatTurnMetadata.Create(model, "medium", "thread-1", "runner-01",
            finished.AddSeconds(-5), finished.AddSeconds(-4), finished,
            new OrchestratorTokenUsage { Model = model, InputTokens = 100, OutputTokens = 20 }, "codex");

        Assert.Equal(model, metadata.Model);
        Assert.Equal(100, metadata.InputTokens);
        Assert.Null(metadata.Cost);
        Assert.Null(metadata.Currency);
        Assert.Null(metadata.PriceCatalogueVersion);
        Assert.False(metadata.Capabilities.Cost);
        Assert.True(metadata.Capabilities.Tokens);
    }

    [Fact]
    public void Local_chat_tracker_keeps_project_cost_unknown_after_unpriced_known_model()
    {
        const string model = "gpt-5-codex";
        var firstPrice = TokenPricing.Catalog[model].History.Min(price => price.ValidFrom);
        var tracker = new LocalChatUsageTracker();
        tracker.Start("unpriced", "Agent Studio", firstPrice.AddMinutes(-1), firstPrice.AddSeconds(-2));
        tracker.Complete("unpriced", new OrchestratorTokenUsage
        {
            Model = model, InputTokens = 100, OutputTokens = 20,
        }, model, firstPrice.AddTicks(-1));
        var unpriced = Assert.Single(tracker.GetUsage(firstPrice));
        Assert.Equal(120, unpriced.Tokens);
        Assert.Null(unpriced.CostUsd);

        tracker.Start("priced", "Agent Studio", firstPrice, firstPrice);
        tracker.Complete("priced", new OrchestratorTokenUsage
        {
            Model = model, InputTokens = 50, OutputTokens = 10,
        }, model, firstPrice.AddSeconds(1));
        var combined = Assert.Single(tracker.GetUsage(firstPrice.AddSeconds(2)));
        Assert.Equal(180, combined.Tokens);
        Assert.Null(combined.CostUsd);
    }

    [Fact]
    public void Local_chat_tracker_reports_heavy_activity_and_completed_project_cost()
    {
        var tracker = new LocalChatUsageTracker();
        var start = new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);
        tracker.Start("turn-1", "Agent Studio", start.AddSeconds(-2), start);
        var active = Assert.Single(tracker.GetUsage(start.AddSeconds(31)));
        Assert.Equal(1, active.ActiveTurns);
        Assert.Equal(1, active.HeavyTurns);
        Assert.Null(active.CpuPercent);

        tracker.Complete("turn-1", new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol", InputTokens = 20, CacheReadTokens = 80,
            OutputTokens = 10, InputIncludesCached = true,
        }, "gpt-5.6-sol", start.AddSeconds(35));
        var completed = Assert.Single(tracker.GetUsage(start.AddSeconds(36)));
        Assert.Equal(0, completed.ActiveTurns);
        Assert.Equal(110, completed.Tokens);
        Assert.Equal(TokenPricing.Estimate("gpt-5.6-sol", 20, 10, 80, 0,
            start.AddSeconds(35)).Total, completed.CostUsd);
    }
}
