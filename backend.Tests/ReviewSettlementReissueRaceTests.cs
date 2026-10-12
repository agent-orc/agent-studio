using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Contract = AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3091: the review settlement sweep integrated AGT-2951 at 18:12 on
/// 12.10.2026 although the concern round had reissued the card to
/// <c>2-ready</c> at 18:09. The sweep had waited out another card's gate and
/// then judged AGT-2951 on its stale lane snapshot; the coordinator and the
/// publication fence compared only the review generation, which a reissue does
/// not change. Only a successor review rolled the merge back.
///
/// <para>
/// Every case here drives one of those windows: the stale snapshot (resume and
/// reconcile), the superseded RunAttempt (resume, coordinator dequeue and
/// publication fence), the SHA the merge runner resolves, and the in-flight
/// integration the sweep no longer waits for.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class ReviewSettlementReissueRaceTests : IDisposable
{
    private const string Project = "Fixture";
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T18:09:19Z"));
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public ReviewSettlementReissueRaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "review-reissue-race-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Reissue Race Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---- 1. Fresh lane before deciding -------------------------------------

    [Fact]
    public async Task Resume_on_a_stale_auto_review_snapshot_of_a_reissued_card_neither_integrates_nor_writes()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "agt-2951", lane: TaskStates.Ready);
        RemoteReviewSettlementJournal.Write(card.FolderPath,
            JournalEntry(card, stack) with { Delivery = Settlement(card, shouldIntegrate: true) });
        var snapshot = stack.Scanner.FindJob(card.Id, _watchPath)! with { State = TaskStates.AutoReview };

        var outcome = await stack.Resume.ResumeAsync(snapshot, "review-settlement-reconcile");

        Assert.Equal(AutoReviewResumeAction.None, outcome.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.OutsideAutoReview, outcome.Reason);
        Assert.Equal(0, stack.IntegrateCalls);
        Assert.Null(RemoteDeliverySettlementStore.Read(card.FolderPath));
        Assert.Equal(TaskStates.Ready, stack.Scanner.FindJob(card.Id, _watchPath)!.State);
    }

    [Fact]
    public void Reconcile_on_a_stale_auto_review_snapshot_of_a_reissued_card_writes_no_sidecar()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "agt-2951-reconcile", lane: TaskStates.Ready);
        RemoteReviewSettlementJournal.Write(card.FolderPath,
            JournalEntry(card, stack) with { Delivery = Settlement(card, shouldIntegrate: true) });
        var snapshot = stack.Scanner.FindJob(card.Id, _watchPath)! with { State = TaskStates.AutoReview };

        stack.Reconciler.Reconcile(snapshot);

        Assert.Null(RemoteDeliverySettlementStore.Read(card.FolderPath));
        Assert.Equal(0, stack.IntegrateCalls);

        // The same journal on a card that really is in Auto Review still
        // restores its sidecar: only the stale lane is refused.
        SetLane(card, TaskStates.AutoReview);
        stack.Reconciler.Reconcile(snapshot);
        Assert.True(RemoteDeliverySettlementStore.MatchesAttempt(
            RemoteDeliverySettlementStore.Read(card.FolderPath), card.ReviewAttemptId));
    }

    // ---- 2. Only the reviewed RunAttempt may integrate ---------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_card_back_in_auto_review_with_a_newer_run_is_not_integrated_on_the_old_verdict(bool shouldIntegrate)
    {
        var entries = new List<string>();
        var stack = Build(resumeLogger: new CollectingLogger<AutoReviewDeliveryResumeService>(entries));
        var card = SeedPassedDelivery(stack, "reissued-" + shouldIntegrate.ToString().ToLowerInvariant());
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate));
        // The fix round: a new RunAttempt settles, the card is back in Auto
        // Review, and no successor review exists yet.
        SettleNewRun(stack, card, "fix-round", new string('b', 40));
        var task = stack.Scanner.FindJob(card.Id, _watchPath)!;

        var first = await stack.Resume.ResumeAsync(task, "review-settlement-reconcile");
        var second = await stack.Resume.ResumeAsync(task, "post-processing-deferral");

        foreach (var outcome in new[] { first, second })
        {
            Assert.Equal(AutoReviewResumeAction.None, outcome.Action);
            Assert.Equal(AutoReviewDeliveryResumeService.Reasons.ReviewedDeliverySuperseded, outcome.Reason);
            Assert.False(outcome.Resumed);
        }
        Assert.Equal(0, stack.IntegrateCalls);
        Assert.Equal(0, stack.RecordedFailures);
        Assert.Equal(TaskStates.AutoReview, stack.Scanner.FindJob(card.Id, _watchPath)!.State);
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationPending,
            RemoteDeliverySettlementStore.Read(card.FolderPath)!.Stage);
        Assert.Single(entries, entry => entry.Contains(
            AutoReviewDeliveryResumeService.Reasons.ReviewedDeliverySuperseded, StringComparison.Ordinal));
    }

    // ---- 4. The coordinator re-checks at dequeue ---------------------------

    [Fact]
    public async Task A_queued_review_driven_delivery_whose_card_was_reissued_completes_without_integrating()
    {
        var stack = Build();
        var left = SeedPassedDelivery(stack, "left-lane");
        var replaced = SeedPassedDelivery(stack, "replaced-run");
        var current = SeedPassedDelivery(stack, "still-owed");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var integrated = new List<string>();
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            async request =>
            {
                lock (integrated) integrated.Add(request.JobId);
                if (request.JobId == "blocker") await hold.Task;
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('c', 40));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            isAdmissibleDelivery: request => RemoteDeliveryIntegrationCoordinator.IsReviewedDeliveryAdmissible(
                request, stack.Scanner, stack.Authority));

        // A batch member carries no ReviewAttemptId and keeps today's path.
        var blocker = coordinator.EnqueueAsync(Request("blocker", minute: 0, reviewAttemptId: null));
        var leftLane = coordinator.EnqueueAsync(Request(left, minute: 1));
        var replacedRun = coordinator.EnqueueAsync(Request(replaced, minute: 2));
        var stillOwed = coordinator.EnqueueAsync(Request(current, minute: 3));
        var batchMember = coordinator.EnqueueAsync(Request("batch-member", minute: 4, reviewAttemptId: null));

        // While the blocker holds the gate: one card is reissued to Ready, the
        // other's fix round leases a new RunAttempt.
        SetLane(left, TaskStates.Ready);
        stack.Authority.AcquireRun(
            replaced.TaskKey, "repo_" + replaced.Id, null, "runner-a", "coding-host", 60, "run-fix-" + replaced.Id);
        hold.SetResult();

        await blocker.WaitAsync(Bounded);
        foreach (var refused in new[] { await leftLane.WaitAsync(Bounded), await replacedRun.WaitAsync(Bounded) })
        {
            Assert.Equal(MergeIntoIntegrationOutcome.Error, refused.Outcome);
            Assert.StartsWith(RemoteDeliveryIntegrationCoordinator.ReviewedDeliverySupersededError, refused.Error);
        }
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await stillOwed.WaitAsync(Bounded)).Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await batchMember.WaitAsync(Bounded)).Outcome);
        Assert.Equal(new[] { "blocker", current.Id, "batch-member" }, integrated);
    }

    // ---- 3. The merge runner merges only the reviewed SHA ------------------

    [Fact]
    public async Task Merge_runner_refuses_a_subject_that_names_another_sha_than_the_review()
    {
        var (repo, folder, taskSha) = SeedRunnerDelivery("subject-mismatch");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-subject-mismatch",
            RunAttemptId = "run-fix-round",
            Project = Project,
            Repository = repo,
            ResultSha = taskSha,
            AttemptChainId = "chain-fix-round",
            Executor = "agent-runner-01",
            LeaseId = "lease-fix-round",
            FencingToken = 1,
            CompletedAtUtc = _time.GetUtcNow(),
        });

        await AssertRefusedWithoutMerging(repo, folder, "subject-mismatch", reviewedSha: new string('d', 40));
    }

    [Fact]
    public async Task Merge_runner_refuses_the_attributed_commit_fallback_when_it_is_not_the_reviewed_sha()
    {
        // AGT-2951's shape: the reissue set the subject aside, so Resolve fell
        // back to the last attributed commit in task.json.
        var (repo, folder, taskSha) = SeedRunnerDelivery("attributed-fallback");
        WriteCard(folder, "attributed-fallback", TaskStates.AutoReview, new[]
        {
            new { sha = taskSha, branch = "runner/agent-runner-01/attributed-fallback" },
        });
        Assert.Equal(DeliveryRefSource.AttributedCommit, DeliveryRefResolver.Resolve("attributed-fallback", folder).Source);

        await AssertRefusedWithoutMerging(repo, folder, "attributed-fallback", reviewedSha: new string('e', 40));
    }

    [Fact]
    public async Task Merge_runner_counts_a_delivery_without_any_sha_as_a_mismatch()
    {
        var (repo, folder, taskSha) = SeedRunnerDelivery("no-sha");
        Assert.Null(DeliveryRefResolver.Resolve("no-sha", folder).ExpectedResultSha);

        await AssertRefusedWithoutMerging(repo, folder, "no-sha", reviewedSha: taskSha);
    }

    // ---- 5. The publication fence also checks the RunAttempt ---------------

    [Fact]
    public async Task A_run_leased_while_the_gate_runs_refuses_the_review_driven_publication()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "fenced");
        var (repo, folder, _) = SeedRunnerDelivery("fenced-merge");
        var (runner, queue) = Runner(repo, folder, "fenced-merge");
        var before = Git(repo, "rev-parse", "refs/heads/develop");
        var atFence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            request => Task.Run(() => runner.RunAsync(
                Project, "fenced-merge", folder, repo, "develop", CancellationToken.None,
                publicationFence: apply =>
                {
                    atFence.SetResult();
                    release.Task.Wait(Bounded);
                    return stack.Authority.TryApplyForCurrentReviewedDelivery(request.ReviewAttemptId!, () =>
                    {
                        applied = true;
                        apply();
                    });
                })),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);

        var integration = coordinator.EnqueueAsync(Request(card, minute: 0));
        await atFence.Task.WaitAsync(Bounded);
        Assert.True(stack.Authority.AcquireRun(
            card.TaskKey, "repo_" + card.Id, null, "runner-a", "coding-host", 60, "run-fix-" + card.Id).Accepted);
        release.SetResult();
        var result = await integration.WaitAsync(Bounded);

        Assert.False(applied);
        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.StartsWith(MergeIntoDevelopRunner.SupersededReviewGenerationError, result.Error);
        Assert.Equal(before, Git(repo, "rev-parse", "refs/heads/develop"));
        Assert.False(queue.Reader.TryRead(out _), "a superseded delivery must not publish");

        // The evidence projector's fence keeps its meaning: the review is still
        // the current generation, whatever happened to the delivery.
        var projected = false;
        Assert.True(stack.Authority.TryApplyForCurrentReview(card.ReviewAttemptId, () => projected = true));
        Assert.True(projected);
    }

    // ---- 6. No waiting on someone else's integration -----------------------

    [Fact]
    public async Task Resume_skips_an_integration_another_caller_drives_and_the_reconcile_tick_does_not_wait()
    {
        var holds = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var entered = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var calls = new List<string>();
        foreach (var id in new[] { "report-driven", "sweep-driven" })
        {
            holds[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entered[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var stack = Build(async request =>
        {
            lock (calls) calls.Add(request.JobId);
            entered[request.JobId].TrySetResult();
            await holds[request.JobId].Task;
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: new string('f', 40));
        });
        var reportDriven = SeedPassedDelivery(stack, "report-driven");
        RemoteDeliverySettlementStore.Write(reportDriven.FolderPath, Settlement(reportDriven, shouldIntegrate: true));
        var sweepDriven = SeedPassedDelivery(stack, "sweep-driven");
        RemoteDeliverySettlementStore.Write(sweepDriven.FolderPath, Settlement(sweepDriven, shouldIntegrate: true));

        // The report endpoint awaits its integration inline.
        var reportIntegration = stack.Coordinator.EnqueueAsync(Request(reportDriven, minute: 0));
        await entered["report-driven"].Task.WaitAsync(Bounded);

        var task = stack.Scanner.FindJob(reportDriven.Id, _watchPath)!;
        foreach (var source in new[] { "review-settlement-reconcile", "post-processing-deferral" })
        {
            var outcome = await stack.Resume.ResumeAsync(task, source).WaitAsync(Bounded);
            Assert.Equal(AutoReviewResumeAction.None, outcome.Action);
            Assert.Equal(AutoReviewDeliveryResumeService.Reasons.IntegrationInFlight, outcome.Reason);
        }

        // A tick starts one resume pass. That pass starts the sweep-driven
        // integration itself, which the per-project queue holds behind the
        // report's gate. Neither tick waits for the pass.
        await stack.Reconciler.RunOnceAsync().WaitAsync(Bounded);
        var pass = stack.Reconciler.ResumePass;
        Assert.False(pass.IsCompleted);
        await stack.Reconciler.RunOnceAsync().WaitAsync(Bounded);
        Assert.Same(pass, stack.Reconciler.ResumePass);
        Assert.False(entered["sweep-driven"].Task.IsCompleted);

        holds["report-driven"].SetResult();
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, (await reportIntegration.WaitAsync(Bounded)).Outcome);
        await entered["sweep-driven"].Task.WaitAsync(Bounded);
        Assert.False(pass.IsCompleted);
        holds["sweep-driven"].SetResult();
        await pass.WaitAsync(Bounded);

        // The integration the sweep started itself completed in the same pass.
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob(sweepDriven.Id, _watchPath)!.State);
        Assert.Equal(new[] { "report-driven", "sweep-driven" }, calls);
    }

    // ---- harness -------------------------------------------------------------

    private async Task AssertRefusedWithoutMerging(string repo, string folder, string jobId, string reviewedSha)
    {
        var (runner, queue) = Runner(repo, folder, jobId);
        var before = Git(repo, "rev-parse", "refs/heads/develop");
        var laneBefore = TryGit(repo, "rev-parse", GitService.IntegrationLaneRef("develop"));

        var result = await runner.RunAsync(
            Project, jobId, folder, repo, "develop", CancellationToken.None,
            reviewedResultSha: reviewedSha);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.StaleAttempt, result.FailureCode);
        Assert.Equal(before, Git(repo, "rev-parse", "refs/heads/develop"));
        Assert.Equal(laneBefore, TryGit(repo, "rev-parse", GitService.IntegrationLaneRef("develop")));
        Assert.False(queue.Reader.TryRead(out _), "a refused delivery must not publish");
    }

    /// <summary>A repository with a task branch the local fallback would merge, and a job folder outside it.</summary>
    private (string Repo, string Folder, string TaskSha) SeedRunnerDelivery(string id)
    {
        var repo = Path.Combine(_root, "runner-" + id);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "develop");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Reissue Race Test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "seed");
        Git(repo, "checkout", "-q", "-b", "task/" + id);
        File.WriteAllText(Path.Combine(repo, id + ".txt"), id + "\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "feat: " + id);
        var taskSha = Git(repo, "rev-parse", "HEAD");
        Git(repo, "checkout", "-q", "develop");

        var folder = Path.Combine(_root, "jobs", id);
        Directory.CreateDirectory(folder);
        return (repo, folder, taskSha);
    }

    private static (MergeIntoDevelopRunner Runner, IntegrationPushQueue Queue) Runner(
        string repo, string folder, string jobId)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        pipeline.Begin(folder, PipelineCatalogue.Standard, Project, jobId);
        var queue = new IntegrationPushQueue();
        return (new MergeIntoDevelopRunner(git, pipeline, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue), queue);
    }

    private RemoteDeliveryIntegrationRequest Request(SeededCard card, int minute)
        => Request(card.Id, minute, card.ReviewAttemptId) with { ReviewedResultSha = card.DeliverySha };

    private RemoteDeliveryIntegrationRequest Request(string jobId, int minute, string? reviewAttemptId)
        => new(
            Project,
            jobId,
            Path.Combine(_watchPath, TaskStates.AutoReview, jobId),
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            _time.GetUtcNow().AddMinutes(minute),
            reviewAttemptId);

    private RemoteDeliverySettlementRecord Settlement(SeededCard card, bool shouldIntegrate) => new()
    {
        TaskKey = card.TaskKey,
        ReviewAttemptId = card.ReviewAttemptId,
        Outcome = nameof(ReviewTerminalOutcome.Pass),
        ShouldIntegrate = shouldIntegrate,
        BuildTestGate = shouldIntegrate
            ? nameof(RemoteBuildTestGateClass.NotApplicable)
            : nameof(RemoteBuildTestGateClass.Failed),
        GateReason = shouldIntegrate
            ? "The Remote Review plan has no applicable build/test gate."
            : "At least one applicable Remote Review build/test gate did not pass.",
        IntegrationBranch = "develop",
        IntegrationStrategy = IntegrationStrategies.DirectMerge,
        PipelineType = PipelineTypes.Task,
        DeliveredAtUtc = _time.GetUtcNow(),
        Stage = RemoteDeliverySettlementStage.IntegrationPending,
        RecordedAtUtc = _time.GetUtcNow(),
    };

    private RemoteReviewSettlementEntry JournalEntry(SeededCard card, Stack stack)
    {
        var review = stack.Authority.GetReview(card.ReviewAttemptId)!;
        var report = new Contract.ReviewReportRequest(
            "reviewer", "instance", "lease", review.LastFence, "review-settle-" + card.Id,
            "Pass", null, "All aspects passed.",
            new Contract.ReviewWorkspaceProofDto(review.Subject.RepositoryId, card.DeliverySha,
                card.DeliverySha, new string('a', 40), false, false, "workspace", "namespace"),
            new Contract.ReviewEnvironmentDto("review-host", "reviewer", "instance", "linux", "x64",
                "10.0", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [], review.AuthorityEpoch);
        return new RemoteReviewSettlementEntry
        {
            AttemptId = card.ReviewAttemptId,
            TaskKey = card.TaskKey,
            IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Report = report,
            ReceivedAtUtc = _time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>A card whose delivery settled and whose canonical ReviewAttempt passed.</summary>
    private SeededCard SeedPassedDelivery(Stack stack, string id, string lane = TaskStates.AutoReview)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + id);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var taskKey = "AGT-" + id;
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, id);
        Directory.CreateDirectory(folder);
        WriteCard(folder, id, lane, Array.Empty<object>(), taskKey);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);

        var run = SettleNewRun(stack, new SeededCard(id, taskKey, folder, deliverySha, string.Empty), "run", deliverySha);
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "repo_" + id, deliverySha, run.AttemptId, "req", "policy", [],
            "review-create-" + id)).ReviewAttempt!;
        var claimed = stack.Authority.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 60, "review-claim-" + id).ReviewAttempt!;
        Assert.True(stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + id),
            deliverySha,
            ReviewTerminalOutcome.Pass,
            Reason: "All aspects passed.")).Accepted);

        return new SeededCard(id, taskKey, folder, deliverySha, review.AttemptId);
    }

    private static RunAttemptDto SettleNewRun(Stack stack, SeededCard card, string label, string resultSha)
    {
        var run = stack.Authority.AcquireRun(
            card.TaskKey, "repo_" + card.Id, null, "runner-a", "coding-host", 60, $"{label}-{card.Id}").RunAttempt!;
        Assert.True(stack.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, $"{label}-complete-{card.Id}"),
            Outcome = "done",
            ResultSha = resultSha,
        }).Accepted);
        return run;
    }

    private static void WriteCard(string folder, string id, string lane, object[] commits, string? key = null)
        => File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = key ?? "AGT-" + id,
                    title = id,
                    state = lane,
                    order = 1,
                    agent = "codex",
                    cliType = "codex",
                    mode = TaskModes.Coding,
                    projectName = Project,
                    ownerClientId = DefaultClientIdentity.Id,
                    commits,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    /// <summary>A lane move as the scanner sees it; the folder layout keeps the card where it is.</summary>
    private static void SetLane(SeededCard card, string lane)
        => TaskJsonFile.UpdateField(card.FolderPath, "state", lane, NullLogger.Instance);

    private Stack Build(
        Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>>? integrate = null,
        ILogger<AutoReviewDeliveryResumeService>? resumeLogger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
        }).Build();

        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var authority = new AttemptAuthorityService(
            NullLogger<AttemptAuthorityService>.Instance, () => _time.GetUtcNow().UtcDateTime);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline,
            attemptAuthority: authority);
        var escalation = new HumanReviewEscalation(
            states, transitions, _root, NullLogger<HumanReviewEscalation>.Instance, scanner);

        var counters = new Counters();
        // The coordinator's own guards stay permissive: only the resume
        // service under test can keep a delivery out of `integrate`.
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            request =>
            {
                Interlocked.Increment(ref counters.Integrate);
                return integrate?.Invoke(request)
                       ?? Task.FromResult(MergeIntoIntegrationResult.Of(
                           MergeIntoIntegrationOutcome.Merged, mergedSha: new string('9', 40)));
            },
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            recordFailure: (_, _, _, _) => Interlocked.Increment(ref counters.RecordFailure));
        var resume = new AutoReviewDeliveryResumeService(
            scanner,
            authority,
            integration,
            settings,
            coordinator,
            transitions,
            escalation,
            resumeLogger ?? NullLogger<AutoReviewDeliveryResumeService>.Instance);
        var reconciler = new RemoteReviewSettlementReconciler(
            scanner, authority, new RemoteReviewEvidenceProjectionQueue(), resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);

        return new Stack(scanner, authority, pipeline, coordinator, resume, reconciler, counters);
    }

    private sealed class Counters
    {
        public int Integrate;
        public int RecordFailure;
    }

    private sealed record SeededCard(
        string Id,
        string TaskKey,
        string FolderPath,
        string DeliverySha,
        string ReviewAttemptId);

    private sealed record Stack(
        TaskScannerService Scanner,
        AttemptAuthorityService Authority,
        PipelineExecutionLog Pipeline,
        RemoteDeliveryIntegrationCoordinator Coordinator,
        AutoReviewDeliveryResumeService Resume,
        RemoteReviewSettlementReconciler Reconciler,
        Counters Counters)
    {
        public int IntegrateCalls => Volatile.Read(ref Counters.Integrate);
        public int RecordedFailures => Volatile.Read(ref Counters.RecordFailure);
    }

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
        {
            lock (entries) entries.Add(formatter(state, exception));
        }
    }

    private static string? TryGit(string cwd, params string[] args)
    {
        var (exitCode, stdout, _) = RunGit(cwd, args);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private static string Git(string cwd, params string[] args)
    {
        var (exitCode, stdout, stderr) = RunGit(cwd, args);
        Assert.True(exitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string cwd, string[] args)
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
        return (process.ExitCode, stdout, stderr);
    }
}
