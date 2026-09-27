using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;
using Xunit;
using System.Text.Json;

namespace AgentStudio.Tests;

public sealed class ChatTurnMetadataTests
{
    [Fact]
    public void Local_chat_activity_reports_active_turn_by_project()
    {
        var activity = new LocalChatTurnActivity();
        using (activity.Start("Agent Studio"))
        {
            var row = Assert.Single(activity.Snapshot(DateTime.UtcNow));
            Assert.Equal("Agent Studio", row.ProjectName);
            Assert.Equal(1, row.ActiveTurns);
            Assert.Equal(0, row.HeavyTurns);
            Assert.Null(row.CpuPercent);
        }
        Assert.Empty(activity.Snapshot(DateTime.UtcNow));
    }

    [Fact]
    public void Local_chat_heavy_classification_starts_at_thirty_seconds()
    {
        var started = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(LocalChatTurnActivity.IsHeavy(started, started.AddSeconds(29)));
        Assert.True(LocalChatTurnActivity.IsHeavy(started, started.AddSeconds(30)));
    }

    [Fact]
    public void Prices_normalized_cached_input_once_and_retains_catalogue_version()
    {
        var at = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var usage = new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol",
            InputTokens = 1000,
            CacheReadTokens = 500,
            OutputTokens = 100,
            InputIncludesCached = true,
        };
        var expected = TokenPricing.Estimate(usage.Model, 1000, 100, 500, 0, at);

        var metadata = ChatTurnMetadata.Create("codex", usage.Model, "medium", "thread-1",
            "agent-runner-01", at.AddSeconds(-3), at.AddSeconds(-2), at, usage, 20);

        Assert.Equal(expected.Total, metadata.Cost);
        Assert.Equal(expected.PriceBasis?.Currency, metadata.Currency);
        Assert.StartsWith("TokenEconomy/", metadata.PriceCatalogueVersion);
        Assert.Equal(20, metadata.ReasoningTokens);
        Assert.Equal("chat-turn", metadata.UsageClass);
    }

    [Fact]
    public void Unknown_model_preserves_tokens_without_inventing_zero_cost()
    {
        var metadata = ChatTurnMetadata.Create("codex", "unlisted-model", "medium", null,
            "host", DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            new OrchestratorTokenUsage { Model = "unlisted-model", InputTokens = 42 });
        Assert.Equal(42, metadata.InputTokens);
        Assert.Null(metadata.Cost);
        Assert.StartsWith("TokenEconomy/", metadata.PriceCatalogueVersion);
    }

    [Fact]
    public void Chat_usage_class_keeps_host_project_cost_and_live_occupancy_separate()
    {
        var now = DateTime.UtcNow;
        var receipts = new[]
        {
            new ChatUsageReceipt("Agent Studio", new ChatTurnMetadataDto(
                ExecutingHost: "host-1", FinishedAt: now, InputTokens: 100,
                CachedInputTokens: 40, OutputTokens: 20, ReasoningTokens: 10,
                Cost: 0.01m)),
            new ChatUsageReceipt("Agent Studio", new ChatTurnMetadataDto(
                ExecutingHost: "host-1", FinishedAt: now, InputTokens: 10,
                OutputTokens: 5, Cost: 0.002m)),
            new ChatUsageReceipt("Other", new ChatTurnMetadataDto(
                ExecutingHost: "host-1", FinishedAt: now, InputTokens: 5)),
        };
        var active = new[]
        {
            new RemoteChatUsage("host-1", "Agent Studio", 1, 1, 30, 0, null),
            new RemoteChatUsage("host-1", "Agent Studio", 1, 0, 10, 0, null),
        };

        var rows = ChatUsageProjection.Build(receipts, active);
        var studio = Assert.Single(rows, row => row.ProjectName == "Agent Studio");
        Assert.Equal(2, studio.ActiveTurns);
        Assert.Equal(1, studio.HeavyTurns);
        Assert.Equal(40, studio.CpuPercent);
        Assert.Equal(175, studio.Tokens);
        Assert.Equal(0.012m, studio.CostUsd);
        Assert.Null(Assert.Single(rows, row => row.ProjectName == "Other").CostUsd);
    }

    [Fact]
    public void Chat_metadata_setting_uses_named_key_and_inherits_default_on()
    {
        var workspace = JsonSerializer.Deserialize<WorkspaceSettings>("{}")!;
        var project = JsonSerializer.Deserialize<ProjectSettings>("{}")!;
        Assert.True(project.ChatMetadataEnabled ?? workspace.ChatMetadataEnabled ?? true);

        var disabled = JsonSerializer.Deserialize<ProjectSettings>("""{"chat.metadata.enabled":false}""")!;
        Assert.False(disabled.ChatMetadataEnabled);
        Assert.Contains("\"chat.metadata.enabled\":false", JsonSerializer.Serialize(disabled));
    }
}
