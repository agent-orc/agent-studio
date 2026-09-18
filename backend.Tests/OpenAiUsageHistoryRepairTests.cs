using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OpenAiUsageHistoryRepairTests
{
    private static readonly DateTime At = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Task_receipt_repair_is_idempotent_and_reprices_the_normalized_dimensions()
    {
        var legacy = new TaskTokenSummary
        {
            Calls = 1,
            InputTokens = 14_983_295,
            OutputTokens = 24_305,
            CacheReadTokens = 14_786_304,
            TotalTokens = 29_793_904,
            EstimatedApiCostUsd = 66.33m,
            AllModelsPriced = true,
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = At,
                    Model = "gpt-5.6-sol",
                    ParticipantId = "agent:codex",
                    InputTokens = 14_983_295,
                    OutputTokens = 24_305,
                    CacheReadTokens = 14_786_304,
                    EstimatedApiCostUsd = 66.33m,
                    ModelPriced = true,
                },
            ],
        };

        var first = OpenAiUsageHistoryRepair.RepairSummary(legacy, "project", "AGT-2873");
        var second = OpenAiUsageHistoryRepair.RepairSummary(first.Summary, "project", "AGT-2873");

        Assert.True(first.Changed);
        Assert.Equal(1, first.Corrected);
        var entry = Assert.Single(first.Summary.Entries);
        Assert.Equal(196_991, entry.InputTokens);
        Assert.Equal(14_786_304, entry.CacheReadTokens);
        Assert.True(entry.InputIncludesCached);
        Assert.Equal(ProviderUsageNormalization.OpenAiInputIncludesCachedV1, entry.UsageNormalization);
        Assert.Equal(7.19m, decimal.Round(entry.EstimatedApiCostUsd, 2));
        Assert.Equal(14_983_295 + 24_305, first.Summary.TotalTokens);
        Assert.False(second.Changed);
        Assert.Equal(first.Summary, second.Summary);
    }

    [Fact]
    public void Pipeline_repair_recurses_previous_attempts_and_leaves_claude_unchanged()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = "default",
            Project = "project",
            JobId = "AGT-2873",
            StartedAt = At,
            Steps =
            [
                Step("codex", "gpt-5.6-sol", 100, 80),
                Step("claude", "claude-opus-5", 10, 90),
            ],
            PreviousAttempts =
            [
                new PipelineExecutionRecord
                {
                    PipelineId = "default",
                    Project = "project",
                    JobId = "AGT-2873",
                    StartedAt = At.AddHours(-1),
                    Steps = [Step("prior-codex", "gpt-5.6-sol", 50, 40)],
                },
            ],
        };

        var first = OpenAiUsageHistoryRepair.RepairPipeline(record, "project", "AGT-2873");
        var second = OpenAiUsageHistoryRepair.RepairPipeline(first.Record, "project", "AGT-2873");

        Assert.True(first.Changed);
        Assert.Equal(2, first.Corrected);
        Assert.Equal(20, first.Record.Steps[0].InputTokens);
        Assert.Equal(10, first.Record.PreviousAttempts[0].Steps[0].InputTokens);
        Assert.Equal(10, first.Record.Steps[1].InputTokens);
        Assert.Equal(90, first.Record.Steps[1].CacheReadTokens);
        Assert.Null(first.Record.Steps[1].InputIncludesCached);
        Assert.False(second.Changed);
        Assert.Equal(first.Record, second.Record);
    }

    [Fact]
    public void Pipeline_cost_uses_the_repaired_dimensions()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = "default",
            Project = "project",
            JobId = "AGT-2873",
            StartedAt = At,
            CompletedAt = At.AddMinutes(1),
            Steps =
            [
                new PipelineStepExecution
                {
                    StepId = "core-agent-run",
                    Kind = StepKind.Core,
                    Model = "gpt-5.6-sol",
                    StartedAt = At,
                    CompletedAt = At.AddMinutes(1),
                    InputTokens = 14_983_295,
                    OutputTokens = 24_305,
                    CacheReadTokens = 14_786_304,
                },
            ],
        };

        var repaired = OpenAiUsageHistoryRepair
            .RepairPipeline(record, "project", "AGT-2873").Record;
        var timeline = ProjectPipelineCostService.BuildFromRecords(
            "project", [repaired], 1, At.AddHours(1));

        Assert.Equal(7.19m, decimal.Round(timeline.TotalCostUsd, 2));
        Assert.Equal(14_983_295 + 24_305, timeline.TotalTokens);
    }

    [Fact]
    public void OpenAi_entry_outside_the_safe_pattern_is_listed_and_untouched()
    {
        var untouched = new List<OpenAiUsageHistoryUntouchedEntry>();
        var summary = new TaskTokenSummary
        {
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = At,
                    Model = "gpt-5.6-sol",
                    InputTokens = 10,
                    CacheReadTokens = 20,
                },
            ],
        };

        var repair = OpenAiUsageHistoryRepair.RepairSummary(
            summary, "project", "AGT-2873", untouched);

        Assert.False(repair.Changed);
        Assert.Single(untouched);
        Assert.Equal(summary, repair.Summary);
    }

    private static PipelineStepExecution Step(string id, string model, long input, long cacheRead)
        => new()
        {
            StepId = id,
            Kind = StepKind.Aspect,
            Model = model,
            StartedAt = At,
            InputTokens = input,
            OutputTokens = 5,
            CacheReadTokens = cacheRead,
        };
}
