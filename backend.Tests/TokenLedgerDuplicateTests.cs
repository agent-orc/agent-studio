using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3012: AGT-3004's receipt held one Claude session's usage 22 times
/// (5.6 billion tokens over 23 calls instead of about 260 million over two).
/// The Claude result frame restates the session's cumulative
/// <c>modelUsage</c> on every frame and the remote receipt summed each one.
/// These tests pin the write path (one row per session snapshot, completion
/// replay is a no-op), every reader (collapse already-written duplicates),
/// the one-off repair, and the AGT-2882 Codex cache correction on the
/// receipt and bus read paths.
/// </summary>
public sealed class TokenLedgerDuplicateTests : IDisposable
{
    private const string ProjectName = "Agent Studio";
    private const string JobId = "agt-3004";
    private const string Attempt = "run_3fe269e9166f41248f987acc7011ee5c";
    private const string LaterAttempt = "run_a131d6c417b2465ea656914cde1969a2";

    // AGT-3004 production values (task.json receipt, 2026-10-04).
    private static readonly DateTime SessionAt = new(2026, 9, 29, 13, 29, 54, 50, DateTimeKind.Utc);
    private static readonly DateTime CodexAt = new(2026, 10, 3, 4, 32, 56, 381, DateTimeKind.Utc);
    private const long SessionInput = 4_096;
    private const long SessionOutput = 983_563;
    private const long SessionCacheRead = 248_769_893;
    private const long SessionCacheWrite = 4_987_546;
    private const long SessionTotal = 254_745_098;
    private const long CodexTotal = 6_129_017;
    private const int Copies = 22;

    private readonly string _workspace;
    private readonly string _watchPath;

    public TokenLedgerDuplicateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "token-ledger-duplicates-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "watched");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- write path -------------------------------------------------------

    [Fact]
    public void Claude_modelUsage_is_marked_as_a_session_cumulative_snapshot()
    {
        using var frame = JsonDocument.Parse(ResultFrame(turnInput: 54, session: "s-1"));

        var usage = Assert.Single(new ClaudeUsageParser().ParseAll(frame.RootElement, "claude-opus-5-5", new CliModelRegistry()));

        Assert.Equal("claude-session:s-1", usage.CumulativeScope);
        Assert.Equal(SessionCacheRead, usage.CacheRead);
    }

    [Fact]
    public void Remote_receipt_from_the_AGT3004_log_records_the_session_once_and_replay_is_a_noop()
    {
        var (scanner, mutations, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        WriteCliLog(Enumerable.Range(0, Copies).Select(index => ResultFrame(turnInput: 10 + index, session: "b2cb04c3")));
        var task = scanner.FindJob(JobId, _watchPath)!;

        var first = receipts.PersistFromLog(task, Attempt, "agent-runner-01");

        Assert.True(first.Persisted);
        Assert.Equal(SessionTotal, first.TotalTokens);
        var receipt = ReadReceipt();
        var call = Assert.Single(receipt.Entries);
        Assert.Equal(SessionCacheRead, call.CacheReadTokens);
        Assert.Equal(SessionTotal, receipt.TotalTokens);
        var totalsBefore = ProjectTotals();

        // A completion retry of the same attempt and a re-attach that reports
        // the same log under a new attempt id must leave the ledger unchanged.
        var bytesBefore = File.ReadAllText(TaskJsonPath());
        receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, Attempt, "agent-runner-01");
        Assert.Equal(bytesBefore, File.ReadAllText(TaskJsonPath()));
        receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, LaterAttempt, "agent-runner-01");
        Assert.Equal(bytesBefore, File.ReadAllText(TaskJsonPath()));
        Assert.Equal(totalsBefore, ProjectTotals());
        Assert.NotNull(mutations);
    }

    [Fact]
    public void Receipt_store_appends_new_usage_and_ignores_usage_already_recorded_by_another_attempt()
    {
        var (_, mutations, _) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var session = SessionCall(participant: null);
        var codex = CodexCall(participant: null);

        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, Attempt, Summary(session)));
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(session, session, codex)));

        var receipt = ReadReceipt();
        Assert.Equal(2, receipt.Calls);
        Assert.Equal(SessionTotal + CodexTotal, receipt.TotalTokens);
        Assert.Equal($"agent:remote-runner:{Attempt}", receipt.Entries[0].ParticipantId);
        Assert.Equal($"agent:remote-runner:{LaterAttempt}", receipt.Entries[1].ParticipantId);

        var bytes = File.ReadAllText(TaskJsonPath());
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(session, codex)));
        Assert.Equal(bytes, File.ReadAllText(TaskJsonPath()));
    }

    [Fact]
    public void Per_turn_usages_are_kept_and_only_the_last_cumulative_snapshot_counts()
    {
        var at = SessionAt;
        var turn = new ParsedTurnUsage("gpt-6-sol", 10, 1, 0, 0, null, null);
        var early = new ParsedTurnUsage("claude-opus-5-5", 1, 1, 100, 0, null, null, CumulativeScope: "claude-session:a");
        var late = early with { CacheRead = 300 };
        var other = early with { CumulativeScope = "claude-session:b", CacheRead = 50 };

        var kept = RemoteTokenReceiptService.LatestCumulativeSnapshots(
            [(at, turn), (at, early), (at.AddSeconds(1), turn), (at.AddSeconds(2), late), (at, other)]);

        Assert.Equal(4, kept.Count);
        Assert.Equal(2, kept.Count(item => item.Usage.CumulativeScope is null));
        Assert.Contains(kept, item => item.Usage.CacheRead == 300);
        Assert.DoesNotContain(kept, item => item.Usage.CacheRead == 100);
        Assert.Contains(kept, item => item.Usage.CacheRead == 50);
    }

    // ---- readers ----------------------------------------------------------

    [Fact]
    public void Receipt_reader_collapses_the_AGT3004_pattern()
    {
        WriteTask(Agt3004Receipt());

        var read = new ProjectTokenReceiptReader().Read(_watchPath);

        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(Copies - 1, read.CollapsedDuplicates.Entries);
        Assert.Equal((Copies - 1) * SessionTotal, read.CollapsedDuplicates.Tokens);
        var summary = read.Summaries[JobId];
        Assert.Equal(2, summary.Calls);
        Assert.Equal(SessionTotal + CodexTotal, summary.TotalTokens);
        Assert.Equal(SessionTotal + CodexTotal, read.Entries.Sum(Total));
    }

    [Fact]
    public void Project_usage_readers_report_the_real_AGT3004_usage()
    {
        WriteTask(Agt3004Receipt());
        var reader = BuildProjectReader();

        var expensive = Assert.Single(reader.BuildExpensiveJobs(ProjectName, _watchPath, 10));
        Assert.Equal(SessionTotal + CodexTotal, expensive.TotalTokens);
        Assert.Equal(2, expensive.Calls);

        var summary = reader.BuildSummary(ProjectName, _watchPath, CodexAt.AddDays(1));
        Assert.Equal(SessionTotal + CodexTotal, summary.LifetimeTotalTokens);
        Assert.Equal(2, summary.LifetimeCalls);

        var detail = reader.BuildJobDetail(ProjectName, _watchPath, JobId)!;
        Assert.Equal(2, detail.Calls);
        Assert.Equal(SessionTotal + CodexTotal, detail.TotalTokens);

        // runner/{project}/token-summary
        var lifetime = reader.BuildLifetimeSummary(ProjectName, _watchPath);
        Assert.Equal(2, lifetime.OrchestratorLlmCalls);
        Assert.Equal(SessionCacheRead + 5_901_696, lifetime.TotalCacheReadTokens);
    }

    [Fact]
    public void Pipeline_cost_day_of_the_AGT3004_session_counts_it_once()
    {
        WriteTask(Agt3004Receipt());
        var (scanner, _, _) = BuildWriteServices();
        var service = new ProjectPipelineCostService(
            scanner, new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance), new ProjectTokenReceiptReader());

        var timeline = service.Build(ProjectName, _watchPath, 30, CodexAt.AddDays(1));

        var day = timeline.DayCosts.Single(cell => cell.Day == "2026-09-29");
        Assert.Equal(SessionTotal, day.TotalTokens);
        var once = TokenPricing.Estimate("claude-opus-5-5", SessionInput, SessionOutput, SessionCacheRead, SessionCacheWrite, SessionAt);
        Assert.Equal(decimal.Round(once.Total, 2), decimal.Round(day.CostUsd, 2));
    }

    [Fact]
    public async Task Bus_readers_collapse_identical_token_usage_messages()
    {
        var store = new AgentMessageBusStore();
        for (var copy = 0; copy < Copies; copy++)
            await store.AppendAsync(_workspace, BusMessage($"duplicate-{copy:D2}", "run-1", SessionAt, SessionCacheRead));
        await store.AppendAsync(_workspace, BusMessage("distinct", "run-1", CodexAt, 5_901_696));
        // Same instant and counts from a different run is a different call.
        await store.AppendAsync(_workspace, BusMessage("other-run", "run-2", SessionAt, SessionCacheRead));

        var entries = BusTokenEntryConverter.LoadTokenUsageEntries(store, _workspace, ProjectName);
        Assert.Equal(3, entries.Count);

        var summary = new BusBackedTokenSummaryReader(store, BuildConfig()).Summarize(ProjectName);
        Assert.Equal(3, summary.OrchestratorLlmCalls);
        Assert.Equal(2 * SessionCacheRead + 5_901_696, summary.TotalCacheReadTokens);

        var projectReader = BuildProjectReader(store);
        var row = Assert.Single(projectReader.BuildExpensiveJobs(ProjectName, _watchPath, 10));
        Assert.Equal(3, row.Calls);
    }

    [Fact]
    public void Collapse_returns_the_same_list_when_there_are_no_duplicates()
    {
        var messages = new List<AgentMessage>
        {
            BusMessage("a", "run-1", SessionAt, 1),
            BusMessage("b", "run-1", SessionAt, 2),
        };

        var kept = TokenLedgerDuplicates.CollapseMessages(messages, out var collapsed);

        Assert.Same(messages, kept);
        Assert.Equal(TokenLedgerCollapseCount.None, collapsed);
    }

    // ---- repair -----------------------------------------------------------

    [Fact]
    public void Repair_reports_collapsed_entries_per_project_and_runs_once()
    {
        var store = new FakeRepairStore(
        [
            new TokenLedgerReceiptSource("/agt-3004", ProjectName, "AGT-3004", Agt3004Summary()),
            new TokenLedgerReceiptSource("/clean", ProjectName, "AGT-1", Summary(CodexCall("agent:codex"))),
        ]);
        var reportPath = Path.Combine(_workspace, "migrations", TokenLedgerDuplicateRepair.ReportFileName);
        var repair = new TokenLedgerDuplicateRepair(store, reportPath, NullLogger<TokenLedgerDuplicateRepair>.Instance);

        var report = repair.RunOnce();

        Assert.True(report.Completed);
        var project = Assert.Single(report.Projects);
        Assert.Equal(1, project.ReceiptTasks);
        Assert.Equal(Copies - 1, project.ReceiptEntriesCollapsed);
        Assert.Equal((Copies - 1) * SessionTotal, project.ReceiptTokensCollapsed);
        Assert.True(project.ReceiptCostCollapsedUsd > 0m);
        var task = Assert.Single(report.Tasks);
        Assert.Equal(Copies + 1, task.EntriesBefore);
        Assert.Equal(SessionTotal + CodexTotal, task.TotalTokensAfter);
        var rewritten = Assert.Single(store.Rewrites);
        Assert.Equal(2, rewritten.Summary.Entries.Count);
        Assert.Equal(SessionTotal + CodexTotal, rewritten.Summary.TotalTokens);
        Assert.True(File.Exists(reportPath));

        var again = repair.RunOnce();
        Assert.True(again.AlreadyCompleted);
        Assert.Single(store.Rewrites);
    }

    // ---- AGT-2882 Codex cache correction ----------------------------------

    [Fact]
    public void Legacy_codex_receipt_is_normalized_before_pricing_in_every_receipt_reader()
    {
        var legacy = new TaskTokenCall
        {
            Ts = CodexAt,
            Model = "gpt-5.6-sol",
            ParticipantId = "agent:remote-runner:run_1",
            InputTokens = 1_000,
            OutputTokens = 100,
            CacheReadTokens = 400,
        };
        WriteTask(Summary(legacy));

        var entry = Assert.Single(new ProjectTokenReceiptReader().Read(_watchPath).Entries);
        Assert.Equal(600, entry.TokenUsage!.InputTokens);
        Assert.Equal(400, entry.TokenUsage.CacheReadTokens);
        Assert.True(entry.TokenUsage.InputIncludesCached);

        var expected = TokenPricing.Estimate("gpt-5.6-sol", 600, 100, 400, 0, CodexAt);
        Assert.True(expected.ModelKnown);
        var lifetime = BuildProjectReader().BuildLifetimeSummary(ProjectName, _watchPath);
        Assert.Equal(600, lifetime.TotalInputTokens);
        Assert.Equal(expected.Total, lifetime.EstimatedApiCostUsd);

        var (scanner, _, _) = BuildWriteServices();
        var timeline = new ProjectPipelineCostService(
                scanner, new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance), new ProjectTokenReceiptReader())
            .Build(ProjectName, _watchPath, 30, CodexAt.AddDays(1));
        Assert.Equal(1_100, timeline.TotalTokens);
        Assert.Equal(decimal.Round(expected.Total, 2), decimal.Round(timeline.TotalCostUsd, 2));
    }

    [Fact]
    public async Task Legacy_codex_bus_row_is_normalized_before_pricing_in_the_project_reader()
    {
        var store = new AgentMessageBusStore();
        await store.AppendAsync(_workspace, new AgentMessage
        {
            Id = "codex-legacy",
            CreatedAt = CodexAt,
            ParticipantId = "agent:codex",
            Role = "evidence",
            Kind = "token-usage",
            Project = ProjectName,
            JobId = JobId,
            RunId = "run-1",
            Tokens = new AgentMessageTokens(1_000, 100, CacheRead: 400, Model: "gpt-5.6-sol"),
        });

        var lifetime = BuildProjectReader(store).BuildLifetimeSummary(ProjectName, _watchPath);

        Assert.Equal(600, lifetime.TotalInputTokens);
        Assert.Equal(400, lifetime.TotalCacheReadTokens);
        Assert.Equal(TokenPricing.Estimate("gpt-5.6-sol", 600, 100, 400, 0, CodexAt).Total, lifetime.EstimatedApiCostUsd);
    }

    // ---- fixtures ---------------------------------------------------------

    private static long Total(OrchestratorLogEntry entry)
        => (long)entry.TokenUsage!.InputTokens + entry.TokenUsage.OutputTokens
           + entry.TokenUsage.CacheReadTokens + entry.TokenUsage.CacheCreationTokens;

    private static TaskTokenCall SessionCall(string? participant) => new()
    {
        Ts = SessionAt,
        Model = "claude-opus-5-5",
        ParticipantId = participant,
        InputTokens = SessionInput,
        OutputTokens = SessionOutput,
        CacheReadTokens = SessionCacheRead,
        CacheCreationTokens = SessionCacheWrite,
        ModelPriced = true,
    };

    private static TaskTokenCall CodexCall(string? participant) => new()
    {
        Ts = CodexAt,
        Model = "gpt-6-sol",
        ParticipantId = participant,
        InputTokens = 195_722,
        OutputTokens = 31_599,
        CacheReadTokens = 5_901_696,
        InputIncludesCached = true,
    };

    private static TaskTokenSummary Summary(params TaskTokenCall[] calls) => new()
    {
        Calls = calls.Length,
        InputTokens = calls.Sum(call => call.InputTokens),
        OutputTokens = calls.Sum(call => call.OutputTokens),
        CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
        CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
        TotalTokens = calls.Sum(TokenLedgerDuplicates.TotalTokens),
        LastUpdate = calls.Max(call => call.Ts),
        Entries = calls.ToList(),
    };

    /// <summary>22 identical session rows plus one distinct call, as persisted for AGT-3004.</summary>
    private static TaskTokenSummary Agt3004Summary()
        => Summary(Enumerable.Repeat(SessionCall($"agent:remote-runner:{Attempt}"), Copies)
            .Append(CodexCall($"agent:remote-runner:{LaterAttempt}"))
            .ToArray());

    private static object Agt3004Receipt() => Agt3004Summary();

    private static string ResultFrame(long turnInput, string session)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["session_id"] = session,
            ["total_cost_usd"] = 94.86,
            ["usage"] = new { input_tokens = turnInput, output_tokens = 33, cache_read_input_tokens = 86_854 },
            ["modelUsage"] = new Dictionary<string, object>
            {
                ["claude-opus-5-5"] = new
                {
                    inputTokens = SessionInput,
                    outputTokens = SessionOutput,
                    cacheReadInputTokens = SessionCacheRead,
                    cacheCreationInputTokens = SessionCacheWrite,
                },
            },
        });

    private static AgentMessage BusMessage(string id, string runId, DateTime at, long cacheRead) => new()
    {
        Id = id,
        CreatedAt = at,
        ParticipantId = "agent:claude",
        Role = "evidence",
        Kind = "token-usage",
        Project = ProjectName,
        JobId = JobId,
        RunId = runId,
        Tokens = new AgentMessageTokens(SessionInput, SessionOutput, CacheRead: cacheRead, CacheWrite: SessionCacheWrite, Model: "claude-opus-5-5"),
    };

    private string TaskJsonPath() => Path.Combine(_watchPath, "6-completed", JobId, "task.json");

    private void WriteTask(object? tokenSummary)
    {
        var dir = Path.GetDirectoryName(TaskJsonPath())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(TaskJsonPath(), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = JobId,
            ["title"] = "Documentation drift audit",
            ["state"] = "6-completed",
            ["order"] = 1,
            ["agent"] = "claude",
            ["cliType"] = "claude",
            ["model"] = "claude-opus-5-5",
            ["tokenSummary"] = tokenSummary,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private void WriteCliLog(IEnumerable<string> frames)
    {
        var path = TaskPaths.CliOutputLog(Path.GetDirectoryName(TaskJsonPath())!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Every result frame was flushed in the same millisecond.
        File.WriteAllLines(path, frames.Select(frame => $"[13:29:54.050] [stdout] {frame}"));
        File.SetLastWriteTimeUtc(path, SessionAt);
    }

    private TaskTokenSummary ReadReceipt()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TaskJsonPath()));
        return document.RootElement.GetProperty("tokenSummary")
            .Deserialize<TaskTokenSummary>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private (long Tokens, int Calls) ProjectTotals()
    {
        var summary = BuildProjectReader().BuildLifetimeSummary(ProjectName, _watchPath);
        return (summary.TotalInputTokens + summary.TotalOutputTokens + summary.TotalCacheReadTokens
                + summary.TotalCacheCreationTokens, summary.OrchestratorLlmCalls);
    }

    private IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();

    private BusBackedProjectTokenUsageReader BuildProjectReader(AgentMessageBusStore? store = null)
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        return new BusBackedProjectTokenUsageReader(
            store ?? new AgentMessageBusStore(), config, stats, new ProjectTokenReceiptReader());
    }

    private (TaskScannerService Scanner, TaskMutationService Mutations, RemoteTokenReceiptService Receipts) BuildWriteServices()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var receipts = new RemoteTokenReceiptService(
            new CliUsageParserRegistry([new ClaudeUsageParser(), new CodexUsageParser()]),
            new CliModelRegistry(),
            mutations,
            new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance),
            NullLogger<RemoteTokenReceiptService>.Instance);
        return (scanner, mutations, receipts);
    }

    private sealed class FakeRepairStore(IReadOnlyList<TokenLedgerReceiptSource> jobs) : ITokenLedgerDuplicateRepairStore
    {
        public List<TokenLedgerReceiptRewrite> Rewrites { get; } = [];

        public IReadOnlyList<TokenLedgerReceiptSource> ScanJobs() => jobs;

        public IEnumerable<TokenLedgerBusSource> ReadBus(IEnumerable<string> projects) => [];

        public bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary)
        {
            Rewrites.Add(new TokenLedgerReceiptRewrite(folderPath, folderPath, summary));
            return true;
        }

        public void InvalidateCaches()
        {
        }
    }
}
