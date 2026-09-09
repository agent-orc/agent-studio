using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaAwareOneShotRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "quota-one-shot-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 9, 8, 7, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("pre-orchestrator-prep")]
    [InlineData("aspect-requirement-fit")]
    [InlineData("post-code-review-grade")]
    [InlineData("ui-visual-verdict")]
    [InlineData("orchestrator-chat")]
    [InlineData("one-shot")]
    public async Task Capped_claude_routes_every_shared_one_shot_path_to_codex_once(string source)
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistry(claude, codex);
        var configured = new CliOneShotRequest(
            CliTypes.Claude,
            ModelIds.ClaudeOpus5,
            "bounded prompt")
        {
            ThinkingLevel = "high",
            Project = "quota-test",
            Source = source,
        };

        var result = await registry.Require(CliTypes.Claude).RunAsync(configured);

        Assert.True(result.Ok);
        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, result.QuotaAdmission?.Outcome);
        Assert.Equal(CliTypes.Codex, result.EffectiveCliType);
        Assert.Equal(ModelIds.Gpt56Sol, result.EffectiveModel);
        Assert.Equal("high", result.EffectiveThinkingLevel);
        Assert.Empty(claude.Requests);
        var dispatched = Assert.Single(codex.Requests);
        Assert.Equal(CliTypes.Codex, dispatched.CliType);
        Assert.Equal(ModelIds.Gpt56Sol, dispatched.Model);
        Assert.Equal("high", dispatched.ThinkingLevel);
        Assert.Equal(CliTypes.Claude, configured.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, configured.Model);
    }

    [Fact]
    public async Task Unsupported_pinned_floor_waits_without_dispatch_or_fallback_cycle()
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistry(claude, codex);

        var result = await registry.Require(CliTypes.Claude).RunAsync(new CliOneShotRequest(
            CliTypes.Claude,
            ModelIds.ClaudeOpus5,
            "correctness-critical prompt")
        {
            ThinkingLevel = "max",
            Project = "quota-test",
            Source = "orchestrator-preparation",
        });

        Assert.False(result.Ok);
        Assert.True(result.QuotaDeferred);
        Assert.Equal(QuotaAdmissionOutcome.Wait, result.QuotaAdmission?.Outcome);
        Assert.Equal(CliTypes.Claude, result.EffectiveCliType);
        Assert.Equal(ModelIds.ClaudeOpus5, result.EffectiveModel);
        Assert.Equal("max", result.EffectiveThinkingLevel);
        Assert.Empty(claude.Requests);
        Assert.Empty(codex.Requests);
    }

    [Fact]
    public async Task Orchestrator_entry_point_uses_canonical_admission_before_dispatch()
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistry(claude, codex);
        var runner = new OrchestratorRunner(
            null!,
            NullLogger<OrchestratorRunner>.Instance,
            oneShotRegistry: registry);

        var result = await runner.DecideAsync(
            "Prepare a bounded orchestration decision.",
            ModelIds.ClaudeOpus5,
            _root);

        Assert.True(result.Success);
        Assert.Equal(CliTypes.Codex, result.CliType);
        Assert.Equal(ModelIds.Gpt56Sol, result.Model);
        Assert.True(result.QuotaFallback);
        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, result.QuotaAdmission?.Outcome);
        Assert.Empty(claude.Requests);
        Assert.Single(codex.Requests);
    }

    [Fact]
    public async Task Task_scoped_one_shot_records_timeline_and_feed_without_leaving_stale_marker()
    {
        var watchPath = Path.Combine(_root, "projects", "quota-test");
        var jobFolder = Path.Combine(watchPath, TaskStates.AutoReview, "AGT-2751");
        Directory.CreateDirectory(jobFolder);
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistryWithRecorder(claude, codex);

        var result = await registry.Require(CliTypes.Claude).RunAsync(
            new CliOneShotRequest(
                CliTypes.Claude,
                ModelIds.ClaudeOpus5,
                "Review the bounded aspect.")
            {
                ThinkingLevel = "high",
                Project = "quota-test",
                JobId = "AGT-2751",
                JobFolderPath = jobFolder,
                StepId = "aspect-requirement-fit",
            });

        Assert.True(result.Ok);
        Assert.Null(QuotaFallbackMarker.TryRead(jobFolder));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(jobFolder);
        Assert.Contains(timeline, item => item.Kind == TimelineEventKinds.QuotaAdmissionDecision);
        Assert.Contains(timeline, item => item.Kind == TimelineEventKinds.QuotaFallbackActivated);
        var feed = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance).Read(watchPath);
        Assert.Contains(feed, item => item.Topic == OrchestratorLogTopics.LoadDistribution);
    }

    private CliOneShotRegistry BuildRegistry(params RecordingOneShot[] implementations)
        => BuildRegistryCore(implementations, recordDecisions: false);

    private CliOneShotRegistry BuildRegistryWithRecorder(params RecordingOneShot[] implementations)
        => BuildRegistryCore(implementations, recordDecisions: true);

    private CliOneShotRegistry BuildRegistryCore(
        RecordingOneShot[] implementations,
        bool recordDecisions)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        var store = new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance);
        store.Write([
            Snapshot(CliTypes.Claude, usedPct: 100),
            Snapshot(CliTypes.Codex, usedPct: 20),
        ]);
        var quota = new QuotaService(
            NullLogger<QuotaService>.Instance,
            [],
            configuration,
            store);
        var caps = new CliQuotaCapsService(
            NullLogger<CliQuotaCapsService>.Instance,
            configuration);
        var fallback = new CliQuotaFallbackService(
            configuration,
            NullLogger<CliQuotaFallbackService>.Instance,
            new ModelEquivalenceCatalog());
        var wait = new CliQuotaWaitPolicyService(
            NullLogger<CliQuotaWaitPolicyService>.Instance,
            configuration);
        var projects = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            configuration);
        var admission = new QuotaAdmissionService(
            quota,
            caps,
            fallback,
            wait,
            projects,
            new FixedTimeProvider(_now));
        QuotaAdmissionRecorder? recorder = null;
        if (recordDecisions)
        {
            recorder = new QuotaAdmissionRecorder(
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance),
                new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
                NullLogger<QuotaAdmissionRecorder>.Instance);
        }
        return new CliOneShotRegistry(implementations, admission, recorder);
    }

    private QuotaSnapshot Snapshot(string cliType, double usedPct) => new()
    {
        CliType = cliType,
        FetchedAt = _now,
        Windows =
        [
            new QuotaWindow
            {
                Label = "Weekly",
                UsedPct = usedPct,
                ResetAt = _now.AddHours(4),
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

    private sealed class RecordingOneShot(string cliType) : ICliOneShot
    {
        public string CliType { get; } = cliType;
        public List<CliOneShotRequest> Requests { get; } = [];

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var at = new DateTime(2026, 9, 8, 7, 0, 1, DateTimeKind.Utc);
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: "ok",
                Stderr: "",
                Duration: TimeSpan.Zero,
                ParsedText: "ok",
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: at,
                    CompletedAt: at,
                    TotalMs: 0),
                Error: null));
        }
    }
}
