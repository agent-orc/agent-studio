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
    public async Task ReconciledGeneration_IsAutoAcceptedAndRetainsCommitDecisions()
    {
        var stack = Build();
        var oldSha = CreateUnintegratedDelivery("earlier");
        RunGit(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "final.txt"), "final delivery");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "final generation");
        var finalSha = Git(_repo, "rev-parse", "HEAD");
        var folder = SeedTask(stack, "reconciled", finalSha);
        TaskJsonFile.UpdateField(folder, "commits", new[]
        {
            new TaskCommitInfo { Sha = oldSha, FilesChanged = 1, DeliveryGeneration = 1, Branch = "agent-studio/results/old" },
            new TaskCommitInfo { Sha = finalSha, FilesChanged = 1, DeliveryGeneration = 3, Branch = "agent-studio/results/final" },
        }, NullLogger.Instance);
        stack.Scanner.InvalidateCache();

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Accepted == 1, Describe(stack, snapshot));
        var completed = stack.Scanner.FindJob("reconciled", _watchPath)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Equal([oldSha, finalSha], completed.Commits.Select(commit => commit.Sha));
        Assert.Equal([CommitIntegrationRules.Superseded, CommitIntegrationRules.Ancestor],
            completed.Commits.Select(commit => commit.IntegrationRule));
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
        var obligationPath = Assert.Single(Directory.GetFiles(
            Path.Combine(queued.FolderPath, "logs", "integration-bounce"), "*.json"));
        var obligation = IntegrationBounceObligationStore.Read(obligationPath)!;
        Assert.Equal("queued", obligation.State);
        Assert.Equal("run-conflict", obligation.RunAttemptId);
        Assert.Equal(deliverySha, obligation.ResultSha);
        Assert.Equal("merge-conflict", obligation.FailureCode);
        Assert.Equal("merge-origin-into-delivery", obligation.MechanicalRoute);
        Assert.NotEmpty(obligation.IdempotencyKey);
        Assert.NotEmpty(obligation.PolicyVersion!);

        await stack.Rail.RunOnceAsync();
        await Build().Rail.RunOnceAsync(); // A backend restart has no live studio session.
        Assert.Single(Directory.GetFiles(
            Path.Combine(queued.FolderPath, "logs", "integration-bounce"), "*.json"));
        Assert.Single(stack.Timeline.ReadAll(queued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
    }

    [Fact]
    public async Task SeventeenReviewedConflicts_DrainAfterRestartWithoutStudioSession()
    {
        var first = Build(shadowOnly: true);
        for (var index = 0; index < 17; index++)
        {
            var id = $"session-loss-{index}";
            SeedTask(first, id, CreateUnintegratedDelivery(id), conflict: true);
        }
        var shadow = await first.Rail.RunOnceAsync();
        Assert.Equal(17, shadow.BounceShadowed);
        Assert.Equal(0, shadow.Requeued);
        var shadowMetrics = IntegrationBounceObligationStore.Measure(
            first.Scanner.ScanAllAutomationJobs(), shadow.BounceFalseEligibility);
        Assert.Equal(17, shadowMetrics.Eligible);
        Assert.Equal(0, shadowMetrics.Queued);

        var restarted = Build();
        var drained = await restarted.Rail.RunOnceAsync();
        Assert.Equal(17, drained.Requeued);
        Assert.Equal(0, drained.Failed);
        var metrics = IntegrationBounceObligationStore.Measure(
            restarted.Scanner.ScanAllAutomationJobs(), drained.BounceFalseEligibility);
        Assert.Equal(17, metrics.Queued);
        Assert.NotNull(metrics.MeanClaimLatencyMilliseconds);
        foreach (var index in Enumerable.Range(0, 17))
        {
            var task = restarted.Scanner.FindJob($"session-loss-{index}", _watchPath)!;
            Assert.Equal(TaskStates.Ready, task.State);
            Assert.Single(Directory.GetFiles(
                Path.Combine(task.FolderPath, "logs", "integration-bounce"), "*.json"));
        }
    }

    [Fact]
    public async Task SameOperatorReviewEpoch_ReturnsRepeatToOperator()
    {
        var stack = Build();
        var sha = CreateUnintegratedDelivery("repeat-epoch");
        var folder = SeedTask(stack, "repeat-epoch", sha, conflict: true);
        stack.Timeline.Append(folder, TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System, "Earlier automatic bounce.",
            details: new Dictionary<string, string>
            {
                ["automatic"] = "true",
                ["attemptEpoch"] = "0",
                [IntegrationRecoveryBudget.DeliveryChainIdKey] = "previous-delivery",
            });

        var snapshot = await stack.Rail.RunOnceAsync();
        Assert.Equal(1, snapshot.BounceDeferred);
        Assert.Equal(TaskStates.HumanReview,
            stack.Scanner.FindJob("repeat-epoch", _watchPath)!.State);
        var path = Assert.Single(Directory.GetFiles(
            Path.Combine(folder, "logs", "integration-bounce"), "*.json"));
        Assert.Equal("guardian-required", IntegrationBounceObligationStore.Read(path)!.RouteDecision);
        Assert.Equal(1, IntegrationBounceObligationStore.Measure(
            stack.Scanner.ScanAllAutomationJobs(), snapshot.BounceFalseEligibility).Repeats);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BounceKillSwitch_LeavesTypedObligationForOperator(
        bool globallyEnabled, bool projectEnabled)
    {
        var stack = Build(bounceEnabled: globallyEnabled,
            projectBounceEnabled: projectEnabled);
        var id = $"disabled-{globallyEnabled}-{projectEnabled}";
        var folder = SeedTask(stack, id, CreateUnintegratedDelivery(id), conflict: true);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.Equal(1, snapshot.BounceDeferred);
        Assert.Equal(0, snapshot.Requeued);
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob(id, _watchPath)!.State);
        var path = Assert.Single(Directory.GetFiles(
            Path.Combine(folder, "logs", "integration-bounce"), "*.json"));
        var obligation = IntegrationBounceObligationStore.Read(path)!;
        Assert.Equal("operator-disabled", obligation.RouteDecision);
        Assert.Equal("merge-conflict", obligation.FailureCode);
    }

    [Fact]
    public void ManualBounce_ProjectsOneMatchingObligation()
    {
        var stack = Build(shadowOnly: true);
        var id = "manual-bounce";
        SeedTask(stack, id, CreateUnintegratedDelivery(id), conflict: true);
        var job = stack.Scanner.FindJob(id, _watchPath)!;
        var status = stack.Integration.BuildLookup([job])[job.TaskKey];

        var queued = stack.Recovery.Queue(job, status,
            AcceptedIntegrationFailureCodes.MergeConflict,
            TaskIntegrationRecoveryService.OperatorSource);

        Assert.True(queued.Queued, queued.Error);
        var ready = stack.Scanner.FindJob(id, _watchPath)!;
        var path = Assert.Single(Directory.GetFiles(
            Path.Combine(ready.FolderPath, "logs", "integration-bounce"), "*.json"));
        var obligation = IntegrationBounceObligationStore.Read(path)!;
        Assert.Equal("manual-queued", obligation.State);
        Assert.Equal("operator", obligation.RouteDecision);
        Assert.Equal(1, IntegrationBounceObligationStore.Measure(
            stack.Scanner.ScanAllAutomationJobs(), 0).ManualInterventions);
    }

    [Fact]
    public async Task StaleAttempt_DoesNotCreateAnEligibleBounce()
    {
        var stack = Build();
        var sha = CreateUnintegratedDelivery("stale-attempt");
        var folder = SeedTask(stack, "stale-attempt", sha, conflict: true);
        var path = ReviewSubjectStore.PathFor(folder);
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "run-stale-attempt", "", StringComparison.Ordinal));

        var snapshot = await stack.Rail.RunOnceAsync();
        Assert.Equal(1, snapshot.BounceFalseEligibility);
        Assert.Equal(0, snapshot.Requeued);
        Assert.Equal(TaskStates.HumanReview,
            stack.Scanner.FindJob("stale-attempt", _watchPath)!.State);
        Assert.False(Directory.Exists(Path.Combine(folder, "logs", "integration-bounce")));
    }

    [Fact]
    public async Task RestartRepairsReceiptAfterReadyPromotion()
    {
        var stack = Build();
        var sha = CreateUnintegratedDelivery("partial-receipt");
        SeedTask(stack, "partial-receipt", sha, conflict: true);
        Assert.Equal(1, (await stack.Rail.RunOnceAsync()).Requeued);
        var ready = stack.Scanner.FindJob("partial-receipt", _watchPath)!;
        var path = Assert.Single(Directory.GetFiles(
            Path.Combine(ready.FolderPath, "logs", "integration-bounce"), "*.json"));
        var queued = IntegrationBounceObligationStore.Read(path)!;
        IntegrationBounceObligationStore.Update(ready.FolderPath,
            queued with { State = "proposed", ClaimedAtUtc = null });

        await Build().Rail.RunOnceAsync();

        Assert.Equal("queued", IntegrationBounceObligationStore.Read(path)!.State);
        Assert.Single(stack.Timeline.ReadAll(ready.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
    }

    [Fact]
    public async Task DuplicateConcurrentTicks_QueueOneBounce()
    {
        var stack = Build();
        var id = "duplicate-tick";
        SeedTask(stack, id, CreateUnintegratedDelivery(id), conflict: true);

        await Task.WhenAll(stack.Rail.RunOnceAsync(), stack.Rail.RunOnceAsync());

        var ready = stack.Scanner.FindJob(id, _watchPath)!;
        Assert.Equal(TaskStates.Ready, ready.State);
        Assert.Single(Directory.GetFiles(
            Path.Combine(ready.FolderPath, "logs", "integration-bounce"), "*.json"));
        Assert.Single(stack.Timeline.ReadAll(ready.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
    }

    [Theory]
    [InlineData(false, false, "low")]
    [InlineData(true, false, "xhigh")]
    [InlineData(false, true, "xhigh")]
    public async Task BounceRoute_RespectsPolicyFloorAndOperatorPin(
        bool critical, bool pinned, string expectedLevel)
    {
        var stack = Build();
        var id = $"route-{critical}-{pinned}";
        var folder = SeedTask(stack, id, CreateUnintegratedDelivery(id), conflict: true);
        TaskJsonFile.UpdateField(folder, "model", "gpt-5.6-sol", NullLogger.Instance);
        TaskJsonFile.UpdateField(folder, "thinkingLevel", "xhigh", NullLogger.Instance);
        TaskJsonFile.UpdateField(folder, "modelExplicit", pinned, NullLogger.Instance);
        TaskJsonFile.UpdateField(folder, "thinkingLevelExplicit", pinned, NullLogger.Instance);
        if (critical)
            File.AppendAllText(Path.Combine(folder, "prompt.md"), "Security boundary and stale-write rejection.\n");
        stack.Scanner.InvalidateCache();

        Assert.Equal(1, (await stack.Rail.RunOnceAsync()).Requeued);
        var ready = stack.Scanner.FindJob(id, _watchPath)!;
        Assert.Equal(expectedLevel, ready.ThinkingLevel);
        var path = Assert.Single(Directory.GetFiles(
            Path.Combine(ready.FolderPath, "logs", "integration-bounce"), "*.json"));
        var receipt = IntegrationBounceObligationStore.Read(path)!;
        Assert.Equal($"gpt-5.6-sol/{expectedLevel}", receipt.SelectedRoute);
        Assert.Equal(pinned, receipt.OperatorPinPresent);
        Assert.NotEmpty(receipt.PolicyVersion!);
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
        Assert.StartsWith(
            $"automatic recovery budget used: 1/1 for delivery {deliverySha[..12]}",
            laneChange.Details!["reason"],
            StringComparison.Ordinal);
        Assert.Contains(
            "legacy automatic recovery round without a delivery identifier",
            laneChange.Details["reason"],
            StringComparison.Ordinal);

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

    /// <summary>
    /// AGT-2856 (a) - the operator case: a card whose delivery is proven by its
    /// reviewed result instead of an attributed commit. Its verdict must not
    /// depend on the other cards sharing the sweep, so the rail accepts it
    /// rather than refusing an acceptance the board already reports as
    /// integrated.
    /// </summary>
    [Fact]
    public async Task IntegratedFencedDeliveryWithoutAttributedCommit_IsAcceptedNotRefused()
    {
        var stack = Build();
        var integratedSha = Git(_repo, "rev-parse", "develop");
        SeedTask(stack, "fenced", integratedSha, attributeCommits: false);
        SeedTask(stack, "neighbour", integratedSha);

        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.True(snapshot.Accepted == 2, Describe(stack, snapshot));
        Assert.Equal(TaskStates.Completed, stack.Scanner.FindJob("fenced", _watchPath)!.State);
        Assert.DoesNotContain(
            stack.Logs,
            line => line.Contains("acceptance-rail-accept-refused", StringComparison.Ordinal));
    }

    /// <summary>
    /// AGT-2856 (b) and (c) - a delivery the rail cannot recover is refused once
    /// and then left alone. Only a new fact reopens the decision, and every
    /// refusal line in the log belongs to one such state change.
    /// </summary>
    [Fact]
    public async Task PendingDelivery_IsRefusedOnce_AndRetriedOnlyOnANewFact()
    {
        var stack = Build();
        var deliverySha = CreateUnintegratedDelivery("unrecoverable");
        var folder = SeedTask(
            stack,
            "unrecoverable",
            deliverySha,
            conflict: true,
            fencedDelivery: false);

        var first = await stack.Rail.RunOnceAsync();
        Assert.True(first.Failed == 1, Describe(stack, first));
        Assert.Equal(0, first.Suppressed);
        Assert.Equal(1, RefusalLines(stack));
        Assert.Contains(
            stack.Logs,
            line => line.Contains("acceptance-rail-requeue-refused", StringComparison.Ordinal));

        var second = await stack.Rail.RunOnceAsync();
        Assert.Equal(0, second.Failed);
        Assert.True(second.Suppressed == 1, Describe(stack, second));
        Assert.Equal(1, RefusalLines(stack));
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob("unrecoverable", _watchPath)!.State);

        // A new fact - the next integration attempt fails differently - reopens
        // the decision, and the refusal is logged again exactly once.
        RecordMergeConflict(stack, folder, "Merge conflict in another-file.txt.");
        stack.Scanner.InvalidateCache();

        var third = await stack.Rail.RunOnceAsync();
        Assert.True(third.Failed == 1, Describe(stack, third));
        Assert.Equal(2, RefusalLines(stack));

        var fourth = await stack.Rail.RunOnceAsync();
        Assert.Equal(1, fourth.Suppressed);
        Assert.Equal(2, RefusalLines(stack));
    }

    /// <summary>
    /// AGT-2856 (3) - the project policy may keep a card in Human Review. It
    /// then stays there with its integration proof and without a warning, no
    /// matter how often the rail sweeps.
    /// </summary>
    [Fact]
    public async Task HeldIntegratedCard_StaysInHumanReviewWithoutWarnings()
    {
        var stack = Build();
        var integratedSha = Git(_repo, "rev-parse", "develop");
        SeedTask(stack, "held-integrated", integratedSha, tags: [AcceptanceRailDefaults.OperatorHoldTag]);

        await stack.Rail.RunOnceAsync();
        var second = await stack.Rail.RunOnceAsync();

        Assert.Equal(1, second.Held);
        Assert.Equal(TaskStates.HumanReview, stack.Scanner.FindJob("held-integrated", _watchPath)!.State);
        Assert.DoesNotContain(
            stack.Logs,
            line => line.StartsWith("Warning:", StringComparison.Ordinal));
    }

    private static int RefusalLines(Stack stack)
        => stack.Logs.Count(line => line.Contains("-refused", StringComparison.Ordinal));

    private static void RecordMergeConflict(Stack stack, string folder, string reason)
    {
        var now = DateTime.UtcNow;
        stack.Pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "conflict",
            FailureCode = AcceptedIntegrationFailureCodes.MergeConflict,
            VerdictSummary = "Delivery conflicts with develop.",
            Reason = reason,
        });
    }

    private Stack Build(
        int maxRequeues = AcceptanceRailDefaults.MaxRequeues,
        int maxInfrastructureRequeues = AcceptanceRailDefaults.MaxInfrastructureRequeues,
        bool shadowOnly = false,
        bool bounceEnabled = true,
        bool projectBounceEnabled = true)
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
            ["IntegrationBounceRail:ShadowOnly"] = shadowOnly.ToString(),
            ["IntegrationBounceRail:Enabled"] = bounceEnabled.ToString(),
            ["IntegrationBounceRail:Projects:Fixture:Enabled"] = projectBounceEnabled.ToString(),
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
            new CollectingLogger<AcceptanceRailHostedService>(logs),
            new IntegrationGenerationReconcileSweep(mutations));
        return new Stack(scanner, timeline, pipeline, integration, recovery, rail, logs);
    }

    private string SeedTask(
        Stack stack,
        string id,
        string commitSha,
        bool conflict = false,
        bool infrastructureFailure = false,
        string mode = TaskModes.Coding,
        IReadOnlyList<string>? tags = null,
        string state = TaskStates.HumanReview,
        bool attributeCommits = true,
        bool fencedDelivery = true)
    {
        var folder = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(folder);
        var task = new
        {
            id,
            key = "AGT-" + id,
            title = id,
            state,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            tags = tags ?? [],
            commit = attributeCommits ? Commit(commitSha) : null,
            commits = attributeCommits ? new[] { Commit(commitSha) } : Array.Empty<object>(),
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                task,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: Awaiting acceptance.\n");
        if (fencedDelivery)
        {
            ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
            {
                TaskKey = "AGT-" + id,
                RunAttemptId = "run-" + id,
                Project = Project,
                Repository = _repo,
                ResultSha = commitSha,
                ResultRef = "task/" + id,
                AttemptChainId = "chain-" + id,
                IntegrationBranch = "develop",
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        }
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
        TaskIntegrationStatusService Integration,
        TaskIntegrationRecoveryService Recovery,
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
