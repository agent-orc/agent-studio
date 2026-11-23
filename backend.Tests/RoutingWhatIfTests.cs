using Xunit;
using AgentStudio.Tokens;
using AgentStudio.Runner;
using AgentStudio.Shared;

// clock-independent: receipt dates are pricing inputs and never compared with the wall clock.
namespace AgentStudio.Tests;

public class RoutingWhatIfTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static UsageCostInput Input(params OrchestratorLogEntry[] entries) =>
        new("p1", "Project", entries, new ProjectTokenDataFreshness { Status = "complete" });
    private static OrchestratorLogEntry Receipt(string participant, DateTime at, string model = "claude-opus-4-8") =>
        new() { Ts = at, ParticipantId = participant, Topic = "general", TokenUsage = new()
        { Model = model, InputTokens = 1000000, OutputTokens = 100000, CacheReadTokens = 100000, CacheCreationTokens = 100000 } };

    [Fact]
    public void Reprices_only_selected_consumer_and_half_open_period_at_receipt_time()
    {
        var pricing = new RecordingPricing();
        var result = RoutingWhatIf.Calculate(pricing, new(Start, Start.AddDays(7), "coding", "alternative"),
            [Input(Receipt("agent:one", Start), Receipt("agent:two", Start.AddDays(7)),
                Receipt("support:aspect-security", Start), Receipt("agent:old", Start.AddSeconds(-1)))]);
        Assert.Equal(1, result.Receipts);
        Assert.Equal(10m, result.ActualUsd);
        Assert.Equal(4m, result.AlternativeUsd);
        Assert.Equal(6m, result.SavingUsd);
        Assert.Equal(60m, result.SavingPercent);
        Assert.All(pricing.Dates, date => Assert.Equal(Start, date));
        Assert.Contains("Correctness", result.CorrectnessFloor);
        Assert.Contains("quality", result.QualityCaveat);
    }

    [Fact]
    public void Missing_historical_price_is_unknown_never_free()
    {
        var result = RoutingWhatIf.Calculate(new RecordingPricing(), new(Start, Start.AddDays(7), "coding", "unknown"),
            [Input(Receipt("agent:one", Start))]);
        Assert.Null(result.AlternativeUsd);
        Assert.Null(result.SavingUsd);
        Assert.Equal(1, result.UnpricedAlternative);
    }

    [Fact]
    public void Empty_period_has_no_projected_saving()
    {
        var result = RoutingWhatIf.Calculate(new RecordingPricing(), new(Start, Start.AddDays(7), "one-shot", "alternative"), []);
        Assert.Null(result.SavingUsd);
        Assert.Equal(0, result.Receipts);
    }

    [Theory]
    [InlineData("review-aspect", "support:aspect-security")]
    [InlineData("one-shot", "orchestrator:summary")]
    public void Consumer_groups_do_not_include_coding(string consumer, string participant)
    {
        var result = RoutingWhatIf.Calculate(new RecordingPricing(), new(Start, Start.AddDays(7), consumer, "alternative"),
            [Input(Receipt("agent:one", Start), Receipt(participant, Start))]);
        Assert.Equal(1, result.Receipts);
    }

    [Theory]
    [InlineData(true, "preference")]
    [InlineData(false, "hard")]
    public void Economy_preview_preserves_pins(bool explicitModel, string pinMode)
    {
        var entry = Receipt("agent:one", Start);
        var card = new TaskDetail { Info = new TaskInfo { ModelExplicit = explicitModel, PinMode = pinMode } };
        Assert.Equal(entry.TokenUsage!.Model, RoutingWhatIf.EconomyModel(new(), entry, card, true));
    }

    [Fact]
    public void Economy_preview_reuses_critical_floor_and_missing_cards_stay_unknown()
    {
        var entry = Receipt("agent:one", Start);
        var card = new TaskDetail { Info = new TaskInfo { ModelExplicit = false, TaskType = "chore", Title = "P0 fencing repair" } };
        Assert.Equal("claude-opus-5", RoutingWhatIf.EconomyModel(new(), entry, card, true));
        Assert.Null(RoutingWhatIf.EconomyModel(new(), entry, null, true));
    }

    [Theory]
    [InlineData(0, "coding", false)]
    [InlineData(94, "coding", false)]
    [InlineData(7, "other", false)]
    [InlineData(7, "coding", true)]
    public void Request_requires_bounded_period_and_known_consumer(int days, string consumer, bool valid)
        => Assert.Equal(valid, RoutingWhatIf.Valid(new(Start, Start.AddDays(days), consumer, "alternative")));

    private sealed class RecordingPricing : ITokenPricing
    {
        public List<DateTime?> Dates { get; } = [];
        public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens, long cacheReadTokens,
            long cacheCreationTokens, DateTime? recordedAt = null)
        {
            Dates.Add(recordedAt);
            Assert.Equal(100000, cacheReadTokens);
            Assert.Equal(100000, cacheCreationTokens);
            return new(0, 0, 0, 0, modelId == "alternative" ? 4 : 10, modelId ?? "", modelId != "unknown",
                default, null);
        }
        public IReadOnlyList<PricedTokenEntry> Price(IReadOnlyList<OrchestratorLogEntry> ledger) => throw new NotSupportedException();
    }
}
