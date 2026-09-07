

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-project rollup math: the orchestrator-log entries
/// in, total amounts + per-model breakdown + theoretical API cost
/// out. Same matrix style as <c>RunOutcomePolicyTests</c>.
/// </summary>
public class TokenSummaryTests
{
    private static OrchestratorLogEntry Entry(string model, long input, long output, long cacheRead = 0, long cacheCreate = 0)
        => new()
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.General,
            Summary = "test entry",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = (int)input,
                OutputTokens = (int)output,
                CacheReadTokens = (int)cacheRead,
                CacheCreationTokens = (int)cacheCreate
            }
        };

    private static OrchestratorLogEntry JobEntry(
        string? model,
        long input,
        long output,
        DateTime ts,
        string jobId = "job-a",
        string? participantId = null)
        => new()
        {
            Ts = ts,
            Kind = OrchestratorLogKinds.Decision,
            Topic = "test-token",
            Summary = "test job entry",
            JobId = jobId,
            ParticipantId = participantId,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = (int)input,
                OutputTokens = (int)output,
            }
        };

    [Fact]
    public void Summarize_NoEntries_ReturnsZeros()
    {
        var s = TokenSummaryService.Summarize("Demo", Array.Empty<OrchestratorLogEntry>());
        Assert.Equal("Demo", s.Project);
        Assert.Equal(0, s.OrchestratorLlmCalls);
        Assert.Equal(0L, s.TotalInputTokens);
        Assert.Equal(0m, s.EstimatedApiCostUsd);
        Assert.Empty(s.ByModel);
    }

    [Fact]
    public void Summarize_EntriesWithoutTokenUsage_DontInflateLlmCallCount()
    {
        // OrchestratorLogEntry without tokenUsage represents an action
        // the runner took without an LLM call (queued follow-up,
        // watchdog kill). Those count toward "entries" but not "LLM calls".
        var entries = new[]
        {
            new OrchestratorLogEntry { Summary = "no usage" },
            Entry("claude-haiku-4-5", 1_000, 100)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);
        Assert.Equal(2, s.OrchestratorEntries);
        Assert.Equal(1, s.OrchestratorLlmCalls);
    }

    [Fact]
    public void Summarize_AggregatesPerModel_WithCorrectCost()
    {
        var entries = new[]
        {
            Entry("claude-opus-4-7", 100_000, 10_000),
            Entry("claude-opus-4-7",  50_000,  5_000),
            Entry("claude-haiku-4-5", 200_000, 20_000)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);

        Assert.Equal(3, s.OrchestratorLlmCalls);
        Assert.Equal(350_000L, s.TotalInputTokens);
        Assert.Equal(35_000L, s.TotalOutputTokens);

        // Opus: 150K in @ $5/M = $0.75; 15K out @ $25/M = $0.375; total $1.125
        // Haiku: 200K in @ $1/M = $0.20; 20K out @ $5/M = $0.10; total $0.30
        // Grand: $1.425
        Assert.Equal(1.425m, s.EstimatedApiCostUsd);
        Assert.True(s.AllModelsPriced);

        Assert.Equal(2, s.ByModel.Count);
        var opus = s.ByModel.Single(m => m.Model == "Claude Opus 4.7");
        Assert.Equal(2, opus.Calls);
        Assert.Equal(150_000L, opus.InputTokens);
        Assert.Equal(1.125m, opus.EstimatedApiCostUsd);
    }

    [Fact]
    public void Summarize_HistoricalGpt56SolUsage_IsPriced()
    {
        var entry = Entry("gpt-5.6-sol", 1_000_000, 100_000) with
        {
            Ts = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
        };

        var summary = TokenSummaryService.Summarize("MKT-20", [entry]);

        Assert.True(summary.AllModelsPriced);
        Assert.Equal(0, summary.UnknownModelCount);
        Assert.True(summary.EstimatedApiCostUsd > 0m);
        var model = Assert.Single(summary.ByModel);
        Assert.True(model.ModelPriced);
        Assert.True(model.ModelInCatalog);
        Assert.True(model.EstimatedApiCostUsd > 0m);
    }

    [Fact]
    public void Summarize_DisplayNameAndCanonicalId_CollapseIntoOnePricedModel()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            Entry("Claude Sonnet 5", 1_000_000, 100_000) with { Ts = at },
            Entry("claude-sonnet-5", 2_000_000, 200_000) with { Ts = at.AddMinutes(1) },
        };

        var summary = TokenSummaryService.Summarize("Demo", entries);

        var model = Assert.Single(summary.ByModel);
        Assert.Equal("Claude Sonnet 5", model.Model);
        Assert.Equal(ModelIds.ClaudeSonnet5, model.ModelId);
        Assert.Equal(2, model.Calls);
        Assert.Equal(3_000_000, model.InputTokens);
        Assert.Equal(300_000, model.OutputTokens);
        Assert.True(model.ModelPriced);
        Assert.True(model.ModelInCatalog);
        Assert.True(summary.AllModelsPriced);
    }

    [Fact]
    public void Summarize_Gpt55DisplayNameAndId_ExposeCanonicalRowIdentity()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        var summary = TokenSummaryService.Summarize(
            "Demo",
            [
                Entry("GPT-5.5", 1_000_000, 100_000) with { Ts = at },
                Entry("gpt-5.5", 2_000_000, 200_000) with { Ts = at.AddMinutes(1) },
            ]);

        var model = Assert.Single(summary.ByModel);
        Assert.Equal("GPT-5.5", model.Model);
        Assert.Equal(ModelIds.Gpt55, model.ModelId);
        Assert.Equal(2, model.Calls);
        Assert.True(model.ModelPriced);
        Assert.True(summary.AllModelsPriced);
    }

    [Fact]
    public void Summarize_CatalogOnlyDisplayNameAndId_ShareCanonicalRowIdentity()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        var summary = TokenSummaryService.Summarize(
            "Demo",
            [
                Entry("GPT-5.6 Sol", 1_000_000, 100_000) with { Ts = at },
                Entry("gpt-5.6-sol", 2_000_000, 200_000) with { Ts = at.AddMinutes(1) },
            ]);

        var model = Assert.Single(summary.ByModel);
        Assert.Equal("GPT-5.6 Sol", model.Model);
        Assert.Equal(ModelIds.Gpt56Sol, model.ModelId);
        Assert.Equal(2, model.Calls);
        Assert.True(model.ModelPriced);
        Assert.True(summary.AllModelsPriced);
    }

    [Fact]
    public void AggregateSummaries_UnpricedRowDoesNotHideKnownPriceSubtotal()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var summary = TokenSummaryService.Summarize(
            "Demo",
            [
                Entry("claude-haiku-4-5", 1_000_000, 200_000) with { Ts = at },
                Entry("unlisted-model", 5_000_000, 500_000) with { Ts = at.AddMinutes(1) },
                Entry("gpt-5-codex", 2_000_000, 200_000) with { Ts = at.AddMinutes(2) },
            ]);

        var aggregate = TokenSummaryService.AggregateSummaries([("Demo", summary)]);

        var priced = Assert.Single(aggregate.ByModel, model => model.ModelPriced);
        var unknown = Assert.Single(aggregate.ByModel, model => !model.ModelInCatalog);
        Assert.Equal(priced.EstimatedApiCostUsd, aggregate.EstimatedApiCostUsd);
        Assert.True(aggregate.EstimatedApiCostUsd > 0m);
        Assert.All(aggregate.ByModel.Where(model => !model.ModelPriced),
            model => Assert.Equal(0m, model.EstimatedApiCostUsd));
        Assert.Equal("unlisted-model", unknown.ModelId);
        Assert.Equal(1, aggregate.UnknownModelCount);
        Assert.Equal(2, aggregate.UnpricedModelCount);
        Assert.False(aggregate.AllModelsPriced);
    }

    [Fact]
    public void Summarize_UnknownModel_FlagsNotAllPriced()
    {
        var entries = new[]
        {
            Entry("claude-opus-4-7", 1_000, 100),
            Entry("unknown-catalog-model", 1_000, 100)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);
        Assert.False(s.AllModelsPriced);
        Assert.Equal(1, s.UnknownModelCount);
        var unknown = s.ByModel.Single(m => m.Model == "unknown-catalog-model");
        Assert.False(unknown.ModelPriced);
        Assert.False(unknown.ModelInCatalog);
        Assert.Equal(0m, unknown.EstimatedApiCostUsd);
    }

    [Fact]
    public void Summarize_KnownModelWithoutPrice_IsUnpricedButNotCatalogDrift()
    {
        var entry = Entry("gpt-5-codex", 1_000, 100) with
        {
            Ts = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var summary = TokenSummaryService.Summarize("Demo", [entry]);

        Assert.False(summary.AllModelsPriced);
        Assert.Equal(0, summary.UnknownModelCount);
        var model = Assert.Single(summary.ByModel);
        Assert.False(model.ModelPriced);
        Assert.True(model.ModelInCatalog);
    }

    [Fact]
    public void DriftMonitor_UnknownActiveModel_LogsOneWarning()
    {
        var summary = TokenSummaryService.Summarize(
            "Demo",
            [Entry("unknown-catalog-model", 1_000, 100)]);
        var logger = new LevelCapturingLogger<TokenPricingDriftMonitor>();
        var monitor = new TokenPricingDriftMonitor(logger);

        monitor.Observe(summary);
        monitor.Observe(summary);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("unknown-catalog-model", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Demo", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_ByModelUsesRegistryLabels()
    {
        var entries = new[]
        {
            Entry("gpt-5-codex", 1_000, 100),
            Entry("claude-sonnet-4.6", 1_000, 100),
        };

        var s = TokenSummaryService.Summarize("Demo", entries);

        Assert.Contains(s.ByModel, m => m.Model == "GPT-5 Codex");
        Assert.Contains(s.ByModel, m => m.Model == "Claude Sonnet 4.6");
    }

    [Fact]
    public void Summarize_DisclaimerIsAlwaysPresent()
    {
        var s = TokenSummaryService.Summarize("Demo", Array.Empty<OrchestratorLogEntry>());
        Assert.Contains("subscription", s.Disclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comparison", s.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizePerJob_PrefersAgentModelAndUsesRegistryLabels()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            JobEntry("gpt-5-codex", 1_000, 100, t, participantId: "agent:codex"),
            JobEntry("claude-haiku-4.5", 2_000, 200, t.AddMinutes(5), participantId: "orchestrator:Demo"),
        };

        var summary = TokenSummaryService.SummarizePerJob(entries)["job-a"];

        Assert.Equal(3_300, summary.TotalTokens);
        Assert.Equal("GPT-5 Codex", summary.LastModel);
        Assert.Equal("GPT-5 Codex", summary.Entries[0].Model);
        Assert.Equal("Claude Haiku 4.5", summary.Entries[1].Model);
        Assert.Equal(t.AddMinutes(5), summary.LastUpdate);
        Assert.False(summary.AllModelsPriced);
        Assert.True(summary.EstimatedApiCostUsd > 0m);
        Assert.False(summary.Entries[0].ModelPriced);
        Assert.True(summary.Entries[1].ModelPriced);
    }

    [Fact]
    public void SummarizePerJob_PricesEachCallAtItsRecordedTimestamp()
    {
        var transition = TokenPricing.Catalog["claude-sonnet-5"].History.Max(price => price.ValidFrom);
        var entries = new[]
        {
            JobEntry("claude-sonnet-5", 1_000_000, 0, transition.AddTicks(-1)),
            JobEntry("claude-sonnet-5", 1_000_000, 0, transition),
        };

        var summary = TokenSummaryService.SummarizePerJob(entries)["job-a"];

        Assert.True(summary.AllModelsPriced);
        Assert.Equal(2, summary.Entries.Count);
        Assert.NotEqual(
            summary.Entries[0].EstimatedApiCostUsd,
            summary.Entries[1].EstimatedApiCostUsd);
        Assert.Equal(
            summary.Entries.Sum(entry => entry.EstimatedApiCostUsd),
            summary.EstimatedApiCostUsd);
    }

    [Fact]
    public void SummarizePerJob_BlankLaterAgentModelDoesNotClearRecordedRunModel()
    {
        var t = new DateTime(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            JobEntry("gpt-5-codex", 1_000, 100, t, participantId: "agent:codex"),
            JobEntry(null, 2_000, 200, t.AddMinutes(5), participantId: "agent:codex"),
            JobEntry("claude-haiku-4.5", 500, 50, t.AddMinutes(10), participantId: "orchestrator:Demo"),
        };

        var summary = TokenSummaryService.SummarizePerJob(entries)["job-a"];

        Assert.Equal("GPT-5 Codex", summary.LastModel);
        Assert.Equal("GPT-5 Codex", summary.Entries[0].Model);
        Assert.Null(summary.Entries[1].Model);
        Assert.Equal("Claude Haiku 4.5", summary.Entries[2].Model);
        Assert.Equal(t.AddMinutes(10), summary.LastUpdate);
    }

    [Fact]
    public void WithModelFallback_FillsBlankAgentAggregateAndRowsFromRegistry()
    {
        var summary = new TaskTokenSummary
        {
            Calls = 1,
            InputTokens = 100,
            TotalTokens = 100,
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = new DateTime(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc),
                    Model = "unknown",
                    ParticipantId = "agent:claude",
                    InputTokens = 100,
                }
            ]
        };

        var filled = TokenSummaryService.WithModelFallback(summary, "claude-sonnet-4.6");

        Assert.Equal("Claude Sonnet 4.6", filled.LastModel);
        Assert.Equal("Claude Sonnet 4.6", filled.Entries[0].Model);
    }

    [Fact]
    public void WithModelFallback_DoesNotStampRunModelOntoOrchestratorRows()
    {
        var summary = new TaskTokenSummary
        {
            Calls = 2,
            InputTokens = 300,
            TotalTokens = 300,
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = new DateTime(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc),
                    Model = null,
                    ParticipantId = "agent:codex",
                    InputTokens = 100,
                },
                new TaskTokenCall
                {
                    Ts = new DateTime(2026, 6, 9, 8, 5, 0, DateTimeKind.Utc),
                    Model = null,
                    ParticipantId = "orchestrator:Demo",
                    InputTokens = 200,
                }
            ]
        };

        var filled = TokenSummaryService.WithModelFallback(summary, "gpt-5-codex");

        Assert.Equal("GPT-5 Codex", filled.LastModel);
        Assert.Equal("GPT-5 Codex", filled.Entries[0].Model);
        Assert.Null(filled.Entries[1].Model);
    }

    [Fact]
    public void WithModelFallback_LeavesOrchestratorOnlyUnknownModelBlank()
    {
        var summary = new TaskTokenSummary
        {
            Calls = 1,
            InputTokens = 100,
            TotalTokens = 100,
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = new DateTime(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc),
                    Model = null,
                    ParticipantId = "orchestrator:Demo",
                    InputTokens = 100,
                }
            ]
        };

        var filled = TokenSummaryService.WithModelFallback(summary, "gpt-5-codex");

        Assert.Null(filled.LastModel);
        Assert.Null(filled.Entries[0].Model);
    }

    private sealed class LevelCapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
