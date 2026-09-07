using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract for the ad-hoc Haiku usage tracking surface:
/// the recorder appends one JSONL line per call, the aggregator groups
/// by source / day / model and prices Haiku spend, and the parser
/// tolerates plain-text fakes (so existing TitleGenerationService tests
/// stay green).
/// </summary>
public class AdHocUsageTests
{
    [Fact]
    public void Recorder_RoundTripsOneRecord()
    {
        using var temp = new TempDir();
        var (recorder, _) = BuildRecorder(temp.Path);

        var ok = recorder.Record(new AdHocUsageRecord
        {
            Source = AdHocUsageSources.TitleGeneration,
            Model = "claude-haiku-4-5",
            InputTokens = 1500,
            OutputTokens = 80,
            DurationMs = 420
        });

        Assert.True(ok);
        var read = recorder.ReadAll();
        Assert.Single(read);
        Assert.Equal("title-generation", read[0].Source);
        Assert.Equal(1500, read[0].InputTokens);
        Assert.Equal(80, read[0].OutputTokens);
    }

    [Fact]
    public void Recorder_TolerantToTornLines()
    {
        using var temp = new TempDir();
        var (recorder, path) = BuildRecorder(temp.Path);
        recorder.Record(new AdHocUsageRecord { Source = "a", Model = "m", InputTokens = 1 });
        File.AppendAllText(path, "{this is not valid json\n");
        recorder.Record(new AdHocUsageRecord { Source = "b", Model = "m", InputTokens = 2 });

        var read = recorder.ReadAll();
        Assert.Equal(2, read.Count);
        Assert.Equal(1, read[0].InputTokens);
        Assert.Equal(2, read[1].InputTokens);
    }

    [Fact]
    public void Aggregate_GroupsBySourceAndDay()
    {
        var day1 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            Rec("title-generation", "claude-haiku-4-5", 100, 20, day1),
            Rec("title-generation", "claude-haiku-4-5", 200, 40, day1),
            Rec("summary-generation", "claude-haiku-4-5", 50_000, 800, day2),
        };

        var agg = AdHocUsageService.Aggregate(records, "/log", 0, null);

        Assert.Equal(3, agg.Calls);
        Assert.Equal(50_300, agg.InputTokens);
        Assert.Equal(860, agg.OutputTokens);
        Assert.Equal(2, agg.BySource.Count);
        var title = agg.BySource.First(s => s.Source == "title-generation");
        Assert.Equal(2, title.Calls);
        Assert.Equal(300, title.InputTokens);
        Assert.Equal(60, title.OutputTokens);

        Assert.Equal(2, agg.ByDay.Count);
        Assert.Equal("2026-05-02", agg.ByDay[0].Date); // newest first
        Assert.Equal("2026-05-01", agg.ByDay[1].Date);
        Assert.Equal(1, agg.ByDay[0].Calls);
        Assert.Equal(2, agg.ByDay[1].Calls);
    }

    [Fact]
    public void Aggregate_PricesHaikuSpend()
    {
        // Haiku 4.5: $1/M input, $5/M output. 1_000_000 input + 200_000 output -> $1.00 + $1.00 = $2.00
        var records = new[]
        {
            Rec("title-generation", "claude-haiku-4-5", 1_000_000, 200_000, DateTime.UtcNow)
        };
        var agg = AdHocUsageService.Aggregate(records, "/log", 0, null);
        Assert.True(agg.AllModelsPriced);
        Assert.Equal(2.0m, agg.EstimatedApiCostUsd);
    }

    [Fact]
    public void Aggregate_DisplayNameAndCanonicalId_CollapseIntoOneModel()
    {
        var at = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            Rec("title-generation", "Claude Sonnet 5", 1_000, 100, at),
            Rec("summary-generation", "claude-sonnet-5", 2_000, 200, at.AddMinutes(1)),
        };

        var aggregate = AdHocUsageService.Aggregate(records, "/log", 0, null);

        var model = Assert.Single(aggregate.ByModel);
        Assert.Equal("Claude Sonnet 5", model.Model);
        Assert.Equal(ModelIds.ClaudeSonnet5, model.ModelId);
        Assert.Equal(2, model.Calls);
        Assert.True(model.ModelPriced);
        Assert.True(aggregate.AllModelsPriced);
    }

    [Fact]
    public void Aggregate_EmptyLog_ReturnsZeroes()
    {
        var agg = AdHocUsageService.Aggregate(Array.Empty<AdHocUsageRecord>(), "/log", 0, null);
        Assert.Equal(0, agg.Calls);
        Assert.Empty(agg.BySource);
        Assert.Empty(agg.ByDay);
        Assert.False(agg.AllModelsPriced);
    }

    [Fact]
    public void ParseOrFallback_PlainText_ReturnsAsIs()
    {
        var (text, usage) = AdHocClaudeInvoker.ParseOrFallback("Add login form\n", "claude-haiku-4-5");
        Assert.Equal("Add login form\n", text);
        Assert.Null(usage);
    }

    [Fact]
    public void ParseOrFallback_JsonWrapper_ExtractsResultAndUsage()
    {
        const string raw = """
            {"type":"result","subtype":"success","is_error":false,"result":"Add login form","session_id":"abc","model":"claude-haiku-4-5","usage":{"input_tokens":150,"output_tokens":12,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """;
        var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(raw, "claude-haiku-4-5");
        Assert.Equal("Add login form", text);
        Assert.NotNull(usage);
        Assert.Equal(150, usage!.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal("claude-haiku-4-5", usage.Model);
    }

    [Fact]
    public void ParseOrFallback_EmptyInput_ReturnsEmpty()
    {
        var (text, usage) = AdHocClaudeInvoker.ParseOrFallback("", "claude-haiku-4-5");
        Assert.Equal("", text);
        Assert.Null(usage);
    }

    private static AdHocUsageRecord Rec(string source, string model, int input, int output, DateTime ts) =>
        new() { Source = source, Model = model, InputTokens = input, OutputTokens = output, Ts = ts };

    private static (AdHocUsageRecorder recorder, string path) BuildRecorder(string dir)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = dir })
            .Build();
        var recorder = new AdHocUsageRecorder(NullLogger<AdHocUsageRecorder>.Instance, config);
        return (recorder, recorder.LogPath);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adhoc-usage-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
