using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class ChatTurnMetadataTests
{
    [Fact]
    public void Prices_normalized_cached_input_once_and_round_trips_turn()
    {
        var now = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);
        var usage = new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol",
            InputTokens = 100,
            CacheReadTokens = 900,
            OutputTokens = 50,
            InputIncludesCached = true,
        };
        var receipt = OrchestratorChatService.PriceChatMetadata(
            new ChatTurnMetadata(now.AddSeconds(-5), now.AddSeconds(-4), now,
                "agent-runner-01", "thread-1", usage.Model, "medium", 10), usage);
        var expected = TokenPricing.Estimate(usage.Model, 100, 50, 900, 0, now);
        Assert.Equal(expected.Total, receipt.Cost);
        Assert.Equal("USD", receipt.Currency);
        Assert.Contains("TokenEconomy", receipt.PriceCatalogueVersion);
        Assert.Equal(5000, receipt.TotalLatencyMs);
        Assert.Equal(1000, receipt.QueueLatencyMs);

        var turn = new OrchestratorChatTurn { Role = "orchestrator", TokenUsage = usage, Metadata = receipt };
        var restored = JsonSerializer.Deserialize<OrchestratorChatTurn>(JsonSerializer.Serialize(turn));
        Assert.Equal(receipt, restored?.Metadata);
        Assert.Equal(900, restored?.TokenUsage?.CacheReadTokens);
    }

    [Fact]
    public void Unknown_model_keeps_tokens_and_omits_price()
    {
        var now = DateTime.UtcNow;
        var metadata = OrchestratorChatService.PriceChatMetadata(
            new ChatTurnMetadata(now, now, now, "local", null, "unknown-model", null),
            new OrchestratorTokenUsage { Model = "unknown-model", InputTokens = 100 });
        Assert.Null(metadata.Cost);
        Assert.Null(metadata.Currency);
    }

    [Fact]
    public void Chat_ledger_participant_has_its_own_usage_class()
    {
        var now = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            new OrchestratorLogEntry
            {
                Ts = now,
                ParticipantId = "chat:codex",
                Topic = "chat-turn",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = "gpt-5.6-sol", InputTokens = 100,
                    CacheReadTokens = 900, OutputTokens = 50
                }
            }
        };
        var summary = ProjectTokenUsageService.BuildSummaryFromEntries("Agent Studio", entries,
            new Dictionary<string, TaskInfo>(), now.AddMinutes(1));
        Assert.Equal(1050, summary.LifetimeChatTokens);
        Assert.Equal(1050, summary.LifetimeTotalTokens);
        Assert.Equal(1, summary.LifetimeChatCalls);
        Assert.Equal(0, summary.LifetimeOrchestratorTokens);
        Assert.True(summary.LifetimeChatCostUsd > 0);

        var unpriced = ProjectTokenUsageService.BuildSummaryFromEntries("Agent Studio",
            [entries[0] with { TokenUsage = new OrchestratorTokenUsage
                { Model = "unknown-model", InputTokens = 100 } }],
            new Dictionary<string, TaskInfo>(), now.AddMinutes(1));
        Assert.Null(unpriced.LifetimeChatCostUsd);
    }

    [Fact]
    public void Chat_metadata_setting_resolves_project_then_workspace_then_default()
    {
        Assert.True(OrchestratorSettingsResolver.ResolveChatMetadata(null, null));
        Assert.False(OrchestratorSettingsResolver.ResolveChatMetadata(null,
            new WorkspaceSettings { ChatMetadataEnabled = false }));
        Assert.True(OrchestratorSettingsResolver.ResolveChatMetadata(
            new ProjectSettings { ChatMetadataEnabled = true },
            new WorkspaceSettings { ChatMetadataEnabled = false }));
    }
}
