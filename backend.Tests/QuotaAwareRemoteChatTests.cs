using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Review;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaAwareRemoteChatTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "quota-remote-chat-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Remote_project_chat_claim_carries_resolved_family_and_configured_provenance()
    {
        const string projectName = "quota-remote-chat";
        const string runnerId = "runner-01";
        var watchPath = Path.Combine(_root, "projects", projectName);
        var repositoryPath = Path.Combine(_root, "repository");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(repositoryPath);
        var now = new DateTime(2026, 9, 8, 7, 0, 0, DateTimeKind.Utc);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = repositoryPath,
                ["WatchPaths:0:RepositoryPath"] = repositoryPath,
            })
            .Build();
        var quotaStore = new QuotaCacheStore(
            configuration,
            NullLogger<QuotaCacheStore>.Instance);
        quotaStore.Write([
            Snapshot(CliTypes.Codex, 100, now),
            Snapshot(CliTypes.Claude, 20, now),
        ]);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance,
            [],
            configuration,
            quotaStore);
        var admission = new QuotaAdmissionService(
            quota,
            new CliQuotaCapsService(
                NullLogger<CliQuotaCapsService>.Instance,
                configuration),
            new CliQuotaFallbackService(
                configuration,
                NullLogger<CliQuotaFallbackService>.Instance,
                new ModelEquivalenceCatalog()),
            new CliQuotaWaitPolicyService(
                NullLogger<CliQuotaWaitPolicyService>.Instance,
                configuration),
            new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance,
                configuration),
            new FixedTimeProvider(now));
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            configuration);
        settings.SetExecutionRunner(projectName, runnerId, remoteExecutionEnabled: true);
        var projects = new ProjectRegistry(
            configuration,
            NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(watchPath, projectName, "default");
        projects.AddUrl(project.Id, "repo", "https://example.invalid/quota-remote-chat.git");
        var summaries = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summaries);
        var runner = new UnexpectedLocalRunner();
        var sessionStore = new GlobalOrchestratorSessionStore(
            configuration,
            NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore,
            runner,
            scanner,
            configuration);
        var broker = new RemoteChatWorkBroker(
            NullLogger<RemoteChatWorkBroker>.Instance);
        var service = new OrchestratorChatService(
            new OrchestratorChat(NullLogger<OrchestratorChat>.Instance),
            runner,
            sessionStore,
            bootstrap,
            scanner,
            configuration,
            NullLogger<OrchestratorChatService>.Instance,
            projectSettings: settings,
            projects: projects,
            remoteWork: broker,
            quotaAdmission: admission);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var send = service.SendAsync(
            projectName,
            watchPath,
            new SendOrchestratorChatRequest(
                "Inspect quota routing.",
                Attachments: null,
                Model: ModelIds.Gpt56Sol,
                ThinkingLevel: "high"),
            timeout.Token);
        RemoteChatWorkClaimResponse claim;
        do
        {
            claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
                runnerId, "runner-01", "host-01"));
            if (claim.Status == RemoteChatWorkClaimStatuses.Empty)
                await Task.Delay(10, timeout.Token);
        } while (claim.Status == RemoteChatWorkClaimStatuses.Empty);

        Assert.NotNull(claim.Work);
        Assert.Equal(CliTypes.Claude, claim.Work!.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, claim.Work.Model);
        Assert.Equal("high", claim.Work.ThinkingLevel);
        Assert.Equal(CliTypes.Codex, claim.Work.ConfiguredCliType);
        Assert.Equal(ModelIds.Gpt56Sol, claim.Work.ConfiguredModel);
        Assert.Contains("codex", claim.Work.QuotaFallbackReason);

        Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
            claim.Work.WorkId,
            claim.Work.ClaimToken,
            runnerId,
            true,
            "resolved reply",
            claim.Work.Model,
            null,
            null,
            new ChatExecutionContext(
                "remote", "host-01", "/runner/repo", "main", "abc", "ready", now))));
        var reply = await send;

        Assert.Equal("resolved reply", reply.Text);
        Assert.Equal(CliTypes.Claude, reply.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, reply.Model);
        Assert.Equal(ModelIds.Gpt56Sol, reply.ConfiguredModel);
        Assert.Equal(claim.Work.QuotaFallbackReason, reply.QuotaFallbackReason);
        Assert.False(runner.WasCalled);
    }

    private static QuotaSnapshot Snapshot(string cliType, double usedPct, DateTime now) => new()
    {
        CliType = cliType,
        FetchedAt = now,
        Windows =
        [
            new QuotaWindow
            {
                Label = "Weekly",
                UsedPct = usedPct,
                ResetAt = now.AddHours(4),
            },
        ],
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class UnexpectedLocalRunner : OrchestratorRunner
    {
        public UnexpectedLocalRunner()
            : base(null!, NullLogger<OrchestratorRunner>.Instance)
        {
        }

        public bool WasCalled { get; private set; }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt,
            string model,
            string? thinkingLevel,
            string workingDirectory,
            CancellationToken ct = default,
            string? projectName = null,
            string? watchPath = null)
        {
            WasCalled = true;
            throw new InvalidOperationException("Remote chat unexpectedly used the local runner.");
        }
    }
}
