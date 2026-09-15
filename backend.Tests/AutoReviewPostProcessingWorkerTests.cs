using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class AutoReviewPostProcessingWorkerTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace;
    private readonly string _watchPath;

    public AutoReviewPostProcessingWorkerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "auto-review-post-processing-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProcessAsync_DrainsQueuedAutoReviewCardWithoutWaitingForRecurringTick()
    {
        SeedNoOpReviewJob("noop-task");
        var deps = BuildDeps();
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);

        await worker.ProcessAsync(new AutoReviewPostProcessingRequest(
            ProjectName: Project,
            JobId: "noop-task",
            WatchPath: _watchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: "test"),
            CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "noop-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "noop-task")));
        var followUp = Path.Combine(_watchPath, TaskStates.Ready, "noop-task", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUp));
        var lifecycle = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Ready, "noop-task", "lifecycle.json"));
        Assert.Contains("\"status\": \"failed\"", lifecycle);
        Assert.Contains("\"finishedAt\"", lifecycle);
        Assert.DoesNotContain("\"status\": \"running\"", lifecycle);
    }

    [Fact]
    public async Task ProcessAsync_WhenReviewDecisionOrchestratorDisabled_LeavesQueuedCardInAutoReview()
    {
        SeedNoOpReviewJob("disabled-task");
        var deps = BuildDeps(reviewDecisionEnabled: false);
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);

        await worker.ProcessAsync(new AutoReviewPostProcessingRequest(
            ProjectName: Project,
            JobId: "disabled-task",
            WatchPath: _watchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: "test"),
            CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "disabled-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "disabled-task")));
        var followUp = Path.Combine(_watchPath, TaskStates.AutoReview, "disabled-task", "orchestrator-follow-up.md");
        Assert.False(File.Exists(followUp));
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesCardsConcurrently_UpToMaxParallelism()
    {
        // Three cards enqueued, MaxParallelism=2: at most two are post-processed at
        // once, the third parks on the slot until one frees.
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 2);

        var active = 0;
        var maxObserved = 0;
        var processed = new ConcurrentBag<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.ProcessOverride = async (req, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMax(ref maxObserved, now);
            processed.Add(req.JobId);
            await release.Task;
            Interlocked.Decrement(ref active);
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            deps.Queue.Enqueue(Request("card-a"));
            deps.Queue.Enqueue(Request("card-b"));
            deps.Queue.Enqueue(Request("card-c"));

            // Two slots fill; the third cannot start until one frees.
            await WaitUntil(() => Volatile.Read(ref active) == 2);
            // Give the third request a chance to (wrongly) slip through, then confirm
            // the cap held.
            await Task.Delay(100);
            Assert.Equal(2, Volatile.Read(ref active));
            Assert.Equal(2, Volatile.Read(ref maxObserved));

            release.SetResult();
            await WaitUntil(() => processed.Count == 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(Volatile.Read(ref maxObserved) <= 2, "concurrency exceeded MaxParallelism");
        Assert.Equal(new[] { "card-a", "card-b", "card-c" }, processed.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_SameCardEnqueuedTwiceWhileInFlight_IsProcessedOnce()
    {
        // A duplicate enqueue for a card already in flight is dropped: the same card
        // is never post-processed twice concurrently.
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 3);

        var processed = new ConcurrentBag<string>();
        var active = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.ProcessOverride = async (req, ct) =>
        {
            Interlocked.Increment(ref active);
            processed.Add(req.JobId);
            await release.Task;
            Interlocked.Decrement(ref active);
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            deps.Queue.Enqueue(Request("dupe"));
            deps.Queue.Enqueue(Request("dupe")); // duplicate, must be dropped
            deps.Queue.Enqueue(Request("other"));

            // "dupe" (held) + "other" run; the second "dupe" is deduped away.
            await WaitUntil(() => Volatile.Read(ref active) == 2);
            await Task.Delay(100);

            Assert.Equal(2, Volatile.Read(ref active));
            Assert.Equal(1, processed.Count(x => x == "dupe"));

            release.SetResult();
            await WaitUntil(() => processed.Count == 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, processed.Count(x => x == "dupe"));
        Assert.Equal(1, processed.Count(x => x == "other"));
    }

    [Fact]
    public async Task ProcessAsync_WhenCanonicalReviewExecutorOwnsCard_ParksAwaitingReviewInsteadOfBlocking()
    {
        // Regression: a card whose review lives in the canonical remote review
        // data plane is skipped by the decision engine on purpose - the fenced
        // ReviewAttempt executor owns it. The queue used to read that silent
        // return as a failure and wrote post-processing-blocked with a generic
        // sentence, terminally parking a perfectly healthy card.
        SeedNoOpReviewJob("canonical-task");
        var deps = BuildDeps(canonicalReviewTaskKeys: ["canonical-task"]);
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);

        await worker.ProcessAsync(Request("canonical-task"), CancellationToken.None);

        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, "canonical-task");
        Assert.True(Directory.Exists(dir), "the card must stay in 4-auto-review");
        var lifecycle = File.ReadAllText(Path.Combine(dir, "lifecycle.json"));
        Assert.DoesNotContain("post-processing-blocked", lifecycle);
        Assert.DoesNotContain("blockingReason", lifecycle);
        Assert.Contains("awaiting-review", lifecycle);
        // The reason is named, not swallowed.
        Assert.Contains(PostProcessingCardResult.AwaitingCanonicalReviewExecutor, lifecycle);
    }

    [Fact]
    public async Task ProcessAsync_WhenTheCardIsSkipped_ClosesTheDecisionCheckAsSkippedNotRunning()
    {
        // The pass opens post-orchestrator-decision as "running" before it asks
        // the engine for a verdict. When the engine passes the card over, the
        // check must reach a terminal status: left "running" it is re-armed by
        // every backend restart and nothing ever terminalizes it (TE-8's
        // lifecycle.json), and "completed" would claim a decision that never
        // happened.
        SeedNoOpReviewJob("skipped-task");
        var deps = BuildDeps(canonicalReviewTaskKeys: ["skipped-task"]);
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);
        worker.DeferralDelayOverride = _ => TimeSpan.FromHours(1);

        await worker.ProcessAsync(Request("skipped-task"), CancellationToken.None);

        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, "skipped-task");
        var snapshot = JsonSerializer.Deserialize<LifecycleSnapshot>(
            File.ReadAllText(Path.Combine(dir, "lifecycle.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var decision = Assert.Single(
            snapshot.PostProcessingChecks,
            check => check.Name == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal("skipped", decision.Status);
        Assert.NotNull(decision.FinishedAt);
        Assert.DoesNotContain(snapshot.PostProcessingChecks, check => check.Status == "running");
        Assert.Null(snapshot.BlockingReason);
    }

    [Fact]
    public async Task ProcessCardAsync_WhenCanonicalReviewExecutorOwnsCard_ReportsDeferredWithReason()
    {
        // The engine itself must say why it did nothing, so the caller can tell
        // "not mine" apart from "broken".
        SeedNoOpReviewJob("canonical-reason");
        var deps = BuildDeps(canonicalReviewTaskKeys: ["canonical-reason"]);

        var result = await deps.Orchestrator.ProcessCardAsync(
            _workspace, Project, "canonical-reason", _watchPath, CancellationToken.None);

        Assert.Equal(PostProcessingCardStatus.Deferred, result.Status);
        Assert.Equal(PostProcessingCardResult.AwaitingCanonicalReviewExecutor, result.Reason);
    }

    [Fact]
    public async Task ProcessCardAsync_WhenDecisionPathRuns_ReportsDecided()
    {
        // The counter-example: a card the engine does own still runs its
        // decision path and reports Decided.
        SeedNoOpReviewJob("owned-task");
        var deps = BuildDeps();

        var result = await deps.Orchestrator.ProcessCardAsync(
            _workspace, Project, "owned-task", _watchPath, CancellationToken.None);

        Assert.Equal(PostProcessingCardStatus.Decided, result.Status);
        Assert.Equal("noop", result.Reason);
    }

    [Fact]
    public void ApplyOutcome_BlockedResult_WritesTheConcreteReasonNotOnlyTheGenericSentence()
    {
        SeedNoOpReviewJob("blocked-task");
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 1);
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, "blocked-task");
        BeginLifecycle(dir);

        worker.ApplyOutcome(
            Request("blocked-task"),
            PostProcessingCardResult.Blocked("cli-log-unreadable"),
            CancellationToken.None);

        var lifecycle = File.ReadAllText(Path.Combine(dir, "lifecycle.json"));
        Assert.Contains("post-processing-blocked", lifecycle);
        Assert.Contains("cli-log-unreadable", lifecycle);
    }

    [Fact]
    public async Task ApplyOutcome_DeferredResult_ReDrivesTheCard()
    {
        // A deferral is retried rather than blocked: the card comes back through
        // the queue after the backoff.
        SeedNoOpReviewJob("retry-task");
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 1);
        worker.DeferralDelayOverride = _ => TimeSpan.FromMilliseconds(10);

        worker.ApplyOutcome(
            Request("retry-task"),
            PostProcessingCardResult.Deferred(PostProcessingCardResult.AwaitingCanonicalReviewExecutor),
            CancellationToken.None);

        await WaitUntil(() => deps.Queue.PositionOf(Project, "retry-task") != null);
        Assert.NotNull(deps.Queue.PositionOf(Project, "retry-task"));
    }

    [Fact]
    public async Task ApplyOutcome_DeferredResult_StopsReDrivingAfterTheAttemptBudget_AndNeverBlocks()
    {
        // Exhausting the retry budget must leave the card resting, never blocked:
        // the durable lane plus the backstop sweep remain the safety net.
        SeedNoOpReviewJob("exhausted-task");
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 1);
        worker.DeferralDelayOverride = _ => TimeSpan.FromMilliseconds(10);
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, "exhausted-task");
        BeginLifecycle(dir);

        worker.ApplyOutcome(
            Request("exhausted-task") with { Attempt = AutoReviewPostProcessingWorker.MaxDeferralRetries },
            PostProcessingCardResult.Deferred(PostProcessingCardResult.AwaitingCanonicalReviewExecutor),
            CancellationToken.None);

        await Task.Delay(80);
        Assert.Null(deps.Queue.PositionOf(Project, "exhausted-task"));
        var lifecycle = File.ReadAllText(Path.Combine(dir, "lifecycle.json"));
        Assert.DoesNotContain("post-processing-blocked", lifecycle);
        Assert.Contains("awaiting-review", lifecycle);
    }

    [Fact]
    public void DeferralRetryDelay_DoublesPerAttemptAndCaps()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AutoReviewPostProcessingWorker.DeferralRetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(60), AutoReviewPostProcessingWorker.DeferralRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(120), AutoReviewPostProcessingWorker.DeferralRetryDelay(2));
        Assert.Equal(
            AutoReviewPostProcessingWorker.DeferralRetryMaxDelay,
            AutoReviewPostProcessingWorker.DeferralRetryDelay(99));
    }

    [Fact]
    public void DeferralRetryDelay_HonorsAnExplicitLowerCap()
    {
        // AGT-2842: the canonical-review-executor wait uses a tighter cap than
        // the generic ten-minute backoff while an executor is registered.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            AutoReviewPostProcessingWorker.DeferralRetryDelay(0, AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay));
        Assert.Equal(
            AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay,
            AutoReviewPostProcessingWorker.DeferralRetryDelay(1, AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay));
        Assert.Equal(
            AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay,
            AutoReviewPostProcessingWorker.DeferralRetryDelay(4, AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay));
    }

    [Fact]
    public void ResolveReviewExecutorAvailability_RegisteredExecutor_CapsTheEffectiveDelayAtSixtySeconds()
    {
        // Regression for AGT-2842: without a registered executor, attempt 4 of
        // this reason computes the generic 480s step of the ten-minute-capped
        // schedule (30,60,120,240,480,...). With one registered, the resolved
        // cap must bring that down to 60s so the card - and its liveStatus
        // queue reason - keep re-checking at least once a minute instead of
        // going quiet for minutes at a time.
        var deps = BuildDeps();
        var registry = new V1ReviewExecutorRegistry();
        registry.Register("agent-runner-01-review", new Contract.RegisterRunnerRequest(
            "agent-runner-01-review",
            "review-host",
            "review-host:1",
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.ReviewExecutor]));
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance,
            registry);

        var availability = worker.ResolveReviewExecutorAvailability(
            PostProcessingCardResult.AwaitingCanonicalReviewExecutor);
        Assert.NotNull(availability);
        Assert.True(availability!.AnyRegistered);

        var uncapped = AutoReviewPostProcessingWorker.DeferralRetryDelay(4);
        var capped = AutoReviewPostProcessingWorker.DeferralRetryDelay(
            4, AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay);

        Assert.Equal(TimeSpan.FromSeconds(480), uncapped);
        Assert.Equal(AutoReviewPostProcessingWorker.CanonicalReviewExecutorRegisteredMaxDelay, capped);
    }

    [Fact]
    public void ResolveReviewExecutorAvailability_NoRegistry_ReturnsNullAndKeepsTheGenericCap()
    {
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 1);

        var availability = worker.ResolveReviewExecutorAvailability(
            PostProcessingCardResult.AwaitingCanonicalReviewExecutor);

        Assert.Null(availability);
    }

    [Fact]
    public void ResolveReviewExecutorAvailability_UnrelatedReason_NeverConsultsTheRegistry()
    {
        var deps = BuildDeps();
        var registry = new V1ReviewExecutorRegistry();
        registry.Register("agent-runner-01-review", new Contract.RegisterRunnerRequest(
            "agent-runner-01-review",
            "review-host",
            "review-host:1",
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.ReviewExecutor]));
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance,
            registry);

        var availability = worker.ResolveReviewExecutorAvailability("card-already-in-flight");

        Assert.Null(availability);
    }

    [Fact]
    public void ApplyOutcome_CanonicalReviewExecutorDeferral_ExposesTheWaitReasonOnTheQueue()
    {
        SeedNoOpReviewJob("named-wait-task");
        var deps = BuildDeps();
        var registry = new V1ReviewExecutorRegistry();
        registry.Register("agent-runner-01-review", new Contract.RegisterRunnerRequest(
            "agent-runner-01-review",
            "review-host",
            "review-host:1",
            "1.0.0",
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.ReviewExecutor]));
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance,
            registry);
        worker.DeferralDelayOverride = _ => TimeSpan.FromHours(1);

        worker.ApplyOutcome(
            Request("named-wait-task"),
            PostProcessingCardResult.Deferred(PostProcessingCardResult.AwaitingCanonicalReviewExecutor),
            CancellationToken.None);

        // The card left `_pending` (PositionOf is null) but the wait is named
        // instead of reading as an unexplained empty queue.
        Assert.Null(deps.Queue.PositionOf(Project, "named-wait-task"));
        var wait = deps.Queue.WaitStateOf(Project, "named-wait-task");
        Assert.NotNull(wait);
        Assert.Equal(PostProcessingCardResult.AwaitingCanonicalReviewExecutor, wait!.Reason);
        Assert.Contains("agent-runner-01-review", wait.Detail);
    }

    [Fact]
    public void ApplyOutcome_CanonicalReviewExecutorDeferral_WithoutARegisteredExecutor_KeepsTheGenericBackoff()
    {
        // The counter-example: with no registry wired in (production default
        // when a project never runs Remote Review) or no executor registered,
        // the generic ten-minute-capped exponential backoff still applies.
        SeedNoOpReviewJob("uncapped-task");
        var deps = BuildDeps();
        var worker = BuildWorker(deps, maxParallelism: 1);
        worker.DeferralDelayOverride = _ => TimeSpan.FromHours(1);

        worker.ApplyOutcome(
            Request("uncapped-task") with { Attempt = 2 },
            PostProcessingCardResult.Deferred(PostProcessingCardResult.AwaitingCanonicalReviewExecutor),
            CancellationToken.None);

        var wait = deps.Queue.WaitStateOf(Project, "uncapped-task");
        Assert.NotNull(wait);
        Assert.Null(wait!.Detail);
    }

    /// <summary>Puts a card's lifecycle into the active post-processing state.</summary>
    private static void BeginLifecycle(string dir) =>
        File.WriteAllText(Path.Combine(dir, "lifecycle.json"),
            "{\"version\":1,\"phase\":\"post-processing-running\",\"postProcessingChecks\":[" +
            "{\"name\":\"orchestrator-post-processing\",\"status\":\"running\"," +
            "\"startedAt\":\"2026-08-04T15:36:52.1510585Z\"}]}");

    private AutoReviewPostProcessingRequest Request(string jobId) => new(
        ProjectName: Project,
        JobId: jobId,
        WatchPath: _watchPath,
        EnqueuedAtUtc: DateTime.UtcNow,
        Source: "test");

    private AutoReviewPostProcessingWorker BuildWorker(Deps deps, int maxParallelism)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["PostProcessing:MaxParallelism"] = maxParallelism.ToString(),
                ["ReviewDecisionOrchestrator:Enabled"] = "true",
            })
            .Build();
        return new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            config,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(15);
        }
    }

    private static void UpdateMax(ref int max, int candidate)
    {
        int prev;
        do
        {
            prev = Volatile.Read(ref max);
            if (candidate <= prev) return;
        }
        while (Interlocked.CompareExchange(ref max, candidate, prev) != prev);
    }

    /// <summary>
    /// Seeds the canonical attempt authority so the listed task keys look like
    /// remote-review-owned cards (a minted ReviewAttempt makes them non-legacy).
    /// </summary>
    private void SeedCanonicalReviewAttempts(IReadOnlyCollection<string> taskKeys)
    {
        var metadata = Path.Combine(_workspace, ".metadata");
        Directory.CreateDirectory(metadata);
        var attempts = string.Join(",", taskKeys.Select(key =>
            $$"""
            {
              "attemptId": "review_{{key}}",
              "taskKey": "{{key}}",
              "repositoryId": "repo_test",
              "sourceRunAttemptId": "run_{{key}}",
              "sourceReviewAttemptId": null,
              "subject": {
                "subjectId": "subject_{{key}}",
                "repositoryId": "repo_test",
                "expectedResultSha": "0000000000000000000000000000000000000000",
                "sourceRunAttemptId": "run_{{key}}",
                "taskRequirementsHash": "hash",
                "reviewPolicyHash": "policy",
                "evidenceDigestInputs": [],
                "repositoryUrl": null,
                "resultRef": null,
                "plan": null,
                "createdAt": "2026-08-04T12:00:00.0000000Z"
              },
              "state": 0,
              "lease": null,
              "lastFence": 1,
              "authorityEpoch": 1,
              "createdAt": "2026-08-04T12:00:00.0000000Z"
            }
            """));
        File.WriteAllText(
            Path.Combine(metadata, "attempt-authority.json"),
            $$"""
            {
              "schemaVersion": 2,
              "authorityEpoch": 1,
              "lastFenceByTask": {},
              "runAttempts": [],
              "reviewAttempts": [{{attempts}}],
              "currentRunByTask": {},
              "currentReviewByTask": {},
              "currentSubjectByTask": {}
            }
            """);
    }

    private Deps BuildDeps(
        bool reviewDecisionEnabled = true,
        IReadOnlyCollection<string>? canonicalReviewTaskKeys = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["ReviewDecisionOrchestrator:Enabled"] = reviewDecisionEnabled ? "true" : "false",
                ["ReviewDecisionOrchestrator:CallsPerHour"] = "100"
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(
            scanner,
            stateMachine,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner,
            mutations,
            stateMachine,
            transitions,
            indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        if (canonicalReviewTaskKeys is { Count: > 0 })
            SeedCanonicalReviewAttempts(canonicalReviewTaskKeys);
        var attemptAuthority = canonicalReviewTaskKeys is { Count: > 0 }
            ? new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance)
            : null;
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner,
            stateMachine,
            taskAccess,
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance),
            prompts,
            new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance),
            new AutoReviewStatusSnapshot(),
            config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            attemptAuthority: attemptAuthority);
        orchestrator.CliRunner = (_, _, _, _, _) => Task.FromResult("");

        return new Deps(config, scanner, mutations, new AutoReviewPostProcessingQueue(), orchestrator);
    }

    private void SeedNoOpReviewJob(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"),
            "# Implement cache refresh\n\nAdd a deterministic cache refresh for task reads after a lane transition, and verify the next read observes the moved card.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NOOP]]{Environment.NewLine}");
    }

    private sealed record Deps(
        IConfiguration Configuration,
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        AutoReviewPostProcessingQueue Queue,
        ReviewDecisionOrchestrator Orchestrator);
}
