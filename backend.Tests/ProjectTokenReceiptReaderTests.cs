using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2740: historical task-token receipts persisted the display label
/// (e.g. <c>"Claude Sonnet 5"</c>) as the model field instead of the catalog
/// id. <see cref="ProjectTokenReceiptReader"/> must heal that on read so the
/// lifetime aggregate prices those receipts identically to a fresh fold that
/// recorded the raw id, without any task.json migration.
/// </summary>
public sealed class ProjectTokenReceiptReaderTests : IDisposable
{
    private readonly string _watchPath;

    public ProjectTokenReceiptReaderTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "token-receipt-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("claude-sonnet-5", "Claude Sonnet 5")]
    [InlineData("claude-sonnet-4-6", "Claude Sonnet 4.6")]
    [InlineData("claude-opus-4-8", "Claude Opus 4.8")]
    public void Read_LegacyLabelReceipt_PricesIdenticallyToFirstFold(string modelId, string label)
    {
        var ts = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        WriteLegacyTaskJson("job-legacy", label, ts);

        var healed = new ProjectTokenReceiptReader().Read(_watchPath);
        Assert.True(healed.SourceAvailable);
        Assert.Equal(0, healed.FailedTaskCount);

        var healedSummary = TokenSummaryService.Summarize("Demo", healed.Entries);

        var freshEntries = new[]
        {
            new OrchestratorLogEntry
            {
                Ts = ts,
                Kind = OrchestratorLogKinds.Observation,
                Topic = "task-token-receipt",
                JobId = "job-legacy",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = modelId,
                    InputTokens = 1_000_000,
                    OutputTokens = 100_000,
                },
            },
        };
        var freshSummary = TokenSummaryService.Summarize("Demo", freshEntries);

        Assert.True(freshSummary.AllModelsPriced);
        Assert.True(healedSummary.AllModelsPriced);
        Assert.True(healedSummary.EstimatedApiCostUsd > 0m);
        Assert.Equal(freshSummary.EstimatedApiCostUsd, healedSummary.EstimatedApiCostUsd);

        var healedModel = Assert.Single(healedSummary.ByModel);
        var freshModel = Assert.Single(freshSummary.ByModel);
        Assert.Equal(freshModel.Model, healedModel.Model);
        Assert.True(healedModel.ModelPriced);
        Assert.Equal(freshModel.EstimatedApiCostUsd, healedModel.EstimatedApiCostUsd);
    }

    private void WriteLegacyTaskJson(string jobId, string label, DateTime ts)
    {
        var jobDir = Path.Combine(_watchPath, "tasks", "000", jobId);
        Directory.CreateDirectory(jobDir);

        // Shape mirrors the pre-fix receipt: tokenSummary.entries[].model
        // holds the display label, not the catalog id.
        var json = JsonSerializer.Serialize(new
        {
            id = jobId,
            tokenSummary = new
            {
                calls = 1,
                inputTokens = 1_000_000,
                outputTokens = 100_000,
                cacheReadTokens = 0,
                cacheCreationTokens = 0,
                totalTokens = 1_100_000,
                estimatedApiCostUsd = 0,
                allModelsPriced = false,
                lastModel = label,
                lastUpdate = ts,
                entries = new[]
                {
                    new
                    {
                        ts,
                        model = label,
                        participantId = "agent:remote-runner:run_1",
                        inputTokens = 1_000_000,
                        outputTokens = 100_000,
                        cacheReadTokens = 0,
                        cacheCreationTokens = 0,
                        estimatedApiCostUsd = 0,
                        modelPriced = true,
                    },
                },
            },
        });

        File.WriteAllText(Path.Combine(jobDir, "task.json"), json);
    }
}
