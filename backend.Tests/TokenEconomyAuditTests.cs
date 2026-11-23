using AgentStudio.Review;
using AgentStudio.Tags;
using Xunit;

// clock-independent: explicit fixture timestamps are forwarded to historical pricing and never compared with now.
namespace AgentStudio.Tests;

public sealed class TokenEconomyAuditTests
{
    [Theory]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-haiku-5.5")]
    [InlineData("Claude Haiku 5.5")]
    public void Haiku55_IsHistoricallyPriced(string model)
    {
        var cost = TokenTestServices.Pricing().Estimate(model, 10_000, 1000, 10_000, 0,
            new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(cost.ModelKnown);
        Assert.Equal(0.0016m, cost.Total);
    }

    [Fact]
    public void BoundedEnrichment_DefaultsToHaiku55Low()
    {
        Assert.Equal("claude-haiku-5-5", AutoTagClassifier.ResolveRoute(null).Model);
        var summary = SummaryGenerationService.ResolveStepSelection(null);
        Assert.Equal("claude-haiku-5-5", summary.Model);
        Assert.Equal("low", summary.ThinkingLevel);
    }

    [Fact]
    public void Receipt_TotalWithoutDimensions_DoesNotInventUncachedInput()
    {
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var normalized = ProjectTokenReceiptReader.NormalizeSummary(new TaskTokenSummary
        {
            TotalTokens = 154_000_000,
            LastUpdate = at,
            LastModel = "claude-opus-5-5",
            Entries = [new TaskTokenCall { Ts = at, Model = "claude-opus-5-5",
                InputTokens = 100, CacheReadTokens = 153_000_000, OutputTokens = 999_900 }],
        }, out _);
        Assert.Single(normalized.Entries);
        Assert.Equal(100, normalized.Entries.Sum(call => call.InputTokens));
    }
    [Theory]
    [InlineData("aspect-requirement-fit", TokenUsageKinds.ReviewAspect)]
    [InlineData("aspect-code-quality", TokenUsageKinds.ReviewAspect)]
    [InlineData("aspect-documentation-impact", TokenUsageKinds.ReviewAspect)]
    [InlineData("aspect-tests-and-evidence", TokenUsageKinds.ReviewAspect)]
    [InlineData("post-code-review-grade", TokenUsageKinds.ReviewGrade)]
    public void ReviewReceipt_PreservesStepAndKind(string step, string expectedKind)
    {
        var participant = $"support:remote-review:attempt:step:{step}:usage:attempt:0";
        Assert.Equal(step, ReviewStatisticsFold.StepId(participant));
        Assert.Equal(expectedKind, TokenUsageKindPolicy.ForLedger(participant, "task-token-receipt", true));
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var item = Assert.Single(TokenAnalysisFold.Items(TokenTestServices.Pricing(), new("card", null, null,
            [new OrchestratorLogEntry { JobId = "card", Ts = at, ParticipantId = participant, Topic = "task-token-receipt",
                TokenUsage = new OrchestratorTokenUsage { Model = "claude-opus-5-5", InputTokens = 100 } }])));
        Assert.Equal(step, item.Label);
        Assert.Equal(expectedKind, item.Kind);
        var timeline = ProjectPipelineCostService.BuildFromAnalysis(TokenTestServices.Pricing(), "Studio",
            [("card", item)], 7, at);
        Assert.Equal(step, Assert.Single(timeline.Steps).StepId);
    }

    [Fact]
    public void PipelineAndAnalysis_PriceTheSameFactsInTheSameWindow()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var calls = new[]
        {
            new OrchestratorLogEntry { Ts = now.AddDays(-1), JobId = "card", ParticipantId = "agent:runner",
                TokenUsage = new OrchestratorTokenUsage { Model = "claude-opus-5-5", InputTokens = 1234 } },
            new OrchestratorLogEntry { Ts = now.AddHours(-1), JobId = "card", ParticipantId = "support:adhoc",
                Topic = "auto-tag", TokenUsage = new OrchestratorTokenUsage { Model = "claude-haiku-5-5", InputTokens = 100 } },
        };
        var items = TokenAnalysisFold.Items(TokenTestServices.Pricing(), new("card", null, null, calls));
        var timeline = ProjectPipelineCostService.BuildFromAnalysis(TokenTestServices.Pricing(), "Studio",
            items.Select(item => ("card", item)).ToList(), 28, now);
        Assert.Equal(items.Sum(item => item.Total), timeline.TotalTokens);
        Assert.Equal(items.Sum(item => item.CostUsd), timeline.TotalCostUsd);
        Assert.Equal(timeline.TotalCostUsd, timeline.Steps.Sum(step => step.TotalCostUsd));
    }

    [Fact]
    public void Haiku55_LargePromptUsesCatalogTier()
    {
        var cost = TokenTestServices.Pricing().EstimateRequest("claude-haiku-5-5", 100_001, 1000, 0, 0, 100_001,
            new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(cost.ModelKnown);
        Assert.Equal(0.0525005m, cost.Total);
        Assert.Equal(0.50m, cost.PriceBasis!.InputPerMillion);
        Assert.Equal(2.50m, cost.PriceBasis.OutputPerMillion);
    }
    [Fact]
    public void SummaryWrapper_PreservesRecordedReasoningLevel()
    {
        var parsed = AgentStudio.AdHoc.AdHocClaudeInvoker.ParseOrFallback(
            """{"type":"result","result":"summary","model":"claude-haiku-5-5","thinkingLevel":"low","usage":{"input_tokens":10}}""",
            "claude-haiku-5-5");
        Assert.Equal("low", parsed.Usage?.ThinkingLevel);
    }
    [Fact]
    public void Haiku55_LargeAggregateDoesNotInventRequestPromptSize()
    {
        var cost = TokenTestServices.Pricing().Estimate("claude-haiku-5-5", 200_000, 1000, 0, 0,
            new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(cost.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.AmbiguousPromptSize, cost.Status);
    }
    [Theory]
    [InlineData("support:post-code-review-grade")]
    [InlineData("support:review-attempt:one:step:post-code-review-grade")]
    public void PaidGrade_ReceiptAndMeasuredExecutionCountOnce(string participant)
    {
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var log = new PipelineExecutionRecord { StartedAt = at, Steps = [new PipelineStepExecution
        {
            StepId = "post-code-review-grade", Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed, StartedAt = at, Model = "claude-opus-5-5", InputTokens = 100,
        }] };
        var receipt = new OrchestratorLogEntry { Ts = at, JobId = "card", ParticipantId = participant,
            TokenUsage = new OrchestratorTokenUsage { Model = "claude-opus-5-5", InputTokens = 100 } };
        var items = TokenAnalysisFold.Items(TokenTestServices.Pricing(), new("card", null, log, [receipt]));
        Assert.Equal(100, items.Sum(item => item.Total));
        Assert.Equal(TokenUsageKinds.ReviewGrade, Assert.Single(items, item => item.Total > 0).Kind);
    }
    [Fact]
    public void LargeMeasuredUncachedInput_IsPreserved()
    {
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var normalized = ProjectTokenReceiptReader.NormalizeSummary(new TaskTokenSummary
        {
            InputTokens = 154_000_000, TotalTokens = 154_000_000, LastUpdate = at,
            Entries = [new TaskTokenCall { Ts = at, InputTokens = 154_000_000 }],
        }, out _);
        Assert.Equal(154_000_000, Assert.Single(normalized.Entries).InputTokens);
    }
}
