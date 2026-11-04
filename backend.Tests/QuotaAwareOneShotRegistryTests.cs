using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using System.Text.Json;
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
    [InlineData("result-summary")]
    [InlineData("prompt-enrichment")]
    [InlineData("title-generation")]
    [InlineData("drift-post-step")]
    [InlineData("code-pattern-drift")]
    [InlineData("task-spawner")]
    [InlineData("failure-intervention")]
    [InlineData("watcher-analysis")]
    [InlineData("wiki-search")]
    [InlineData("proposal-drafting")]
    [InlineData("one-shot")]
    public async Task Capped_claude_routes_every_shared_one_shot_path_to_codex_once(string source)
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistryWithRecorder(claude, codex);
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
        Assert.Equal("quota-cap", result.QuotaAdmission?.ModelFallback?.Reason);
        Assert.Equal("Weekly", result.QuotaAdmission?.ModelFallback?.Window);
        Assert.Contains("TokenEconomy", result.QuotaAdmission?.ModelFallback?.CatalogueVersion);
        Assert.Empty(claude.Requests);
        var dispatched = Assert.Single(codex.Requests);
        Assert.Equal(CliTypes.Codex, dispatched.CliType);
        Assert.Equal(ModelIds.Gpt56Sol, dispatched.Model);
        Assert.Equal("high", dispatched.ThinkingLevel);
        Assert.Equal(CliTypes.Claude, configured.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, configured.Model);
        var ledger = new ModelSelectionLedger(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build());
        var selection = Assert.Single(ledger.Query(provider: CliTypes.Codex, project: "quota-test",
            consumer: source, rule: "quota-cap", from: ModelIds.ClaudeOpus5, to: ModelIds.Gpt56Sol).Events);
        Assert.Equal(CliTypes.Claude, selection.From?.CliType);
    }

    [Fact]
    public async Task Pinned_max_uses_a_supported_codex_equivalent_without_downgrading()
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

        Assert.True(result.Ok);
        Assert.False(result.QuotaDeferred);
        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, result.QuotaAdmission?.Outcome);
        Assert.Equal(CliTypes.Codex, result.EffectiveCliType);
        Assert.Equal(ModelIds.Gpt56Sol, result.EffectiveModel);
        Assert.Equal("max", result.EffectiveThinkingLevel);
        Assert.Empty(claude.Requests);
        var dispatched = Assert.Single(codex.Requests);
        Assert.Equal(ModelIds.Gpt56Sol, dispatched.Model);
        Assert.Equal("max", dispatched.ThinkingLevel);
    }

    [Fact]
    public async Task Capped_one_shot_waits_when_equivalent_cli_has_no_implementation()
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var registry = BuildRegistry(claude);

        var result = await registry.Require(CliTypes.Claude).RunAsync(new CliOneShotRequest(
            CliTypes.Claude, ModelIds.ClaudeOpus5, "bounded prompt")
        {
            Project = "quota-test",
            Source = "title-generation",
        });

        Assert.True(result.QuotaDeferred);
        Assert.NotNull(result.QuotaAdmission);
        Assert.False(result.QuotaAdmission.ShouldLaunch);
        Assert.Empty(claude.Requests);
        Assert.DoesNotContain("no one-shot implementation", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gemini_aspect_under_operator_switch_is_admitted_and_logged()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var clock = new FixedTimeProvider(_now);
        var preference = new CliFallbackPreferenceService(configuration, clock);
        preference.Set(CliTypes.Gemini, active: true, resetAt: _now.AddHours(1));
        var cache = new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance);
        var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], configuration, cache);
        var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, configuration);
        var fallback = new CliQuotaFallbackService(configuration, NullLogger<CliQuotaFallbackService>.Instance);
        var wait = new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, configuration);
        var projects = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        var ledger = new RecordingSelectionLedger();
        var selection = new ModelSelectionService(quota, caps, fallback, wait, projects,
            clock, preference: preference, ledger: ledger);
        var registry = new CliOneShotRegistry([], modelSelection: selection);
        var prompts = new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance);
        var aspect = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance,
            oneShotRegistry: registry, configuration: configuration)
        {
            VerdictRetryBackoff = _ => TimeSpan.Zero,
        };
        aspect.CliRunner = (_, _, _, _, _, _) =>
            throw new InvalidOperationException("operator switch should defer before direct CLI dispatch");

        var jobFolder = Path.Combine(_root, "aspect-job");
        Directory.CreateDirectory(jobFolder);
        var deferredEnvelope = await aspect.RunViaOneShotAsync(
            "requirement-fit", CliTypes.Gemini, "gemini-review-model", "review this change",
            null, TimeSpan.FromSeconds(2), jobFolder, "quota-test", "aspect-job",
            CancellationToken.None);
        using (var deferred = JsonDocument.Parse(deferredEnvelope))
        {
            Assert.True(deferred.RootElement.GetProperty("quotaDeferred").GetBoolean());
            Assert.Contains("[quota-deferred]",
                deferred.RootElement.GetProperty("error").GetString());
        }
        var report = await aspect.RunAsync(new AspectRunInputs(
                Project: "quota-test", JobId: "aspect-job", JobTitle: "Review a change",
                JobFolderPath: jobFolder, TaskBody: "Review this change.", RecentLog: "",
                DiffSummary: "One file changed.", StatusSummary: "Done."),
            ["requirement-fit"], CliTypes.Gemini, "gemini-review-model",
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(report.HasQuotaDeferred);
        Assert.False(report.HasInfraFailure);

        var decision = registry.SelectDirect(new CliOneShotRequest(
            CliTypes.Gemini, "gemini-review-model", "review this change")
        {
            StepId = "aspect-requirement-fit",
            Project = "quota-test",
        });

        Assert.NotNull(decision);
        Assert.False(decision.Admission.ShouldLaunch);
        Assert.Equal(SelectionRules.OperatorSwitch, decision.Result.Rule);
        Assert.Contains(ledger.Entries, entry =>
            entry.Consumer == SelectionConsumers.ReviewAspect
            && entry.Kind == SelectionLedgerKinds.Waiting
            && entry.Project == "quota-test"
            && entry.TaskKey == "aspect-job");

        preference.Set(CliTypes.Gemini, active: false, resetAt: null);
        aspect.CliRunner = (_, _, _, _, _, _) =>
            Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=Looks fine.]]");
        var resumed = await aspect.RunAsync(new AspectRunInputs(
                Project: "quota-test", JobId: "aspect-job", JobTitle: "Review a change",
                JobFolderPath: jobFolder, TaskBody: "Review this change.", RecentLog: "",
                DiffSummary: "One file changed.", StatusSummary: "Done."),
            ["requirement-fit"], CliTypes.Gemini, "gemini-review-model",
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.False(resumed.HasQuotaDeferred);
        Assert.Equal(AspectStatus.Pass, Assert.Single(resumed.Verdicts).Status);
    }

    [Fact]
    public async Task Orchestrator_entry_point_uses_canonical_admission_before_dispatch()
    {
        var claude = new RecordingOneShot(CliTypes.Claude);
        var codex = new RecordingOneShot(CliTypes.Codex);
        var registry = BuildRegistry(claude, codex);
        var runner = new OrchestratorRunner(
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
        var activated = Assert.Single(timeline, item => item.Kind == TimelineEventKinds.QuotaFallbackActivated);
        Assert.Contains("catalogueVersion", activated.Details!["modelFallback"]);
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
            new ModelEquivalenceCatalog(TokenTestServices.Pricing()));
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
                NullLogger<QuotaAdmissionRecorder>.Instance,
                new ModelSelectionLedger(configuration));
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

    // MS-5: the registry dispatch asks the selection service once per call.
    [Theory]
    [InlineData("aspect-requirement-fit", "review-aspect", true)]
    [InlineData("post-code-review-grade", "review-grade", true)]
    [InlineData("title-generation", "one-shot:title-generation", false)]
    public async Task One_shot_dispatch_asks_the_model_selection_service(
        string source, string consumer, bool sameClassOnly)
    {
        var spy = new SpyModelSelectionService();
        var claude = new RecordingOneShot(CliTypes.Claude);
        var registry = new CliOneShotRegistry([claude], modelSelection: spy);
        var request = new CliOneShotRequest(CliTypes.Claude, ModelIds.ClaudeOpus5, "bounded prompt")
        {
            ThinkingLevel = "high",
            Project = "quota-test",
            StepId = source,
        };

        var result = await registry.Require(CliTypes.Claude).RunAsync(request);

        Assert.True(result.Ok);
        var call = Assert.Single(spy.Calls);
        Assert.Equal(source, call.ExecutionPath);
        Assert.Equal(consumer, call.Request.Consumer);
        Assert.Equal(sameClassOnly, call.Request.Required.SameClassOnly);
        Assert.Equal("quota-test", call.Request.Project);
        Assert.Equal(source, call.Request.StepId);
        Assert.Equal(
            new AgentStudio.TaskServer.Contracts.SelectionPins(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"),
            call.Request.Pins);
        Assert.Single(claude.Requests);
    }

    [Theory]
    [InlineData("title-generation", "title")]
    [InlineData("periodic-tag-maintenance", "tags")]
    public async Task One_shot_uses_consumer_class_even_with_a_specific_step_id(string step, string kind)
    {
        var spy = new SpyModelSelectionService();
        var registry = new CliOneShotRegistry([new RecordingOneShot(CliTypes.Claude)], modelSelection: spy);

        var result = await registry.Require(CliTypes.Claude).RunAsync(new CliOneShotRequest(
            CliTypes.Claude, ModelIds.ClaudeOpus5, "bounded prompt")
        {
            StepId = step,
            SelectionConsumer = SelectionConsumers.OneShot(kind),
        });

        Assert.True(result.Ok);
        Assert.Equal(SelectionConsumers.OneShot(kind), Assert.Single(spy.Calls).Request.Consumer);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class RecordingSelectionLedger : IModelSelectionLedger
    {
        public List<SelectionLedgerEntry> Entries { get; } = [];
        public void Record(SelectionLedgerEntry entry) => Entries.Add(entry);
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
