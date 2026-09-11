using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class AcceptanceRailHostedServiceTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public AcceptanceRailHostedServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "acceptance-rail-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_root, "init", "-q", "-b", "develop", _repo);
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "Acceptance Rail Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task IntegratedCard_IsAutoAccepted()
    {
        var stack = Build();
        var integratedSha = Git(_repo, "rev-parse", "develop");
        SeedTask(stack, "integrated", integratedSha);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Accepted == 1, Describe(stack, snapshot));
        Assert.Equal(1, snapshot.HumanReviewDepth);
        Assert.NotNull(snapshot.LastRunAtUtc);
        Assert.Equal(TaskStates.Completed, stack.Scanner.FindJob("integrated", _watchPath)!.State);
        Assert.Contains(
            stack.Timeline.ReadAll(stack.Scanner.FindJob("integrated", _watchPath)!.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptanceRailActed
                     && entry.Details?.GetValueOrDefault("action") == "accepted");
    }

    [Fact]
    public async Task HoldCard_IsUntouched()
    {
        var stack = Build();
        var integratedSha = Git(_repo, "rev-parse", "develop");
        SeedTask(stack, "held", integratedSha, tags: [AcceptanceRailDefaults.OperatorHoldTag]);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.Equal(1, snapshot.Held);
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob("held", _watchPath)!.State);
    }

    [Fact]
    public async Task ConflictCard_IsRequeuedWithRebaseSteer()
    {
        var stack = Build();
        var deliverySha = CreateUnintegratedDelivery("conflict");
        SeedTask(stack, "conflict", deliverySha, conflict: true);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Requeued == 1, Describe(stack, snapshot));
        var queued = stack.Scanner.FindJob("conflict", _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        var prompt = File.ReadAllText(Path.Combine(queued.FolderPath, "prompt.md"));
        Assert.Contains("## STEER", prompt, StringComparison.Ordinal);
        Assert.Contains("origin/develop", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not redo the feature work", prompt, StringComparison.Ordinal);
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent!.Mode);
        Assert.Contains(
            stack.Timeline.ReadAll(queued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                     && entry.Details?.GetValueOrDefault("source")
                         == TaskIntegrationRecoveryService.AcceptanceRailSource
                     && entry.Details?.GetValueOrDefault("retryNumber") == "1");
    }

    [Fact]
    public async Task ConflictAtRetryLimit_IsEscalatedWithClearReason()
    {
        var stack = Build(maxRequeues: 1);
        var deliverySha = CreateUnintegratedDelivery("exhausted");
        var folder = SeedTask(stack, "exhausted", deliverySha, conflict: true);
        stack.Timeline.Append(
            folder,
            TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System,
            "Prior deterministic retry.",
            details: new Dictionary<string, string>
            {
                ["source"] = TaskIntegrationRecoveryService.AcceptanceRailSource,
                ["retryNumber"] = "1",
            });

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Escalated == 1, Describe(stack, snapshot));
        var escalated = stack.Scanner.FindJob("exhausted", _watchPath)!;
        Assert.Equal(TaskStates.Escalated, escalated.State);
        var laneChange = Assert.Single(
            stack.Timeline.ReadAll(escalated.FolderPath),
            entry => entry.Kind == TimelineEventKinds.LaneChanged
                     && entry.Details?.GetValueOrDefault("to") == TaskStates.Escalated);
        Assert.Contains("1/1 conflict requeues", laneChange.Details!["reason"], StringComparison.Ordinal);

        var secondSnapshot = await stack.Rail.RunOnceAsync();
        Assert.Equal(0, secondSnapshot.Escalated);
        Assert.Single(
            stack.Timeline.ReadAll(escalated.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptanceRailActed
                     && entry.Details?.GetValueOrDefault("action") == "escalated");
    }

    [Fact]
    public async Task InfrastructureCard_IsRequeuedToAutoReviewWithoutARebaseSteer()
    {
        var stack = Build();
        var deliverySha = CreateUnintegratedDelivery("infrastructure");
        SeedTask(stack, "infrastructure", deliverySha, infrastructureFailure: true);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Requeued == 1, Describe(stack, snapshot));
        var requeued = stack.Scanner.FindJob("infrastructure", _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, requeued.State);
        Assert.Null(requeued.PendingIntent);
        var prompt = File.ReadAllText(Path.Combine(requeued.FolderPath, "prompt.md"));
        Assert.DoesNotContain("## STEER", prompt, StringComparison.Ordinal);
        Assert.Contains(
            stack.Timeline.ReadAll(requeued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptanceRailActed
                     && entry.Details?.GetValueOrDefault("action") == "requeued-infrastructure"
                     && entry.Details?.GetValueOrDefault("retryNumber") == "1");
        var laneChange = Assert.Single(
            stack.Timeline.ReadAll(requeued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.LaneChanged
                     && entry.Details?.GetValueOrDefault("to") == TaskStates.AutoReview);
        Assert.Contains(
            RunFailureSignatures.GitNetworkTimeout,
            laneChange.Details!["reason"],
            StringComparison.Ordinal);
        Assert.Equal(
            LaneChangeCauses.ReviewInfrastructure,
            laneChange.Details.GetValueOrDefault(LaneChangeCauses.DetailKey));
    }

    [Fact]
    public async Task InfrastructureAtRetryLimit_IsEscalatedWithClassAndSignature()
    {
        var stack = Build(maxInfrastructureRequeues: 1);
        var deliverySha = CreateUnintegratedDelivery("infrastructure-exhausted");
        var folder = SeedTask(
            stack,
            "infrastructure-exhausted",
            deliverySha,
            infrastructureFailure: true);
        stack.Timeline.Append(
            folder,
            TimelineEventKinds.AcceptanceRailActed,
            TimelineActors.System,
            "Prior infrastructure replay.",
            details: new Dictionary<string, string>
            {
                ["action"] = "requeued-infrastructure",
                ["source"] = TaskIntegrationRecoveryService.AcceptanceRailSource,
                ["retryNumber"] = "1",
            });

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Escalated == 1, Describe(stack, snapshot));
        var escalated = stack.Scanner.FindJob("infrastructure-exhausted", _watchPath)!;
        Assert.Equal(TaskStates.Escalated, escalated.State);
        var laneChange = Assert.Single(
            stack.Timeline.ReadAll(escalated.FolderPath),
            entry => entry.Kind == TimelineEventKinds.LaneChanged
                     && entry.Details?.GetValueOrDefault("to") == TaskStates.Escalated);
        Assert.Contains(
            "1/1 infrastructure requeues",
            laneChange.Details!["reason"],
            StringComparison.Ordinal);
        Assert.Contains(
            RunFailureSignatures.GitNetworkTimeout,
            laneChange.Details["reason"],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConceptCard_IsUntouched()
    {
        var stack = Build();
        var integratedSha = Git(_repo, "rev-parse", "develop");
        SeedTask(stack, "concept", integratedSha, mode: TaskModes.Concept);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.Equal(0, snapshot.Accepted);
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob("concept", _watchPath)!.State);
    }

    [Fact]
    public async Task PendingCodingCard_IsNeverAccepted()
    {
        var stack = Build();
        var deliverySha = CreateUnintegratedDelivery("pending");
        SeedTask(stack, "pending", deliverySha);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.Equal(0, snapshot.Accepted);
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob("pending", _watchPath)!.State);
    }

    [Fact]
    public async Task EscalatedConflict_IsRequeuedByTheSameRail()
    {
        var stack = Build();
        var deliverySha = CreateUnintegratedDelivery("escalated-conflict");
        SeedTask(
            stack,
            "escalated-conflict",
            deliverySha,
            conflict: true,
            state: TaskStates.Escalated);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Requeued == 1, Describe(stack, snapshot));
        Assert.Equal(TaskStates.Ready, stack.Scanner.FindJob("escalated-conflict", _watchPath)!.State);
    }

    private Stack Build(
        int maxRequeues = AcceptanceRailDefaults.MaxRequeues,
        int maxInfrastructureRequeues = AcceptanceRailDefaults.MaxInfrastructureRequeues)
    {
        var logs = new List<string>();
        var values = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["AcceptanceRail:Enabled"] = "true",
            ["AcceptanceRail:IntervalSeconds"] = "180",
            ["AcceptanceRail:MaxRequeues"] = maxRequeues.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["AcceptanceRail:MaxInfrastructureRequeues"] = maxInfrastructureRequeues.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["AcceptanceRail:HoldList:0"] = AcceptanceRailDefaults.OperatorHoldTag,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var scanner = new TaskScannerService(
            configuration,
            new CollectingLogger<TaskScannerService>(logs),
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var states = new TaskStateMachine(
            scanner,
            new CollectingLogger<TaskStateMachine>(logs),
            timeline: timeline);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git,
            settings,
            pipeline,
            NullLogger<TaskIntegrationStatusService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            new CollectingLogger<TaskTransitionService>(logs),
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline);
        var escalation = new HumanReviewEscalation(
            states,
            transitions,
            configuration,
            new CollectingLogger<HumanReviewEscalation>(logs),
            scanner);
        var recovery = new TaskIntegrationRecoveryService(
            scanner,
            mutations,
            states,
            timeline,
            new CollectingLogger<TaskIntegrationRecoveryService>(logs));
        var rail = new AcceptanceRailHostedService(
            scanner,
            integration,
            transitions,
            recovery,
            escalation,
            timeline,
            configuration,
            new CollectingLogger<AcceptanceRailHostedService>(logs));
        return new Stack(scanner, timeline, pipeline, rail, logs);
    }

    private string SeedTask(
        Stack stack,
        string id,
        string commitSha,
        bool conflict = false,
        bool infrastructureFailure = false,
        string mode = TaskModes.Coding,
        IReadOnlyList<string>? tags = null,
        string state = TaskStates.HumanReview)
    {
        var folder = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(folder);
        var task = new
        {
            id,
            key = "AGT-9001",
            title = id,
            state,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            tags = tags ?? [],
            commit = Commit(commitSha),
            commits = new[] { Commit(commitSha) },
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                task,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: Awaiting acceptance.\n");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-9001",
            RunAttemptId = "run-" + id,
            Project = Project,
            Repository = _repo,
            ResultSha = commitSha,
            ResultRef = "task/" + id,
            AttemptChainId = "chain-" + id,
            IntegrationBranch = "develop",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        if (conflict || infrastructureFailure)
        {
            var now = DateTime.UtcNow;
            stack.Pipeline.RecordStep(folder, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.MergeIntoDevelopStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Failed,
                StartedAt = now,
                CompletedAt = now,
                Verdict = infrastructureFailure ? "error" : "conflict",
                FailureCode = infrastructureFailure
                    ? AcceptedIntegrationFailureCodes.IntegrationError
                    : AcceptedIntegrationFailureCodes.MergeConflict,
                VerdictSummary = infrastructureFailure
                    ? "Integration branch could not be synchronized."
                    : "Delivery conflicts with develop.",
                // Verbatim 2026-09-06 evidence: a 30-second git network cap,
                // which says nothing about the reviewed change.
                Reason = infrastructureFailure
                    ? "Integration branch 'develop' could not be fetched from origin: "
                      + "git operation timed out after 30 seconds"
                    : "Merge conflict in shared.txt.",
            });
        }
        stack.Scanner.InvalidateCache();
        Assert.NotNull(stack.Scanner.FindJob(id, _watchPath));
        return folder;
    }

    private string CreateUnintegratedDelivery(string id)
    {
        RunGit(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "feat: " + id);
        var sha = Git(_repo, "rev-parse", "HEAD");
        RunGit(_repo, "checkout", "-q", "develop");
        return sha;
    }

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static void RunGit(string cwd, params string[] args)
        => Git(cwd, args);

    private static string Describe(Stack stack, AcceptanceRailSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot) + Environment.NewLine + string.Join(Environment.NewLine, stack.Logs);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Acceptance rail test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        TimelineLog Timeline,
        PipelineExecutionLog Pipeline,
        AcceptanceRailHostedService Rail,
        List<string> Logs);

    private sealed class CollectingLogger<T>(List<string> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
