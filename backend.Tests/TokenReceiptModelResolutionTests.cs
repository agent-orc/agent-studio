using AgentStudio.Tokens;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Durable task receipts persist <c>TaskTokenCall.Model</c> as the catalog
/// display label (e.g. "Claude Sonnet 5"), not the model id (AGT-2740). The
/// project/workspace lifetime aggregate re-derives cost from
/// <c>tokenSummary.Entries[].Model</c> on every read, so a label that no
/// longer resolves against the price catalog's id-keyed lookup silently
/// prices as unknown. These tests lock the receipt-&gt;entry boundary
/// (<see cref="ProjectTokenReceiptReader"/>) resolving the label back to its
/// id so a round-tripped receipt prices identically to a fresh fold.
/// </summary>
public sealed class TokenReceiptModelResolutionTests : IDisposable
{
    private static readonly DateTime FixedRunTime = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _watchPath;

    public TokenReceiptModelResolutionTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "token-receipt-model-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_watchPath)) Directory.Delete(_watchPath, recursive: true); }
        catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(ModelIds.ClaudeSonnet5, "Claude Sonnet 5")]
    [InlineData(ModelIds.ClaudeSonnet46, "Claude Sonnet 4.6")]
    [InlineData(ModelIds.ClaudeOpus48, "Claude Opus 4.8")]
    public void ReceiptRoundtrip_PricesIdenticallyToFreshFold(string modelId, string registryLabel)
    {
        const long input = 1_000_000;
        const long output = 100_000;

        var freshFold = TokenSummaryService.Summarize("Demo", [
            new OrchestratorLogEntry
            {
                Ts = FixedRunTime,
                Kind = OrchestratorLogKinds.Decision,
                Topic = "test-token",
                Summary = "fresh fold",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = modelId,
                    InputTokens = (int)input,
                    OutputTokens = (int)output,
                },
            },
        ]);
        Assert.True(freshFold.AllModelsPriced);
        Assert.True(freshFold.EstimatedApiCostUsd > 0m);

        WriteReceipt("AGT-roundtrip", modelId, registryLabel, input, output);
        var receipts = new ProjectTokenReceiptReader().Read(_watchPath);
        Assert.True(receipts.SourceAvailable);

        var roundtripped = TokenSummaryService.Summarize("Demo", receipts.Entries);

        Assert.True(roundtripped.AllModelsPriced);
        Assert.Equal(freshFold.EstimatedApiCostUsd, roundtripped.EstimatedApiCostUsd);
    }

    private void WriteReceipt(string id, string modelId, string registryLabel, long input, long output)
    {
        var dir = Path.Combine(_watchPath, "6-completed", id);
        Directory.CreateDirectory(dir);
        var total = input + output;
        var payload = new
        {
            id,
            title = id,
            state = "6-completed",
            order = 1,
            tokenSummary = new
            {
                calls = 1,
                inputTokens = input,
                outputTokens = output,
                cacheReadTokens = 0,
                cacheCreationTokens = 0,
                totalTokens = total,
                allModelsPriced = true,
                lastModel = registryLabel,
                lastUpdate = FixedRunTime.ToString("o"),
                entries = new[]
                {
                    new
                    {
                        ts = FixedRunTime.ToString("o"),
                        // Historical receipts persisted the display label here,
                        // not modelId - that's the exact shape being healed.
                        model = registryLabel,
                        participantId = "agent:remote-runner:run_1",
                        inputTokens = input,
                        outputTokens = output,
                        cacheReadTokens = 0,
                        cacheCreationTokens = 0,
                        modelPriced = true,
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "task.json"),
            System.Text.Json.JsonSerializer.Serialize(payload));
    }
}
