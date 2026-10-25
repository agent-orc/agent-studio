using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Application flow of the card-centric token analysis over real task
/// folders: durable receipts, the pipeline log, and workspace ad-hoc rows
/// attributed to the card, on an injected clock.
/// </summary>
public sealed class TokenAnalysisServiceTests : IDisposable
{
    private const string Project = "Studio";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "token-analysis-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    public TokenAnalysisServiceTests()
    {
        _watchPath = Path.Combine(_root, "studio");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    [Fact]
    public async Task Cards_combine_receipts_pipeline_steps_and_attributed_ad_hoc_calls()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var folder = WriteTask("job-1", "AGT-1", TaskStates.HumanReview, now.AddHours(-3));
        WritePipelineLog(folder, now.AddHours(-3));
        var (service, bridge) = Build();
        // An ad-hoc title generation for this card: workspace scope, project tag.
        await bridge.EmitTokenUsageAsync(null, "job-1", "support:adhoc", "title-generation",
            new OrchestratorTokenUsage { Model = "claude-sonnet-5", InputTokens = 70, OutputTokens = 7 },
            now.AddHours(-2), attributedProject: Project);
        // Truly projectless: stays out of every card.
        await bridge.EmitTokenUsageAsync(null, null, "support:adhoc", "summary-generation",
            new OrchestratorTokenUsage { Model = "claude-sonnet-5", InputTokens = 5, OutputTokens = 1 },
            now.AddHours(-2));

        var result = service.Analyze([new TokenAnalysisProjectSource("PROJ-1", Project, _watchPath)], 7);

        var card = Assert.Single(result.Cards);
        Assert.Equal("AGT-1", card.Key);
        Assert.Equal(CardOutcomes.Delivered, card.Outcome);
        Assert.Equal(1_100 + 77 + 350, card.TotalTokens);
        Assert.Equal(1_100, card.Slices.Where(slice => slice.Kind == TokenUsageKinds.Coding).Sum(TokenAnalysisFold.SliceTotal));
        Assert.Equal(77, card.Slices.Where(slice => slice.Kind == TokenUsageKinds.Enrichment).Sum(TokenAnalysisFold.SliceTotal));
        Assert.Equal(350, card.Slices.Where(slice => slice.Kind == TokenUsageKinds.ReviewAspect).Sum(TokenAnalysisFold.SliceTotal));
        Assert.Equal(7, result.Days);
        Assert.Equal(now.Date.AddDays(-6), result.From);

        var detail = service.CardDetail(new TokenAnalysisProjectSource("PROJ-1", Project, _watchPath), "AGT-1");
        Assert.NotNull(detail);
        Assert.Equal(card.TotalTokens, detail!.Events.Sum(e => e.Total));
        Assert.Contains(detail.Events, e => e.Kind == TokenUsageKinds.Coding && e.DurationMs == 600_000);
        Assert.Null(service.CardDetail(new TokenAnalysisProjectSource("PROJ-1", Project, _watchPath), "missing"));
    }

    [Fact]
    public void Folded_cards_are_cached_on_the_injected_clock()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        WriteTask("job-1", "AGT-1", TaskStates.Progress, now.AddHours(-1));
        var (service, _) = Build();
        var source = new TokenAnalysisProjectSource("PROJ-1", Project, _watchPath);

        var first = service.Analyze([source], 7);
        Assert.True(Assert.Single(first.Cards).InFlight);
        WriteTask("job-2", "AGT-2", TaskStates.Completed, now.AddHours(-1));

        Assert.Single(service.Analyze([source], 7).Cards);
        _clock.Advance(TokenAnalysisService.CacheTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(2, service.Analyze([source], 7).Cards.Count);
    }

    [Theory]
    [InlineData(null, TokenAnalysisService.DefaultDays)]
    [InlineData(0, TokenAnalysisService.DefaultDays)]
    [InlineData(14, 14)]
    [InlineData(999, TokenAnalysisService.MaxDays)]
    public void Days_are_bounded(int? requested, int expected)
        => Assert.Equal(expected, TokenAnalysisService.ResolveDays(requested));

    private (TokenAnalysisService Service, AgentMessageBusBridge Bridge) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var store = new AgentMessageBusStore();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var pricing = TokenTestServices.Pricing();
        var service = new TokenAnalysisService(pricing, scanner,
            new PipelineExecutionLog(pricing, NullLogger<PipelineExecutionLog>.Instance),
            new ProjectTokenLedger(store, config, new ProjectTokenReceiptReader()),
            store, config, _clock);
        return (service, new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance));
    }

    private string WriteTask(string id, string key, string state, DateTime at)
    {
        var dir = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            id,
            key,
            title = "Card " + key,
            state,
            order = 1,
            agent = "codex",
            tokenSummary = new
            {
                calls = 1,
                inputTokens = 1_000,
                outputTokens = 100,
                totalTokens = 1_100,
                lastUpdate = at.AddMinutes(5),
                entries = new[]
                {
                    new
                    {
                        ts = at.AddMinutes(5), model = "gpt-5.6-sol", participantId = "agent:codex",
                        cliType = "codex", inputTokens = 1_000L, outputTokens = 100L, inputIncludesCached = true,
                    },
                },
            },
        });
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
        return dir;
    }

    private static void WritePipelineLog(string folder, DateTime at)
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = "standard-task-pipeline", JobId = "job-1", Project = Project,
            StartedAt = at, CompletedAt = at.AddHours(1), Attempt = 1,
            Steps =
            [
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core, Status = PipelineStepStatus.Passed,
                    StartedAt = at, CompletedAt = at.AddMinutes(10), DurationMs = 600_000,
                    Model = "gpt-5.6-sol", InputTokens = 1_000, OutputTokens = 100,
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect, Status = PipelineStepStatus.Passed,
                    StartedAt = at.AddMinutes(12), CompletedAt = at.AddMinutes(14), DurationMs = 120_000,
                    Model = "claude-sonnet-5", InputTokens = 300, OutputTokens = 50,
                },
            ],
        };
        File.WriteAllText(Path.Combine(folder, PipelineExecutionLog.FileName),
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
