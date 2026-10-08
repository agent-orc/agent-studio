using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2986: the workspace token timeline read only the token bus, so remote
/// runner usage (persisted as durable task receipts) never reached it and the
/// cockpit showed an empty week. These tests drive the timeline over one
/// merged ledger with local bus rows, remote coding receipts, remote review
/// receipts, and a remote chat turn, and pin the id/label contract.
/// </summary>
public sealed class RemoteUsageLedgerTimelineTests : IDisposable
{
    private const string Project = "studio";
    private const string TaskId = "AGT-1";
    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly DateTime _now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    public RemoteUsageLedgerTimelineTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "remote-usage-ledger-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Timeline_AggregatesLocalBusAndRemoteReceiptsTogether_WithHosts()
    {
        var (bridge, store, ledger) = BuildStack();
        var remoteCodingAt = _now.AddHours(-2);
        WriteReceipt(
            Call("agent:remote-runner:att-1", "claude-opus-5-5", "agent-runner-01", "claude",
                input: 1_000, output: 500, cacheRead: 100_000, cacheWrite: 2_000, remoteCodingAt),
            Call("support:remote-review:rev-1", "gpt-6-sol", "agent-runner-01", "codex",
                input: 3_000, output: 400, cacheRead: 7_000, cacheWrite: 0, _now.AddHours(-1)),
            // Remote receipt written before host attribution existed.
            Call("agent:remote-runner:att-0", "claude-opus-5", host: null, cli: null,
                input: 10, output: 10, cacheRead: 0, cacheWrite: 0, _now.AddHours(-3)));

        // Local workstation coding turn: legacy bus row without host.
        await EmitAsync(bridge, store, "agent:claude", TaskId,
            Usage("claude-sonnet-5", 50_000, 5_000, cacheRead: 0), _now.AddHours(-4));
        // Remote project-chat turn written by the chat path.
        await EmitAsync(bridge, store, AgentMessageBusBridge.ParticipantOrchestratorFor(Project), null,
            Usage("gpt-6-sol", 2_000, 100, cacheRead: 1_000) with
            {
                Host = "agent-runner-02",
                CliType = "codex",
                InputIncludesCached = true,
            },
            _now.AddMinutes(-30));

        var timeline = BusBackedWorkspaceTimelineReader.BuildFromLedger(
            ledger, [(Project, _watchPath)], windowHours: 24, bucketMinutes: 60, nowUtc: _now);

        var project = Assert.Single(timeline.Projects);
        Assert.Equal(5, project.Calls);
        // Cached input stays in its own dimension; input is the uncached share.
        Assert.Equal(1_000 + 3_000 + 10 + 50_000 + 2_000, project.Input);
        Assert.Equal(100_000 + 7_000 + 1_000, project.CacheRead);
        Assert.Equal(project.Input + project.Output + project.CacheRead + project.CacheWrite, project.Total);
        Assert.Equal(project.Total, timeline.Cells.Sum(cell => cell.Total));
        Assert.Equal(project.Total, timeline.Models.Sum(model => model.Total));
        Assert.Equal(project.Total, project.Hosts.Sum(host => host.Total));

        Assert.Equal(
            new[] { "agent-runner-01", "local", "agent-runner-02", TokenUsageHost.UnrecordedRemote },
            project.Hosts.Select(host => host.Host).ToArray());

        var remoteOpus = Assert.Single(timeline.Models, row => row.Model == "claude-opus-5-5");
        Assert.Equal("agent-runner-01", remoteOpus.Host);
        Assert.Equal(["claude"], remoteOpus.CliTypes);
        Assert.Equal(103_500, remoteOpus.Total);

        var solRows = timeline.Models.Where(row => row.Model == "gpt-6-sol").ToList();
        Assert.Equal(["agent-runner-01", "agent-runner-02"], solRows.Select(row => row.Host).Order().ToArray());

        var local = Assert.Single(timeline.Models, row => row.Host == TokenUsageHost.Local);
        Assert.Equal("claude-sonnet-5", local.Model);
        Assert.Equal("Claude Sonnet 5", local.ModelLabel);

        Assert.Contains("task-token-receipts", timeline.Freshness.Sources);
        Assert.Contains("historical-token-bus", timeline.Freshness.Sources);
    }

    [Fact]
    public async Task Timeline_DoesNotDoubleCountAReceiptAlsoPresentOnTheBus()
    {
        var (bridge, store, ledger) = BuildStack();
        var at = _now.AddHours(-2);
        WriteReceipt(Call("agent:remote-runner:att-1", "gpt-6-sol", "agent-runner-01", "codex",
            input: 4_000, output: 300, cacheRead: 9_000, cacheWrite: 0, at));
        await EmitAsync(bridge, store, "agent:codex", TaskId,
            Usage("gpt-6-sol", 4_000, 300, cacheRead: 9_000) with
            {
                Host = "agent-runner-01",
                InputIncludesCached = true,
            }, at);

        var timeline = BusBackedWorkspaceTimelineReader.BuildFromLedger(
            ledger, [(Project, _watchPath)], windowHours: 24, bucketMinutes: 60, nowUtc: _now);

        var project = Assert.Single(timeline.Projects);
        Assert.Equal(1, project.Calls);
        Assert.Equal(13_300, project.Total);
    }

    [Fact]
    public void Timeline_UnknownModelId_RendersTheIdNotAnotherModelsLabel()
    {
        var entries = new List<OrchestratorLogEntry>
        {
            Entry("agent:remote-runner:a", "zeta-model-9", "agent-runner-01", _now.AddHours(-1)),
            Entry("agent:remote-runner:b", "claude-opus-5-5", "agent-runner-01", _now.AddHours(-1)),
        };

        var timeline = WorkspaceTokensTimelineService.BuildFromEntries(
            [(Project, entries)], _now.AddHours(-24), _now, 60);

        Assert.Equal("zeta-model-9", Assert.Single(timeline.Models, row => row.Model == "zeta-model-9").ModelLabel);
        Assert.Equal("Claude Opus 5.5", Assert.Single(timeline.Models, row => row.Model == "claude-opus-5-5").ModelLabel);
    }

    [Fact]
    public async Task Timeline_DeduplicatesChatFallbackReceiptAfterBusRecovery()
    {
        var (bridge, store, ledger) = BuildStack();
        var at = _now.AddHours(-1);
        var usage = Usage("gpt-6-sol", 2_000, 100, cacheRead: 1_000) with
        {
            Host = "agent-runner-02",
            CliType = "codex",
            InputIncludesCached = true,
        };
        await ChatUsageFallbackReceipts.WriteAsync(_workspace, Project, "turn-1", at, usage);
        await EmitAsync(bridge, store, AgentMessageBusBridge.ParticipantOrchestratorFor(Project),
            null, usage, at);

        var timeline = BusBackedWorkspaceTimelineReader.BuildFromLedger(
            ledger, [(Project, _watchPath)], 24, 60, _now);
        Assert.Equal(1, Assert.Single(timeline.Projects).Calls);
        Assert.Equal(3_100, Assert.Single(timeline.Models).Total);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5", "Claude Opus 5.5")]
    [InlineData("zeta-model-9", "zeta-model-9", "zeta-model-9")]
    [InlineData("claude-opus-5", "claude-opus-5", "Claude Opus 5")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5", "Claude Haiku 4.5")]
    [InlineData("claude-opus-4.8", "claude-opus-4-8", "Claude Opus 4.8")]
    // Historical receipts stored the label (AGT-2740); it still resolves.
    [InlineData("Claude Sonnet 5", "claude-sonnet-5", "Claude Sonnet 5")]
    public void ModelIdentity_StoresIdsAndResolvesLabelsOnlyForTheSameModel(
        string recorded, string storedId, string label)
    {
        Assert.Equal(storedId, TokenModelDisplay.StoredId(recorded));
        Assert.Equal(label, TokenModelDisplay.Label(TokenModelDisplay.StoredId(recorded)));
    }

    [Fact]
    public void CardSummary_KeepsTheObservedIdAndLabel_ForAnAliasedGeneration()
    {
        var summary = TokenSummaryService.SummarizePerJob(
        [
            Entry("agent:remote-runner:a", "claude-opus-5-5", "agent-runner-01", _now) with { JobId = TaskId },
        ])[TaskId];

        Assert.Equal("claude-opus-5-5", summary.LastModelId);
        Assert.Equal("Claude Opus 5.5", summary.LastModel);
        var call = Assert.Single(summary.Entries);
        Assert.Equal("claude-opus-5-5", call.Model);
        Assert.Equal("agent-runner-01", call.Host);
        Assert.Equal("claude", call.CliType);
    }

    [Fact]
    public void RemoteReviewAttempt_ProducesLedgerRowsWithHostCliAndLevel()
    {
        var head = new string('a', 40);
        var started = _now.AddMinutes(-10);
        var agentCommand = new Contract.ReviewCommandEvidenceDto(
            "aspect-security", "security", "/usr/local/bin/codex", [], head, head, new string('b', 40),
            started, started.AddMinutes(2), 0, null, "", "",
            ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
            Model: "gpt-6-sol",
            ThinkingLevel: "high",
            InputTokens: 3_000,
            OutputTokens: 400,
            CacheReadTokens: 7_000,
            InputIncludesCached: true);
        var toolCommand = agentCommand with
        {
            StepId = "build-test-gate",
            ExecutionKind = Contract.ReviewCommandKinds.Tool,
        };
        var report = new Contract.ReviewReportRequest(
            "agent-runner-01-review", "instance-1", "lease-1", 3, "key", "Pass", null, null,
            new Contract.ReviewWorkspaceProofDto(
                "example/repository", head, head, new string('b', 40), false, false, "workspace", "rev-1"),
            new Contract.ReviewEnvironmentDto(
                "agent-runner-01", "agent-runner-01-review", "instance-1", "linux", "x64", "10.0",
                new Dictionary<string, string>(), new Dictionary<string, string>(), null),
            [agentCommand, toolCommand],
            [],
            []);

        var calls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(ReviewAttempt(), report);

        var call = Assert.Single(calls);
        Assert.Equal("gpt-6-sol", call.Model);
        Assert.Equal("agent-runner-01", call.Host);
        Assert.Equal("codex", call.CliType);
        Assert.Equal("high", call.ThinkingLevel);
        Assert.Equal(3_000, call.InputTokens);
        Assert.Equal(7_000, call.CacheReadTokens);
        Assert.Equal(started.AddMinutes(2), call.Ts);
    }

    [Fact]
    public void RemoteChatTurn_ProducesALedgerRowWithTheRunnerAsHost()
    {
        var result = new OrchestratorDecisionResult(
            true,
            "reply",
            "gpt-6-sol",
            new OrchestratorTokenUsage { InputTokens = 2_000, OutputTokens = 100, CacheReadTokens = 1_000 },
            CapturedSessionId: null,
            ErrorMessage: null)
        {
            CliType = "codex",
        };

        var usage = OrchestratorChatService.BuildChatUsage(result, "agent-runner-02", "medium");

        Assert.NotNull(usage);
        Assert.Equal("gpt-6-sol", usage!.Model);
        Assert.Equal("agent-runner-02", usage.Host);
        Assert.Equal("codex", usage.CliType);
        Assert.Equal("medium", usage.ThinkingLevel);
        Assert.Null(OrchestratorChatService.BuildChatUsage(
            result with { TokenUsage = new OrchestratorTokenUsage() }, "local", null));
    }

    private (AgentMessageBusBridge Bridge, AgentMessageBusStore Store, BusBackedProjectTokenUsageReader Ledger) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        var generation = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, generation);
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        var ledger = new BusBackedProjectTokenUsageReader(store, config, stats, new ProjectTokenReceiptReader());
        return (bridge, store, ledger);
    }

    private void WriteReceipt(params TaskTokenCall[] calls)
    {
        var folder = Path.Combine(_watchPath, "tasks", "000", TaskId);
        Directory.CreateDirectory(folder);
        var summary = new TaskTokenSummary
        {
            Calls = calls.Length,
            InputTokens = calls.Sum(call => call.InputTokens),
            OutputTokens = calls.Sum(call => call.OutputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
            TotalTokens = calls.Sum(call => call.InputTokens + call.OutputTokens
                                            + call.CacheReadTokens + call.CacheCreationTokens),
            Entries = calls.ToList(),
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new { id = TaskId, title = "Remote task", tokenSummary = summary }));
    }

    private static TaskTokenCall Call(
        string participant, string model, string? host, string? cli,
        long input, long output, long cacheRead, long cacheWrite, DateTime ts) => new()
    {
        Ts = ts,
        Model = model,
        ParticipantId = participant,
        Host = host,
        CliType = cli,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheWrite,
    };

    private static OrchestratorLogEntry Entry(string participant, string model, string host, DateTime ts) => new()
    {
        Ts = ts,
        Kind = OrchestratorLogKinds.Observation,
        Topic = "remote-task-token-receipt",
        Summary = "",
        ParticipantId = participant,
        TokenUsage = new OrchestratorTokenUsage
        {
            Model = model,
            InputTokens = 100,
            OutputTokens = 10,
            Host = host,
            CliType = "claude",
        },
    };

    private static OrchestratorTokenUsage Usage(string model, int input, int output, int cacheRead) => new()
    {
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
    };

    private async Task EmitAsync(
        AgentMessageBusBridge bridge,
        AgentMessageBusStore store,
        string participantId,
        string? jobId,
        OrchestratorTokenUsage usage,
        DateTime ts)
    {
        var before = store.Query(_workspace, Project, new AgentMessageQuery(Kind: "token-usage")).Count;
        await bridge.EmitTokenUsageAsync(Project, jobId, participantId, "token", usage, createdAt: ts);
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (store.Query(_workspace, Project, new AgentMessageQuery(Kind: "token-usage")).Count > before) return;
            await Task.Delay(25);
        }
        Assert.Fail("The bus did not record the token-usage message.");
    }

    private static ReviewAttemptDto ReviewAttempt()
    {
        var created = new DateTime(2026, 9, 28, 11, 0, 0, DateTimeKind.Utc);
        return new ReviewAttemptDto(
            "rev-1",
            "PROJ-002::AGT-1",
            "example/repository",
            "att-1",
            null,
            new ReviewSubjectDto(
                "subject-1", "example/repository", new string('a', 40), "att-1",
                new string('d', 64), "policy-v1", [], created),
            AttemptLifecycleState.Leased,
            null, 3, 0, created, null, null, null, null, null, []);
    }
}
