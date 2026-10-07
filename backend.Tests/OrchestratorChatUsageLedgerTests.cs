using AgentStudio.Cli;
using AgentStudio.Orchestrator;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2986 review fix: a project-chat turn's usage row is the only record the
/// workspace timeline has of that turn. The chat path must await the ledger
/// write before the turn returns and must report a write it could not make.
/// </summary>
public sealed class OrchestratorChatUsageLedgerTests : IDisposable
{
    private const string ProjectName = "chat-usage-ledger";
    private const string RunnerId = "agent-runner-02";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "chat-usage-ledger-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Remote_chat_turn_usage_is_in_the_ledger_when_the_turn_returns()
    {
        var harness = BuildHarness();

        var reply = await harness.SendRemoteTurnAsync(new OrchestratorTokenUsage
        {
            InputTokens = 2_000,
            OutputTokens = 100,
            CacheReadTokens = 1_000,
        });

        Assert.Equal("remote reply", reply.Text);
        // A fresh store reads the day files from disk, so this sees only rows
        // that were fully written before SendAsync completed.
        var rows = new AgentMessageBusStore().Query(
            _root, ProjectName, new AgentMessageQuery(Kind: "token-usage"));
        var row = Assert.Single(rows);
        Assert.Equal(OrchestratorChatService.OrchestratorChatUsageTopic, row.Topic);
        Assert.Equal(AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName), row.ParticipantId);
        Assert.NotNull(row.Tokens);
        Assert.Equal(RunnerId, row.Tokens!.Host);
        Assert.Equal(ModelIds.Gpt56Sol, row.Tokens.Model);
        Assert.Equal(CliTypes.Codex, row.Tokens.CliType);
        Assert.Equal("medium", row.Tokens.ThinkingLevel);
        Assert.Equal(2_000, row.Tokens.Input);
        Assert.Equal(1_000, row.Tokens.CacheRead);
        Assert.Empty(harness.Logger.Warnings);
    }

    [Fact]
    public async Task Failed_bus_write_preserves_the_chat_reply_and_usage_for_recovery()
    {
        var harness = BuildHarness();
        // A file where the project's bus directory belongs makes every ledger
        // append for this project fail.
        var busProjectDir = AgentMessageBusPaths.ProjectDir(_root, ProjectName);
        Directory.CreateDirectory(Path.GetDirectoryName(busProjectDir)!);
        await File.WriteAllTextAsync(busProjectDir, "not a directory");

        var reply = await harness.SendRemoteTurnAsync(new OrchestratorTokenUsage
        {
            InputTokens = 2_000,
            OutputTokens = 100,
        });

        Assert.Equal("remote reply", reply.Text);
        var persisted = Assert.Single(ChatUsageFallbackReceipts.Read(_root, ProjectName).Entries);
        Assert.Equal(RunnerId, persisted.TokenUsage?.Host);
        Assert.Equal(ModelIds.Gpt56Sol, persisted.TokenUsage?.Model);
        Assert.Equal(2_000, persisted.TokenUsage?.InputTokens);
        Assert.Equal(100, persisted.TokenUsage?.OutputTokens);
        // The bus source becomes readable again, as it would after repairing
        // a transient filesystem fault. A fresh reader must recover the row.
        File.Delete(busProjectDir);
        var scanner = new TaskScannerService(harness.Configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, harness.Configuration));
        var stats = new JobStatsMetadataCache(scanner, harness.Configuration,
            NullLogger<JobStatsMetadataCache>.Instance);
        var ledger = new BusBackedProjectTokenUsageReader(new AgentMessageBusStore(),
            harness.Configuration, stats, new ProjectTokenReceiptReader());
        var timeline = BusBackedWorkspaceTimelineReader.BuildFromLedger(
            ledger, [(ProjectName, WatchPath: harness.WatchPath)], 24, 60,
            nowUtc: reply.FinishedAt!.Value.AddHours(1));
        Assert.Equal(1, Assert.Single(timeline.Projects).Calls);
        Assert.Equal(2_100, Assert.Single(timeline.Models).Total);
        Assert.Contains("chat-usage-fallback", timeline.Freshness.Sources);
        var warning = Assert.Single(harness.Logger.Warnings);
        Assert.Contains("bus usage append failed", warning);
        Assert.Contains(ProjectName, warning);
        Assert.Contains(RunnerId, warning);
        Assert.Contains(ModelIds.Gpt56Sol, warning);
    }

    [Fact]
    public async Task Failed_remote_chat_turn_still_records_spent_tokens()
    {
        var harness = BuildHarness();

        var reply = await harness.SendRemoteTurnAsync(new OrchestratorTokenUsage
        {
            InputTokens = 2_000,
            OutputTokens = 100,
        }, success: false);

        Assert.NotNull(reply.ErrorMessage);
        var rows = new AgentMessageBusStore().Query(
            _root, ProjectName, new AgentMessageQuery(Kind: "token-usage"));
        var row = Assert.Single(rows);
        Assert.Equal(RunnerId, row.Tokens?.Host);
        Assert.Equal(2_000, row.Tokens?.Input);
        Assert.Equal(100, row.Tokens?.Output);
    }

    [Fact]
    public async Task Failed_bus_and_fallback_writes_fail_the_request_visibly()
    {
        var harness = BuildHarness();
        var busProjectDir = AgentMessageBusPaths.ProjectDir(_root, ProjectName);
        Directory.CreateDirectory(Path.GetDirectoryName(busProjectDir)!);
        await File.WriteAllTextAsync(busProjectDir, "not a directory");
        await File.WriteAllTextAsync(Path.Combine(_root, "chat-usage-fallback"), "not a directory");

        var error = await Assert.ThrowsAsync<IOException>(() => harness.SendRemoteTurnAsync(new OrchestratorTokenUsage
        {
            InputTokens = 100,
            OutputTokens = 10,
        }));
        Assert.Contains("could not be recovered", error.Message);
        Assert.Single(harness.Logger.Warnings);
    }

    private Harness BuildHarness()
    {
        var watchPath = Path.Combine(_root, "projects", ProjectName);
        var repositoryPath = Path.Combine(_root, "repository");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(repositoryPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = repositoryPath,
                ["WatchPaths:0:RepositoryPath"] = repositoryPath,
            })
            .Build();
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetExecutionRunner(ProjectName, RunnerId, remoteExecutionEnabled: true);
        var projects = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(watchPath, ProjectName, "default");
        projects.AddUrl(project.Id, "repo", "https://example.invalid/chat-usage-ledger.git");
        var summaries = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(
            configuration, NullLogger<TaskScannerService>.Instance, summaries);
        var runner = new UnexpectedLocalRunner();
        var sessionStore = new GlobalOrchestratorSessionStore(
            configuration, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore, runner, scanner, configuration);
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        var bus = new AgentMessageBusBridge(
            new AgentMessageBusStore(), configuration, NullLogger<AgentMessageBusBridge>.Instance);
        var logger = new CapturingLogger();
        var service = new OrchestratorChatService(
            new OrchestratorChat(NullLogger<OrchestratorChat>.Instance),
            runner, sessionStore, bootstrap, scanner, configuration, logger,
            projectSettings: settings, projects: projects, remoteWork: broker, bus: bus);
        return new Harness(service, broker, watchPath, logger, configuration);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed record Harness(
        OrchestratorChatService Service,
        RemoteChatWorkBroker Broker,
        string WatchPath,
        CapturingLogger Logger,
        IConfiguration Configuration)
    {
        public async Task<OrchestratorChatTurn> SendRemoteTurnAsync(
            OrchestratorTokenUsage usage, bool success = true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var send = Service.SendAsync(
                ProjectName, WatchPath,
                new SendOrchestratorChatRequest(
                    "Summarize the board.", Attachments: null,
                    Model: ModelIds.Gpt56Sol, ThinkingLevel: "medium"),
                timeout.Token);
            RemoteChatWorkClaimResponse claim;
            do
            {
                claim = Broker.TryClaim(new RemoteChatWorkClaimRequest(RunnerId, RunnerId, "host-02"));
                if (claim.Status == RemoteChatWorkClaimStatuses.Empty)
                    await Task.Delay(10, timeout.Token);
            } while (claim.Status == RemoteChatWorkClaimStatuses.Empty);

            Assert.True(Broker.Complete(new RemoteChatWorkCompletionRequest(
                claim.Work!.WorkId, claim.Work.ClaimToken, RunnerId,
                success, "remote reply", claim.Work.Model, usage,
                success ? null : "Remote model call failed.",
                new ChatExecutionContext(
                    "remote", "host-02", "/runner/repo", "main", "abc", "ready",
                    // clock-independent: context timestamp is provenance data, never compared with now.
                    new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc)),
                CliType: claim.Work.CliType)));
            return await send;
        }
    }

    private sealed class UnexpectedLocalRunner : OrchestratorRunner
    {
        public UnexpectedLocalRunner()
            : base(NullLogger<OrchestratorRunner>.Instance) { }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt, string model, string? thinkingLevel,
            string workingDirectory, CancellationToken ct = default,
            string? projectName = null, string? watchPath = null)
            => throw new InvalidOperationException("Remote chat unexpectedly used the local runner.");
    }

    private sealed class CapturingLogger : ILogger<OrchestratorChatService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
