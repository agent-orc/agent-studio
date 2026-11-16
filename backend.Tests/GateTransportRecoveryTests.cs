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
    }

    /// <summary>The project repository with one integration branch: develop, or main without a develop line.</summary>
    private void SeedRepository(string integrationBranch)
    {
        Git(_root, "init", "-q", "-b", integrationBranch, _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Gate Transport Recovery Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task Card_escalated_by_the_transport_returns_to_its_lane_once_the_host_answers()
    {
        var stack = Build(Shape.Develop);
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
        Assert.Equal(0, stack.Gate.GreenRuns);
        Assert.Equal(TaskStates.Escalated, Card(stack, transport).State);

        // The transport answers again.
        stack.Gate.Refusing = false;
        stack.HostHealth.Reachable();
        await stack.Retries.RunOnceAsync();

        var returned = Card(stack, transport);
        Assert.True(returned.State == TaskStates.AutoReview,
            $"lane {returned.State}; log: {string.Join(" | ", stack.Log.Lines)}");
        Assert.Equal(LifecyclePhases.Integrating, returned.Phase);
        var events = stack.Timeline.ReadAll(returned.FolderPath);
        Assert.True(stack.Gate.GreenRuns == 1,
            $"gate runs {stack.Gate.GreenRuns}; timeline: {string.Join(" | ", events.Select(entry => entry.Kind + ": " + entry.Summary))}");
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

    /// <summary>
    /// The two live cards of 15 November 2026, each through its own gate path:
    /// AGT-3403 (batch gate enabled, the per-card merge gate into develop) and
    /// QS-136 (batch gate disabled, integration branch main, the pre-main full
    /// suite). The real merge runner meets the refusing gate host, the delivery
    /// chain escalates the card, and once the host answers the sweep replays
    /// the integration with the passed review and returns the card.
    /// </summary>
    [Theory]
    [InlineData(Shape.Develop)]
    [InlineData(Shape.Main)]
    public async Task Live_transport_escalation_returns_automatically_once_the_host_answers(Shape shape)
    {
        var stack = Build(shape);
        var id = SeedDelivery(stack, "live");

        await MeetTheRefusingHostAsync(stack, id, shape);
        Assert.NotNull(stack.HostHealth.Current);
        await stack.Reconciler.RunOnceAsync();
        var escalated = Card(stack, id);
        Assert.Equal(TaskStates.Escalated, escalated.State);
        Assert.Equal(GateFailureParkCategories.EnvironmentExhausted, escalated.ParkedBlocker?.BlockerType);
        Assert.Contains("repair the gate transport", escalated.ParkedBlocker!.Reason, StringComparison.Ordinal);

        // While the host refuses, nothing replays and nothing moves.
        await stack.Retries.RunOnceAsync();
        Assert.Equal(0, stack.Gate.GreenRuns);
        Assert.Equal(TaskStates.Escalated, Card(stack, id).State);

        stack.Gate.Refusing = false;
        stack.HostHealth.Reachable();
        await stack.Retries.RunOnceAsync();

        var returned = Card(stack, id);
        var events = stack.Timeline.ReadAll(returned.FolderPath);
        Assert.True(returned.State == TaskStates.AutoReview,
            $"lane {returned.State}; log: {string.Join(" | ", stack.Log.Lines)}; timeline: "
            + string.Join(" | ", events.Select(entry => entry.Kind + ": " + entry.Summary)));
        Assert.Equal(1, stack.Gate.GreenRuns);
        var note = events.Last(entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
        Assert.Equal(GateEnvironmentRetrySources.TransportRecovered, note.Details?.GetValueOrDefault("source"));
        Assert.Contains(events, entry => entry.Kind == TimelineEventKinds.IntegrationSucceeded);

        await stack.Reconciler.RunOnceAsync();
        Assert.Equal(TaskStates.HumanReview, Card(stack, id).State);
    }

    /// <summary>
    /// AGT-3403: Retry integration answered 409 not-a-gate-environment-failure
    /// with latestAttempt null. The ladder left transport failures out of
    /// "gate environment failure" altogether, so the operator's retry was
    /// refused before the passed review was even read. The transport is a gate
    /// environment failure; only the timed ladder leaves it to the host probe.
    /// </summary>
    [Fact]
    public async Task Operator_retry_replays_a_transport_escalation_with_its_passed_review()
    {
        var stack = Build(Shape.Develop);
        var id = SeedDelivery(stack, "operator");
        await MeetTheRefusingHostAsync(stack, id, Shape.Develop);
        await stack.Reconciler.RunOnceAsync();
        var escalated = Card(stack, id);
        Assert.Equal(TaskStates.Escalated, escalated.State);

        var status = stack.Integration.BuildLookup([escalated]).GetValueOrDefault(escalated.TaskKey);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, status?.Failure?.Code);
        Assert.True(GateEnvironmentRetryFactsReader.IsTransportFailure(status));
        // The timed ladder still leaves the transport to the host probe.
        var ladder = stack.Retries.Evaluate(escalated, status, stack.Retries.Options).Facts;
        Assert.Equal(GateEnvironmentRetryReasons.TransportAwaitsGateHost,
            GateEnvironmentRetryPolicy.Decide(ladder).Reason);

        stack.Gate.Refusing = false;
        stack.HostHealth.Reachable();
        var retried = await stack.Retries.RetryNowAsync(escalated);

        Assert.True(retried.Status == GateEnvironmentRetryStatus.Replayed,
            $"{retried.Status} {retried.Code}: {retried.Reason} latest={retried.LatestAttempt}");
        Assert.True(retried.Outcome?.IsSuccessfulIntegration());
        var returned = Card(stack, id);
        Assert.Equal(TaskStates.AutoReview, returned.State);
        var note = stack.Timeline.ReadAll(returned.FolderPath)
            .Last(entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
        Assert.Equal(GateEnvironmentRetrySources.Operator, note.Details?.GetValueOrDefault("source"));
        Assert.Contains("operator retried the integration", note.Summary, StringComparison.OrdinalIgnoreCase);

        await stack.Reconciler.RunOnceAsync();
        Assert.Equal(TaskStates.HumanReview, Card(stack, id).State);
    }

    [Fact]
    public async Task Refused_operator_retry_names_the_latest_review_of_the_delivery()
    {
        var stack = Build(Shape.Develop);
        var id = SeedDelivery(stack, "refused");
        var card = Card(stack, id);

        // No integration failure at all: the retry is refused, but the answer
        // still names the passed review of the compared delivery.
        var refused = await stack.Retries.RetryNowAsync(card);

        Assert.Equal(GateEnvironmentRetryReasons.NotAGateEnvironmentFailure, refused.Code);
        Assert.NotNull(refused.ComparedDeliverySha);
        Assert.Equal(ReviewTerminalOutcome.Pass, refused.LatestAttempt?.Outcome);
    }

    /// <summary>The merge into the integration branch, through the real runner, while the gate host refuses.</summary>
    private async Task MeetTheRefusingHostAsync(Stack stack, string id, Shape shape)
    {
        var card = stack.Scanner.FindJob(id, stack.WatchPath)!;
        var result = await stack.Runner.RunAsync(Project, id, card.FolderPath, card.WatchPath,
            shape == Shape.Main ? "main" : "develop", CancellationToken.None);

        // Item 6: every gate path records the transport as a gate environment failure.
        Assert.Equal(MergeIntoIntegrationOutcome.GateEnvironmentFailure, result.Outcome);
        Assert.Contains("Remote gate transport failure", result.Error, StringComparison.Ordinal);
        var step = stack.Integration.ReadLatestMergeStep(Card(stack, id))!;
        Assert.Equal("gate-environment-failure", step.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, step.FailureCode);
        Assert.Contains(shape == Shape.Main ? "Pre-main full suite blocked the merge" : "The build gate blocked the merge",
            step.Reason, StringComparison.Ordinal);
    }

    /// <summary>A reviewed delivery in the integration phase of Auto Review, before its merge ran.</summary>
    private string SeedDelivery(Stack stack, string label)
    {
        var (id, folder, key, deliverySha) = SeedCard(stack, label);
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = key,
            RunAttemptId = "run-" + key,
            Project = Project,
            Repository = _repo,
            ResultSha = deliverySha,
            ResultRef = "task/" + id,
            AttemptChainId = "chain-" + key,
            IntegrationBranch = stack.IntegrationBranch,
            CompletedAtUtc = Now.AddMinutes(-30),
        });
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        SeedPassedReview(stack, stack.Scanner.FindJob(id, _watchPath)!.Key ?? key, deliverySha, id);
        stack.Scanner.InvalidateCache();
        return id;
    }

    /// <summary>A coding card in Auto Review whose delivery commit sits on its task branch.</summary>
    private (string Id, string Folder, string Key, string DeliverySha) SeedCard(Stack stack, string label)
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
        Git(_repo, "checkout", "-q", "-b", "task/" + id, stack.IntegrationBranch);
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", key + ".cs"), $"// {key}\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + key);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", stack.IntegrationBranch);

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
        return (id, folder, key, deliverySha);
    }

    private string SeedFailedMergeGate(Stack stack, string label, string fixture, string failureCode)
    {
        var (id, folder, key, deliverySha) = SeedCard(stack, label);
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

    /// <summary>The two gate paths of the live cards.</summary>
    public enum Shape
    {
        /// <summary>AGT-3403: batch gate enabled, the per-card merge gate into develop.</summary>
        Develop,

        /// <summary>QS-136: batch gate disabled, integration branch main, the pre-main full suite.</summary>
        Main,
    }

    private Stack Build(Shape shape)
    {
        var integrationBranch = shape == Shape.Main ? "main" : "develop";
        SeedRepository(integrationBranch);
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
        settings.SetIntegrationBranch(Project, integrationBranch);
        settings.SetBatchGate(Project, new BatchGateFormationOptions(Enabled: shape == Shape.Develop));
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
        var hostHealth = new GateHostOutageMonitor(
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(HostAnswersAt));
        var gate = new SwitchGate(new RemoteBuildTestGateRunner(new RefusingTransport(),
            NullLogger<RemoteBuildTestGateRunner>.Instance, hostHealth: hostHealth));
        var runner = new MergeIntoDevelopRunner(git, pipeline, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings, preDevelopBuildGate: new PreDevelopBuildGate(gate),
            preMainTestGate: new PreMainTestGate(gate));
        var authority = new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance);
        var log = new LineLogger<GateEnvironmentRetryService>();
        var retries = new GateEnvironmentRetryService(scanner, settings, integration, pipeline, runner, timeline,
            authority, new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance),
            configuration, log,
            transitions: transitions, hostHealth: hostHealth);
        return new Stack(scanner, mutations, timeline, pipeline, authority, gate, reconciler, retries, hostHealth, log,
            integration, runner, integrationBranch, _watchPath);
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
        SwitchGate Gate,
        DeliveryChainReconciler Reconciler,
        GateEnvironmentRetryService Retries,
        GateHostOutageMonitor HostHealth,
        LineLogger<GateEnvironmentRetryService> Log,
        TaskIntegrationStatusService Integration,
        MergeIntoDevelopRunner Runner,
        string IntegrationBranch,
        string WatchPath);

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

    /// <summary>
    /// The merge gate: the remote runner against a refusing gate host while
    /// <see cref="Refusing"/>, then the replayed gate that passes the full suite.
    /// </summary>
    private sealed class SwitchGate(IBuildTestGateRunner refused) : IBuildTestGateRunner
    {
        public bool Refusing { get; set; } = true;
        public int GreenRuns { get; private set; }

        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode, TimeSpan timeout,
            CancellationToken ct)
        {
            if (Refusing) return refused.RunAsync(request, changedFiles, profile, mode, timeout, ct);
            GreenRuns++;
            return Task.FromResult(new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 5, string.Empty,
                "replayed gate passed", true, false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
                TestSelection = new TestSelectionAudit
                {
                    Level = TestExecutionLevels.Full,
                    FullSuiteRequired = true,
                    FullSuiteRan = true,
                },
            });
        }
    }

    /// <summary>The gate host of 15 November 2026: every SSH connection is refused.</summary>
    private sealed class RefusingTransport : IRemoteGateTransport
    {
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode, TimeSpan timeout,
            CancellationToken ct)
            => Task.FromException<BuildTestGateResult>(new IOException(
                "ssh exited 255: ssh: connect to host 172.30.83.1 port 12222: Connection refused"));
    }
}
