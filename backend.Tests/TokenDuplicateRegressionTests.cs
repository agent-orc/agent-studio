using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenDuplicateRegressionTests : IDisposable
{
    private const string Project = "Agent Studio";
    private const string Job = "AGT-3004";
    private static readonly DateTime At = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "token-duplicate-regression-" + Guid.NewGuid().ToString("N"));
    private string Watch => Path.Combine(_root, "watched");
    private string Folder => Path.Combine(Watch, "tasks", "003", Job);

    public TokenDuplicateRegressionTests() => Directory.CreateDirectory(Folder);
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Historical_receipt_22_identical_plus_one_distinct_is_two_calls_in_every_reader()
    {
        var repeated = Call(At, 249_000_000);
        var distinct = Call(At.AddMinutes(1), 1_000_000);
        WriteReceipt(Enumerable.Repeat(repeated, 22).Append(distinct).ToList());

        var receipt = new ProjectTokenReceiptReader().Read(Watch);
        Assert.Equal(2, receipt.Entries.Count);
        Assert.Equal(2, receipt.Summaries[Job].Calls);
        Assert.Equal(250_000_000, receipt.Summaries[Job].TotalTokens);

        var reader = Reader(new AgentMessageBusStore());
        var summary = reader.BuildSummary(Project, Watch, At.AddDays(1));
        var expensive = Assert.Single(reader.BuildExpensiveJobs(Project, Watch, 10));
        var runner = reader.BuildLifetimeSummary(Project, Watch);
        var perJob = reader.BuildPerJob(Project, Watch)[Job];
        var pipeline = ProjectPipelineCostService.BuildFromRecords(Project,
            ProjectPipelineCostService.BuildReceiptRecords(Project, receipt.Entries),
            3, At.AddDays(1));
        var pipelineReader = new ProjectPipelineCostService(Scanner(Config()),
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            new ProjectTokenReceiptReader());

        Assert.Equal(250_000_000, summary.LifetimeTotalTokens);
        Assert.Equal(2, summary.LifetimeCalls);
        Assert.Equal(250_000_000, expensive.TotalTokens);
        Assert.Equal(2, expensive.Calls);
        Assert.Equal(250_000_000, runner.TotalCacheReadTokens);
        Assert.Equal(250_000_000, perJob.TotalTokens);
        Assert.Equal(250_000_000, pipeline.TotalTokens);
        Assert.Equal(250_000_000, pipelineReader.Build(Project, Watch, 3, At.AddDays(1)).TotalTokens);
    }

    [Fact]
    public void Completion_replay_does_not_change_totals_and_new_usage_is_appended()
    {
        WriteReceipt([]);
        var config = Config();
        var scanner = Scanner(config);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var first = Summary([Call(At, 249_000_000)]);
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(Folder, "run-1", first));
        var beforeReplay = Reader(new AgentMessageBusStore())
            .BuildSummary(Project, Watch, At.AddDays(1)).LifetimeTotalTokens;
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(Folder, "run-1", first));
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(Folder, "run-2", first));
        Assert.Equal(249_000_000, new ProjectTokenReceiptReader().Read(Watch).Summaries[Job].TotalTokens);
        Assert.Equal(beforeReplay, Reader(new AgentMessageBusStore())
            .BuildSummary(Project, Watch, At.AddDays(1)).LifetimeTotalTokens);

        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(Folder, "run-2",
            Summary([Call(At, 249_000_000), Call(At.AddMinutes(1), 1_000_000)])));
        var saved = new ProjectTokenReceiptReader().Read(Watch).Summaries[Job];
        Assert.Equal(2, saved.Calls);
        Assert.Equal(250_000_000, saved.TotalTokens);
    }

    [Fact]
    public async Task Bus_replay_is_idempotent_and_legacy_duplicates_collapse_on_read()
    {
        var message = Message("01HXYZ0000000000000000A001", 249_000_000);
        var store = new AgentMessageBusStore();
        await store.AppendAsync(_root, message);
        await store.AppendAsync(_root, message with { Id = "01HXYZ0000000000000000A002" });
        Assert.Single(store.Query(_root, Project, new AgentMessageQuery(Kind: "token-usage")));

        var path = AgentMessageBusPaths.DayFile(_root, Project, At);
        File.AppendAllLines(path, Enumerable.Range(0, 21).Select(index =>
            JsonSerializer.Serialize(message with
            {
                Id = "01HXYZ0000000000000000" + index.ToString("D4"),
            }, AgentMessageBusStore.SerializerOptions)));
        store.InvalidateProjection(_root, Project);
        var entries = BusTokenEntryConverter.LoadTokenUsageEntries(store, _root, Project);
        Assert.Single(entries);
        Assert.Equal(249_000_000,
            new BusBackedTokenSummaryReader(store, Config()).Summarize(Project)
                .TotalCacheReadTokens);

        await store.AppendAsync(_root, Message("01HXYZ0000000000000000A004", 1_000_000) with
        {
            CreatedAt = At.AddMinutes(1),
        });
        Assert.Equal(2, BusTokenEntryConverter.LoadTokenUsageEntries(store, _root, Project).Count);
        await store.AppendAsync(_root, Message("01HXYZ0000000000000000A005", 249_000_000) with
        {
            CreatedAt = At.AddMinutes(2),
        });
        Assert.Equal(3, BusTokenEntryConverter.LoadTokenUsageEntries(store, _root, Project).Count);
    }

    [Fact]
    public void Legacy_codex_cache_read_is_removed_from_input_before_pricing()
    {
        WriteReceipt([new TaskTokenCall
        {
            Ts = At,
            Model = "gpt-5.6-sol",
            ParticipantId = "agent:remote-runner:run-1",
            InputTokens = 100_000_000,
            OutputTokens = 10_000_000,
            CacheReadTokens = 80_000_000,
            ModelPriced = true,
        }]);
        var entry = Assert.Single(new ProjectTokenReceiptReader().Read(Watch).Entries);
        Assert.Equal(20_000_000, entry.TokenUsage!.InputTokens);
        Assert.Equal(80_000_000, entry.TokenUsage.CacheReadTokens);
        var summary = Reader(new AgentMessageBusStore()).BuildLifetimeSummary(Project, Watch);
        Assert.Equal(110_000_000, summary.TotalInputTokens + summary.TotalOutputTokens
            + summary.TotalCacheReadTokens + summary.TotalCacheCreationTokens);
        var expected = TokenPricing.Estimate("gpt-5.6-sol", 20_000_000, 10_000_000, 80_000_000, 0, At);
        Assert.Equal(decimal.Round(expected.Total, 4), decimal.Round(summary.EstimatedApiCostUsd, 4));
        var pipeline = ProjectPipelineCostService.BuildFromRecords(Project,
            ProjectPipelineCostService.BuildReceiptRecords(Project, [entry]),
            1, At);
        Assert.Equal(110_000_000, pipeline.TotalTokens);
    }

    [Fact]
    public void One_time_repair_reports_collapsed_project_day_and_job_totals()
    {
        WriteReceipt(Enumerable.Repeat(Call(At, 249_000_000), 22)
            .Append(Call(At.AddMinutes(1), 1_000_000)).ToList());
        var config = Config();
        var scanner = Scanner(config);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var repair = new TokenReceiptDuplicateRepair(scanner, mutations, config,
            NullLogger<TokenReceiptDuplicateRepair>.Instance);

        var first = repair.RunOnce();
        Assert.True(first.Completed);
        var project = Assert.Single(first.Projects);
        Assert.Equal(21, project.EntriesCollapsed);
        Assert.Equal(21L * 249_000_000, project.TokensCollapsed);
        Assert.Equal(22L * 249_000_000 + 1_000_000, project.September29BeforeTokens);
        Assert.Equal(250_000_000, project.September29AfterTokens);
        Assert.Equal(project.September29BeforeTokens, project.Agt3004BeforeTokens);
        Assert.Equal(project.September29AfterTokens, project.Agt3004AfterTokens);
        Assert.True(project.September29BeforeCostUsd > project.September29AfterCostUsd);
        Assert.Equal(project.September29AfterCostUsd, project.Agt3004AfterCostUsd);
        Assert.Equal(project, Assert.Single(repair.RunOnce().Projects));
        Assert.Equal(2, new ProjectTokenReceiptReader().Read(Watch).Summaries[Job].Calls);
    }

    private BusBackedProjectTokenUsageReader Reader(AgentMessageBusStore store)
    {
        var config = Config();
        var scanner = Scanner(config);
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        return new BusBackedProjectTokenUsageReader(store, config, stats, new ProjectTokenReceiptReader());
    }

    private TaskScannerService Scanner(IConfiguration config)
        => new(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));

    private IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = Watch,
        }).Build();

    private void WriteReceipt(List<TaskTokenCall> calls)
    {
        File.WriteAllText(Path.Combine(Folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Job,
            title = "Documentation drift audit",
            state = "7-archive",
            order = 1,
            tokenSummary = Summary(calls),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static TaskTokenSummary Summary(List<TaskTokenCall> calls) => new()
    {
        Calls = calls.Count,
        InputTokens = calls.Sum(call => call.InputTokens),
        OutputTokens = calls.Sum(call => call.OutputTokens),
        CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
        CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
        TotalTokens = calls.Sum(call => call.InputTokens + call.OutputTokens
            + call.CacheReadTokens + call.CacheCreationTokens),
        AllModelsPriced = true,
        LastModel = "gpt-6-sol",
        LastUpdate = calls.Count == 0 ? null : calls.Max(call => call.Ts),
        Entries = calls,
    };

    private static TaskTokenCall Call(DateTime ts, long input) => new()
    {
        Ts = ts,
        Model = "gpt-6-sol",
        ParticipantId = "agent:remote-runner:run-1",
        CacheReadTokens = input,
        InputIncludesCached = true,
        ModelPriced = true,
    };

    private static AgentMessage Message(string id, long input) => new()
    {
        Id = id,
        CreatedAt = At,
        Project = Project,
        JobId = Job,
        RunId = "run-1",
        ParticipantId = "agent:codex",
        Role = "evidence",
        Kind = "token-usage",
        Topic = "core-agent-run",
        Tokens = new AgentMessageTokens(0, 0, CacheRead: input,
            Model: "gpt-6-sol", InputIncludesCached: true),
    };
}
