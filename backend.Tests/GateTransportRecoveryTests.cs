using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3792: on 15 November 2026 the merge gates of AGT-3403 and QS-136 met the
/// unreachable gate host and were escalated as <c>gate-environment-exhausted</c>,
/// "repair the gate transport, then retry the gate". Nothing retried them when
/// the transport returned. The real delivery chain escalates them here; once
/// the gate host answers, the ladder sweep replays the integration and returns
/// the card to its lane. A card escalated by a red gate stays where it is.
/// </summary>
public sealed class GateTransportRecoveryTests : IDisposable
{
    private const string Project = "Fixture";

    /// <summary>A fixed instant for the seeded evidence: the outcome must not depend on the day the test runs.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 13, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The gate host clock. The product stamps lane changes itself, so the
    /// host answers at a fixed instant later than any of them.
    /// </summary>
    private static readonly DateTimeOffset HostAnswersAt = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public GateTransportRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-transport-recovery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Gate Transport Recovery Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task Card_escalated_by_the_transport_returns_to_its_lane_once_the_host_answers()
    {
        var stack = Build();
        var transport = SeedFailedMergeGate(stack, "transport", "environment-transport.derived.log",
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure);
        var red = SeedFailedMergeGate(stack, "red", "product.AGT-2722.log",
            AcceptedIntegrationFailureCodes.BuildGateFailed);
        // The gate host refuses: three attempts of one kind mark it unreachable.
        for (var attempt = 0; attempt < 3; attempt++)
            stack.HostHealth.TransportFailed("ssh: connect to host 172.30.83.1 port 12222: Connection refused");
        Assert.NotNull(stack.HostHealth.Current);

        await stack.Reconciler.RunOnceAsync();

        var escalated = Card(stack, transport);
        Assert.Equal(TaskStates.Escalated, escalated.State);
        Assert.Equal(GateFailureParkCategories.EnvironmentExhausted, escalated.ParkedBlocker?.BlockerType);
        Assert.Contains("repair the gate transport", escalated.ParkedBlocker!.Reason, StringComparison.Ordinal);
        Assert.Equal(TaskStates.Escalated, Card(stack, red).State);
        Assert.Equal(GateFailureParkCategories.Product, Card(stack, red).ParkedBlocker?.BlockerType);

        // While the host is unreachable the sweep neither replays nor moves anything.
        await stack.Retries.RunOnceAsync();
        Assert.Equal(0, stack.Gate.Invocations);
        Assert.Equal(TaskStates.Escalated, Card(stack, transport).State);

        // The transport answers again.
        stack.HostHealth.Reachable();
        await stack.Retries.RunOnceAsync();

        var returned = Card(stack, transport);
        Assert.True(returned.State == TaskStates.AutoReview,
            $"lane {returned.State}; log: {string.Join(" | ", stack.Log.Lines)}");
        Assert.Equal(LifecyclePhases.Integrating, returned.Phase);
        var events = stack.Timeline.ReadAll(returned.FolderPath);
        Assert.True(stack.Gate.Invocations == 1,
            $"gate runs {stack.Gate.Invocations}; timeline: {string.Join(" | ", events.Select(entry => entry.Kind + ": " + entry.Summary))}");
        var note = events.Last(entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
        Assert.Equal(GateEnvironmentRetrySources.TransportRecovered, note.Details?.GetValueOrDefault("source"));
        Assert.Equal(TaskStates.AutoReview, note.Details?.GetValueOrDefault("returnedTo"));
        Assert.Contains("gate transport answers again", note.Summary, StringComparison.Ordinal);
        Assert.Contains(events, entry => entry.Kind == TimelineEventKinds.IntegrationGateEnvironmentRetried
            && entry.Details?.GetValueOrDefault(GateEnvironmentRetryReceipts.SourceKey)
                == GateEnvironmentRetrySources.TransportRecovered);
        Assert.Contains(events, entry => entry.Kind == TimelineEventKinds.IntegrationSucceeded);

        // The red gate is no transport fault: its card stays escalated.
        Assert.Equal(TaskStates.Escalated, Card(stack, red).State);

        // The delivery chain takes the replayed verdict as usual.
        await stack.Reconciler.RunOnceAsync();
        Assert.Equal(TaskStates.HumanReview, Card(stack, transport).State);
    }

    private string SeedFailedMergeGate(Stack stack, string label, string fixture, string failureCode)
    {
        var id = stack.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = label,
            WatchPath = _watchPath,
            TargetState = TaskStates.AutoReview,
            PromptMarkdown = $"Implement {label}.",
        })!;
        Assert.NotNull(id);
        var created = stack.Scanner.FindJob(id, _watchPath)!;
        var key = created.Key ?? id;
        var folder = created.FolderPath;

        // The merge runner finds the delivery on the card's task branch.
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, key + ".txt"), key + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + key);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var taskJson = Path.Combine(folder, "task.json");
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(taskJson))!;
        void Set(string name, object value) => fields[name] = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Set("state", TaskStates.AutoReview);
        Set("phase", LifecyclePhases.Integrating);
        Set("mode", TaskModes.Coding);
        Set("projectName", Project);
        Set("commit", Commit(deliverySha));
        Set("commits", new[] { Commit(deliverySha) });
        File.WriteAllText(taskJson, JsonSerializer.Serialize(fields,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Directory.CreateDirectory(Path.Combine(folder, "post-steps"));
        File.WriteAllText(Path.Combine(folder, "post-steps", "pre-develop-build-gate-1.log"),
            GateFailureTriagePolicyTests.Fixture(fixture));
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = key,
            RunAttemptId = "run-" + key,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + id,
            AttemptChainId = "chain-" + key,
            IntegrationBranch = "develop",
            CompletedAtUtc = Now.AddMinutes(-30),
        });
        var failedAt = Now.UtcDateTime.AddMinutes(-20);
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        stack.Pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = failedAt,
            CompletedAt = failedAt,
            Verdict = failureCode == AcceptedIntegrationFailureCodes.BuildGateFailed
                ? "gate-failed" : "gate-environment-failure",
            Reason = "The build gate blocked the merge into develop: see post-steps/pre-develop-build-gate-1.log.",
            FailureCode = failureCode,
        });
        SeedPassedReview(stack, stack.Scanner.FindJob(id, _watchPath)!.Key ?? key, deliverySha, id);
        stack.Scanner.InvalidateCache();
        return id;
    }

    private static void SeedPassedReview(Stack stack, string taskKey, string deliverySha, string id)
    {
        var run = stack.Authority.AcquireRun(
            taskKey, "PROJ-001", null, "runner", "host", 600, "run-create-" + id).RunAttempt!;
        stack.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete-" + id),
            Outcome = "done",
            ResultSha = deliverySha,
        });
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-001", deliverySha, run.AttemptId,
            "requirements", "policy", [], "review-create-" + id)).ReviewAttempt!;
        var claimed = stack.Authority.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 600, "review-claim-" + id).ReviewAttempt!;
        var settled = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + id),
            deliverySha,
            ReviewTerminalOutcome.Pass));
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
    }

    private TaskInfo Card(Stack stack, string id)
    {
        stack.Scanner.InvalidateCache();
        return stack.Scanner.FindJob(id, _watchPath)!;
    }

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["DeliveryChain:Guarded"] = "true",
        }).Build();
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_watchPath, Project, DefaultWorkspace.Id);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance,
            integrationStatus: integration, configuration: configuration);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance, integrationStatus: integration,
            configuration: configuration, timeline: timeline);
        // No fix-round starter: the red gate parks with the product category.
        var router = new GateFailureRouter(integration, pipeline, settings, timeline, configuration,
            NullLogger<GateFailureRouter>.Instance);
        var reconciler = new DeliveryChainReconciler(scanner, integration, transitions, configuration,
            NullLogger<DeliveryChainReconciler>.Instance, router);
        var gate = new GreenGate();
        var runner = new MergeIntoDevelopRunner(git, pipeline, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings, preDevelopBuildGate: new PreDevelopBuildGate(gate));
        var authority = new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance);
        var hostHealth = new GateHostOutageMonitor(
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(HostAnswersAt));
        var log = new LineLogger<GateEnvironmentRetryService>();
        var retries = new GateEnvironmentRetryService(scanner, settings, integration, pipeline, runner, timeline,
            authority, new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance),
            configuration, log,
            transitions: transitions, hostHealth: hostHealth);
        return new Stack(scanner, mutations, timeline, pipeline, authority, gate, reconciler, retries, hostHealth, log);
    }

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = Now,
        attribution = "automatic",
        confidence = 1,
    };

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Gate transport recovery test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        TimelineLog Timeline,
        PipelineExecutionLog Pipeline,
        AttemptAuthorityService Authority,
        GreenGate Gate,
        DeliveryChainReconciler Reconciler,
        GateEnvironmentRetryService Retries,
        GateHostOutageMonitor HostHealth,
        LineLogger<GateEnvironmentRetryService> Log);

    /// <summary>The retry service's log lines, named in a failed assertion.</summary>
    private sealed class LineLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
    }

    /// <summary>The replayed merge gate once the transport answers.</summary>
    private sealed class GreenGate : IBuildTestGateRunner
    {
        public int Invocations { get; private set; }

        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode, TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 5, string.Empty,
                "replayed gate passed", true, false) { ExpectedSha = request.ExpectedSha });
        }
    }
}
