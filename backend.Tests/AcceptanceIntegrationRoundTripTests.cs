using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Integration-before-review regression coverage. Remote deliveries are fenced
/// by <c>logs/review-subject.json</c>; immediate integration consumes that exact
/// ref and SHA, while acceptance only validates current lineage and Git ancestry.
/// </summary>
public sealed class AcceptanceIntegrationRoundTripTests : IDisposable
{
    private static readonly TimeSpan AsyncTestDeadline = TimeSpan.FromSeconds(30);
    private const string Project = "Fixture";
    private const string Slug = "remote-delivery";
    private const string TaskKey = "AGT-2227";
    private const string DeliveryRef = "runner/agent-runner-01/AGT-2227";

    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _origin;

    public AcceptanceIntegrationRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "accept-integration-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "project-store");
        _repo = Path.Combine(_tempDir, "repo");
        _origin = Path.Combine(_tempDir, "origin.git");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_tempDir, "init", "--bare", "-q", "--initial-branch=main", _origin);
        RunGit(_tempDir, "init", "-q", "-b", "main", _repo);
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "base\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "seed");
        RunGit(_repo, "remote", "add", "origin", _origin);
        RunGit(_repo, "push", "-q", "-u", "origin", "main");
        RunGit(_repo, "remote", "set-head", "origin", "main");
        RunGit(_repo, "checkout", "-q", "-b", "develop");
        RunGit(_repo, "push", "-q", "-u", "origin", "develop");
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "develop");
    }

    [Fact]
    public async Task AcceptPlanningCompletedEpic_ReachesCompleted_WithoutAMergeItCannotHave()
    {
        // An Epic planning completion parks in 5-human-review with no task
        // branch and no commits: its delivery is the child cards, not code.
        // Accepting it must not enter the transactional merge, which would
        // return NoTaskBranch and bounce the card back to Human Review for
        // ever - the TE-8 dead end relocated one lane over.
        var deliverySha = PublishDelivery("epic-unused.txt", "unrelated\n");
        var deps = Build(deliverySha);
        var epicFolder = Path.Combine(_watchPath, TaskStates.HumanReview, "planned-epic");
        Directory.CreateDirectory(epicFolder);
        File.WriteAllText(
            Path.Combine(epicFolder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id = "planned-epic",
                    title = "Planned epic",
                    state = TaskStates.HumanReview,
                    order = 2,
                    agent = "codex",
                    cliType = "codex",
                    kind = TaskKinds.Epic,
                    mode = TaskModes.Coding,
                    projectName = Project,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(epicFolder, "status.md"), "Result: decomposed.\n");
        deps.Scanner.InvalidateCache();

        var outcome = await deps.Transitions.MoveAsync("planned-epic", TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var accepted = deps.Scanner.FindJob("planned-epic", _watchPath);
        Assert.NotNull(accepted);
        Assert.Equal(TaskStates.Completed, accepted!.State);
    }

    [Fact]
    public async Task AcceptRemoteDelivery_DoesNotMergePendingDelivery()
    {
        Assert.Equal(
            IntegrationStatuses.PendingTag,
            TaskMutationService.NormalizeTagId("integration:pending"));

        var deliverySha = PublishDelivery("delivery.txt", "remote work\n");
        var deps = Build(deliverySha);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "main").Code);

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Contains(reviewed.Tags, IntegrationStatuses.IsPendingTag);

        var mergeStep = deps.Pipeline.Read(reviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RemoteSimulation_GreenSettledEnvelope_IntegratesBeforeHumanReview_AndAcceptanceIsIdempotent()
    {
        var deliverySha = PublishDelivery("remote-immediate.txt", "remote work\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview);
        var autoReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        var decision = RemoteDeliveryIntegrationPolicy.Decide(
            hasSettledResultEnvelope: true,
            reviewOutcome: "Pass",
            new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
                [new AgentStudio.TaskServer.Contracts.ReviewCommandDto(
                    "verify",
                    "build-tests",
                    "git",
                    ["status", "--short"])],
                ["build-tests"]),
            [new AgentStudio.TaskServer.Contracts.ReviewVerdictDto(
                "build-tests",
                "pass",
                "GatePassed",
                "Remote build and tests passed.")]);
        Assert.True(decision.ShouldIntegrate);

        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            deps.Merge,
            deps.Scanner,
            deps.Provenance,
            deps.Pipeline,
            deps.Timeline,
            AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);
        var result = await coordinator.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            Project,
            autoReview.Id,
            autoReview.FolderPath,
            autoReview.WatchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            ReviewSubjectStore.Read(autoReview.FolderPath)!.CompletedAtUtc));

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        var integratedBeforeReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, integratedBeforeReview.State);
        Assert.Equal(
            IntegrationStatuses.Integrated,
            Assert.Single(deps.Integration.BuildLookup([integratedBeforeReview]).Values).Status);
        // Integrate-on-delivery records its start as well as its outcome, so the
        // integration span is read from the ledger pair, not from the merge step.
        var ledger = deps.Timeline.ReadAll(integratedBeforeReview.FolderPath);
        var started = Assert.Single(ledger, entry => entry.Kind == TimelineEventKinds.IntegrationStarted);
        var succeeded = Assert.Single(ledger, entry => entry.Kind == TimelineEventKinds.IntegrationSucceeded);
        Assert.Equal(RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage, started.Details!["stage"]);
        Assert.Equal("develop", started.Details["integrationBranch"]);
        Assert.Equal(RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage, succeeded.Details!["stage"]);
        Assert.True(started.Ts <= succeeded.Ts);

        var moved = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.HumanReview,
            _watchPath,
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var humanReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, humanReview.State);
        Assert.Equal(
            IntegrationStatuses.Integrated,
            Assert.Single(deps.Integration.BuildLookup([humanReview]).Values).Status);

        var developBeforeAcceptance = Git(_repo, "rev-parse", "develop").Out.Trim();
        var mergeStepBeforeAcceptance = deps.Pipeline.Read(humanReview.FolderPath)!.Steps.Single(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        var accepted = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Equal(developBeforeAcceptance, Git(_repo, "rev-parse", "develop").Out.Trim());
        var mergeStepAfterAcceptance = deps.Pipeline.Read(completed.FolderPath)!.Steps.Single(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(mergeStepBeforeAcceptance.Status, mergeStepAfterAcceptance.Status);
        Assert.Equal(mergeStepBeforeAcceptance.Verdict, mergeStepAfterAcceptance.Verdict);
        Assert.Equal(mergeStepBeforeAcceptance.CompletedAt, mergeStepAfterAcceptance.CompletedAt);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task UnresolvedImmediateIntegration_AutomaticallyStartsSteerRoundBeforeHumanReview()
    {
        var deliverySha = PublishDelivery("shared.txt", "delivery version\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "develop version\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "develop edits shared");
        RunGit(_repo, "checkout", "-q", "main");
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            initialState: TaskStates.AutoReview);
        var autoReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            deps.Merge,
            deps.Scanner,
            deps.Provenance,
            deps.Pipeline,
            deps.Timeline,
            AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);

        var immediate = await coordinator.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            Project,
            autoReview.Id,
            autoReview.FolderPath,
            autoReview.WatchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            ReviewSubjectStore.Read(autoReview.FolderPath)!.CompletedAtUtc));
        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, immediate.Outcome);
        var queued = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent?.Mode);
        Assert.Equal(
            IntegrationAgentRoundService.AttributionAmbiguousReason,
            queued.PendingIntent?.SavedReason);
        Assert.Contains("direct merge", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-to-one", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            deps.Timeline.ReadAll(queued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                     && entry.Summary.Contains("Automatically started a new agent round", StringComparison.Ordinal));
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task CardinalityAmbiguity_AutomaticallyRequeuesWithSteerNote()
    {
        var deliverySha = PublishDelivery("cardinality.txt", "delivery version\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview);
        var job = deps.Scanner.FindJob(Slug, _watchPath)!;
        var request = new RemoteDeliveryIntegrationRequest(
            Project,
            job.Id,
            job.FolderPath,
            job.WatchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            DateTimeOffset.UtcNow);
        var result = MergeIntoIntegrationResult.RequiresAgentRound(
            [],
            "Mechanical rebase changed the delivery commit cardinality; refusing ambiguous SHA attribution.");

        var started = await AgentRounds(deps).TryStartAsync(request, result);

        Assert.True(started.Started, started.Reason);
        var queued = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent?.Mode);
        Assert.Contains("cardinality", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not squash, split, drop, or combine", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        var queuedLedger = deps.Timeline.ReadAll(queued.FolderPath);
        Assert.Contains(
            "Automatically started a new agent round to preserve unambiguous delivery SHA attribution.",
            queuedLedger.Select(entry => entry.Summary));
        Assert.Contains(
            "Automatic integration recovery",
            File.ReadAllText(Path.Combine(queued.FolderPath, "prompt.md")),
            StringComparison.Ordinal);
        // The requeue names its cause on the lane row itself.
        var laneChange = Assert.Single(queuedLedger, entry => entry.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TaskStates.Ready, laneChange.Details!["to"]);
        Assert.Equal(LaneChangeCauses.IntegrationRecovery, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal(
            MergeIntoIntegrationOutcome.AgentRoundRequired.ToString(),
            laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public async Task Accept_StaleSubjectCannotBypassAttemptCheckWhenOldDeliveryIsAlreadyIntegrated()
    {
        var deliverySha = PublishDelivery("stale.txt", "superseded remote work\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "-m", "integrate stale delivery", deliverySha);
        RunGit(_repo, "checkout", "-q", "main");
        var deps = Build(deliverySha);
        var staleSubject = ReviewSubjectStore.Read(
            Path.Combine(_watchPath, TaskStates.HumanReview, Slug));
        Assert.NotNull(staleSubject);

        var currentRun = deps.Authority.AcquireRun(
            TaskKey,
            "PROJ-FIXTURE",
            staleSubject!.RunAttemptId,
            "local",
            "host-local",
            60,
            "claim-current").RunAttempt;
        Assert.NotNull(currentRun);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.Failure, outcome.Status);
        Assert.Contains(staleSubject.RunAttemptId, outcome.Message);
        Assert.Contains(currentRun!.AttemptId, outcome.Message);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
    }

    [Fact]
    public async Task AcceptHttp_DoesNotStartColdGateForPendingDelivery()
    {
        var deliverySha = PublishDelivery("cold-gate.txt", "remote work\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "checkout", "-q", "main");
        var deps = Build(deliverySha);
        deps.Settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var gate = new BlockingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            "CS0103: the merge does not compile",
            "backend build exit 1",
            true,
            false));

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _tempDir,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IBuildTestGateRunner>();
                    services.RemoveAll<ProjectSettingsService>();
                    services.AddSingleton(deps.Settings);
                    services.AddSingleton<IBuildTestGateRunner>(gate);
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        Assert.Same(gate, factory.Services.GetRequiredService<IBuildTestGateRunner>());
        Assert.True(PreDevelopBuildGate.AppliesTo(
            factory.Services.GetRequiredService<ProjectSettingsService>().Get(Project).BuildProfile));

        var queue = factory.Services.GetRequiredService<AcceptedIntegrationQueue>();
        var responseTask = client.PostAsJsonAsync(
            $"/api/tasks/{Slug}/move?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { targetState = TaskStates.Completed });
        using var acceptanceResponse = await responseTask.WaitAsync(AsyncTestDeadline);
        Assert.Equal(HttpStatusCode.Conflict, acceptanceResponse.StatusCode);
        var stillReviewed = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(Slug, _watchPath);
        Assert.NotNull(stillReviewed);
        Assert.Equal(TaskStates.HumanReview, stillReviewed!.State);
        Assert.Null(stillReviewed.Phase);
        Assert.False(queue.Reader.TryRead(out _));
        Assert.False(gate.Entered.IsCompleted);
    }

    [Fact]
    public async Task LegacyCompletedAccept_NoTaskBranch_ReturnsToHumanReviewWithVisibleReason()
    {
        var deliverySha = PublishDelivery("orphaned-delivery.txt", "fenced work\n");
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            writeReviewSubject: false);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        File.WriteAllText(
            Path.Combine(reviewed.FolderPath, "status.md"),
            "# Status\n\n- Result: Success\n");

        // Reproduce the 09 August incident: the old accept path moved the card
        // before the queued worker discovered that task/<slug> did not exist.
        var legacyAccept = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, legacyAccept.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;

        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            deps.Merge,
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline);
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project,
            Slug,
            completed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge));

        Assert.Equal(MergeIntoIntegrationOutcome.NoTaskBranch, result.Outcome);
        var returned = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(returned);
        Assert.Equal(TaskStates.HumanReview, returned!.State);
        Assert.Null(returned.Phase);
        var integration = deps.Integration.BuildLookup([returned])[returned.TaskKey];
        Assert.Equal(IntegrationStatuses.ConflictSkipped, integration.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.NoTaskBranch, integration.Failure?.Code);
        Assert.Equal("No task branch", integration.Failure?.Label);
        Assert.False(integration.Failure?.RebaseRecoveryAvailable);
        var status = File.ReadAllText(Path.Combine(returned.FolderPath, "status.md"));
        Assert.Contains("## Acceptance integration", status);
        Assert.Contains("- Outcome: `NoTaskBranch`", status);
        Assert.Contains("task/remote-delivery", status);
    }

    [Fact]
    public async Task GateEnvironmentFailure_KeepsTheAcceptancePendingAndRetryable()
    {
        // AGT-2720 acceptance, driven against the CAC-18 subject shape: the
        // pre-develop gate dies inside vite before the first test. The delivery
        // must NOT integrate, but the card must also NOT be marked partial or
        // conflict-skipped and must NOT be parked in Human Review - it stays
        // pending with the named reason and the backstop retries it.
        var deliverySha = PublishDelivery("cac-18-delivery.txt", "unevaluated work\n");
        var gate = new GateEnvironmentBuildTestGateRunner();
        var deps = Build(deliverySha, backgroundIntegration: true, gateRunner: gate);
        var legacyAccept = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, legacyAccept.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;

        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            deps.Merge,
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline);
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project,
            Slug,
            completed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge));

        Assert.Equal(1, gate.Invocations);
        Assert.True(result.GateEnvironmentFailure);
        Assert.False(result.Outcome.IsSuccessfulIntegration());
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        // The card keeps its lane, so the accepted-integration backstop still
        // owns it instead of an operator having to re-accept an untested card.
        var afterAttempt = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, afterAttempt.State);
        Assert.DoesNotContain(
            "## Acceptance integration",
            File.Exists(Path.Combine(afterAttempt.FolderPath, "status.md"))
                ? File.ReadAllText(Path.Combine(afterAttempt.FolderPath, "status.md"))
                : string.Empty);

        var integration = deps.Integration.BuildLookup([afterAttempt])[afterAttempt.TaskKey];
        Assert.Equal(IntegrationStatuses.Pending, integration.Status);
        Assert.NotEqual(IntegrationStatuses.Partial, integration.Status);
        Assert.NotEqual(IntegrationStatuses.ConflictSkipped, integration.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironment, integration.Failure?.Code);
        Assert.Contains("vite case-insensitive FS probe failed", integration.Detail!);
        Assert.Contains("dependency-cache hit", integration.Detail!);

        var mergeStep = deps.Pipeline.Read(afterAttempt.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal("gate-environment", mergeStep?.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironment, mergeStep?.FailureCode);

        var recovery = deps.Integration.ResolveAcceptedIntegrationRecovery(afterAttempt, integration);
        Assert.Equal(AcceptedIntegrationRecoveryAction.Retry, recovery.Action);

        // The notice is the durable counter that bounds the retry, so the
        // backstop cannot loop a broken gate host forever.
        var notice = Assert.Single(
            deps.Timeline.ReadAll(afterAttempt.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationFailed
                     && entry.Details?.GetValueOrDefault("failureCode")
                        == AcceptedIntegrationFailureCodes.GateEnvironment);
        Assert.Contains("retries", notice.Summary);
    }

    [Fact]
    public async Task OperatorOverride_CompletesWithoutStartingIntegration_AndRecordsAuditReason()
    {
        var deliverySha = PublishDelivery("override.txt", "operator accepted\n");
        var deps = Build(deliverySha, backgroundIntegration: true);

        var result = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            reason: "Concept outcome has no branch to merge.",
            operatorOverride: true);

        Assert.Equal(MoveJobStatus.Success, result.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        Assert.Equal(TaskStates.Completed, completed!.State);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        var mergeStep = deps.Pipeline.Read(completed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal("operator-override", mergeStep!.Verdict);
        Assert.Contains(
            deps.Timeline.ReadAll(completed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationOverridden
                     && entry.Details?.GetValueOrDefault("outcome") == "OperatorOverride");
        var status = File.ReadAllText(Path.Combine(completed.FolderPath, "status.md"));
        Assert.Contains("- Outcome: `OperatorOverride`", status);
        Assert.Contains("Concept outcome has no branch to merge.", status);
    }

    [Fact]
    public async Task AcceptRemoteDelivery_UsesConfiguredPullRequestStrategy_AndReturnsToReview()
    {
        var deliverySha = PublishDelivery("pull-request.txt", "review handoff\n");
        var deps = Build(deliverySha, IntegrationStrategies.PullRequest);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
        Assert.Contains(reviewed.Tags, IntegrationStatuses.IsPendingTag);

        var mergeStep = deps.Pipeline.Read(reviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);

        var integration = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationStatuses.Pending, integration.Status);
        Assert.DoesNotContain(
            deps.Timeline.ReadAll(reviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationFailed);
    }

    [Fact]
    public async Task AcceptRemoteDelivery_WithConflict_LeavesVisibleConflictAndRecoveryHint()
    {
        var deliverySha = PublishDelivery("shared.txt", "delivery version\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "develop version\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "develop edits shared");
        var developBefore = Git(_repo, "rev-parse", "develop").Out.Trim();
        var deps = Build(deliverySha);
        var delivery = deps.Scanner.FindJob(Slug, _watchPath)!;
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            deps.Merge,
            deps.Scanner,
            deps.Provenance,
            deps.Pipeline,
            deps.Timeline,
            AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance);
        var immediate = await coordinator.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            Project,
            Slug,
            delivery.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            DateTimeOffset.UtcNow));
        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, immediate.Outcome);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.Equal(developBefore, Git(_repo, "rev-parse", "develop").Out.Trim());
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
        Assert.Contains(
            reviewed.Tags,
            IntegrationStatuses.IsPendingTag);

        var mergeStep = deps.Pipeline.Read(reviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Failed, mergeStep!.Status);
        Assert.Equal("agent-round-required", mergeStep.Verdict);
        Assert.Equal(
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous,
            mergeStep.FailureCode);
        Assert.Contains("shared.txt", mergeStep.VerdictSummary);

        var integration = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationStatuses.ConflictSkipped, integration.Status);
        Assert.Contains("Mechanical rebase conflicted", integration.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            deps.Timeline.ReadAll(reviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationFailed);
    }

    [Fact]
    public async Task BackgroundAccept_DoesNotQueuePendingIntegration()
    {
        var deliverySha = PublishDelivery("background.txt", "remote work\n");
        var deps = Build(deliverySha, backgroundIntegration: true);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
        Assert.Contains(reviewed.Tags, IntegrationStatuses.IsPendingTag);
        Assert.DoesNotContain(
            deps.Timeline.ReadAll(reviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationPendingWarning);
        Assert.NotNull(deps.AcceptedQueue);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AcceptOutOfBandIntegratedCard_CompletesWithoutOwnMergeAttempt()
    {
        var deliverySha = PublishDelivery("out-of-band.txt", "already integrated\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        RunGit(_repo, "checkout", "-q", "main");
        var deps = Build(deliverySha, backgroundIntegration: true);

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var statusBeforeAccept = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, statusBeforeAccept.Status);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        Assert.Equal(TaskStates.Completed, completed!.State);
        Assert.Null(completed.Phase);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        var mergeStep = deps.Pipeline.Read(completed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
        Assert.DoesNotContain(
            deps.Timeline.ReadAll(completed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationSucceeded
                     && entry.Details?.GetValueOrDefault("outcome") == "AlreadyMerged");
    }

    [Fact]
    public async Task AcceptMainOnlyProject_WithoutExplicitIntegrationBranch_DoesNotIntegrate()
    {
        var deliverySha = PublishDelivery("main-only-delivery.txt", "remote work\n");
        RunGit(_repo, "push", "-q", "origin", "--delete", "develop");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(
            deliverySha,
            gateRunner: gate,
            configureIntegrationBranch: false,
            recordedIntegrationBranch: "refs/heads/develop");

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var before = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal("main", before.IntegrationBranch);
        Assert.Equal(IntegrationStatuses.Pending, before.Status);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "main").Code);
        Assert.Equal(0, gate.Invocations);
        var stillReviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, stillReviewed.State);
        var mergeStep = deps.Pipeline.Read(stillReviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
    }

    [Fact]
    public async Task AcceptAlreadyIntegratedMainOnlyCard_MovesWithoutMergeOrGate()
    {
        var deliverySha = PublishDelivery("main-only-integrated.txt", "already integrated\n");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        RunGit(_repo, "push", "-q", "origin", "--delete", "develop");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            gateRunner: gate,
            configureIntegrationBranch: false,
            recordedIntegrationBranch: "refs/heads/develop");

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var before = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, before.Status);
        Assert.Equal("main", before.IntegrationBranch);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)?.State);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        Assert.Equal(0, gate.Invocations);
        var mergeStep = deps.Pipeline.Read(
                deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
    }

    [Fact]
    public async Task AcceptOutOfBandIntegratedCard_WithStaleLocalIntegrationBranch_DoesNotFetchOrIntegrate()
    {
        var deliverySha = PublishDelivery("out-of-band-remote.txt", "already on origin\n");
        RunGit(_repo, "branch", "develop", "origin/develop");
        var staleLocalTip = Git(_repo, "rev-parse", "develop").Out.Trim();

        var integrator = Path.Combine(_tempDir, "integrator");
        RunGit(_tempDir, "clone", "-q", _origin, integrator);
        RunGit(integrator, "config", "user.email", "test@example.com");
        RunGit(integrator, "config", "user.name", "test");
        RunGit(integrator, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(integrator, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        RunGit(integrator, "push", "-q", "origin", "develop");
        var remoteTip = Git(integrator, "rev-parse", "develop").Out.Trim();

        Assert.NotEqual(staleLocalTip, remoteTip);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "origin/develop").Code);

        var gate = new CountingBuildTestGateRunner();
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            gateRunner: gate);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        var stillReviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(stillReviewed);
        Assert.Equal(TaskStates.HumanReview, stillReviewed!.State);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(staleLocalTip, Git(_repo, "rev-parse", "develop").Out.Trim());
        Assert.NotEqual(remoteTip, Git(_repo, "rev-parse", "origin/develop").Out.Trim());
        var mergeStep = deps.Pipeline.Read(stillReviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
    }

    [Fact]
    public async Task Accept_WithDivergedLocalIntegrationBranch_DoesNotSynchronizeOrIntegrate()
    {
        var deliverySha = PublishDelivery("diverged-delivery.txt", "delivery\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "local-integration.txt"), "local\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "chore: local integration work");
        var localTip = Git(_repo, "rev-parse", "develop").Out.Trim();
        RunGit(_repo, "checkout", "-q", "main");

        var integrator = Path.Combine(_tempDir, "diverged-integrator");
        RunGit(_tempDir, "clone", "-q", _origin, integrator);
        RunGit(integrator, "config", "user.email", "test@example.com");
        RunGit(integrator, "config", "user.name", "test");
        RunGit(integrator, "checkout", "-q", "-b", "develop", "origin/develop");
        File.WriteAllText(Path.Combine(integrator, "remote-integration.txt"), "remote\n");
        RunGit(integrator, "add", "-A");
        RunGit(integrator, "commit", "-q", "-m", "chore: remote integration work");
        RunGit(integrator, "push", "-q", "origin", "develop");

        var gate = new CountingBuildTestGateRunner();
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            gateRunner: gate);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        Assert.Contains("Acceptance does not integrate deliveries", accepted.Message, StringComparison.Ordinal);
        var apiResult = TaskEndpointHelpers.MoveResult(accepted);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(apiResult).StatusCode);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(localTip, Git(_repo, "rev-parse", "develop").Out.Trim());

        var mergeStep = deps.Pipeline.Read(reviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
        var integration = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationStatuses.Pending, integration.Status);
        Assert.DoesNotContain(
            deps.Timeline.ReadAll(reviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationFailed);
    }

    [Fact]
    public async Task AcceptedIntegrationWorker_Shutdown_DrainsActiveMergeGate()
    {
        var deliverySha = PublishDelivery("worker-drain.txt", "remote work\n");
        var gate = new BlockingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            "CS0103: the merge does not compile",
            "backend build exit 1",
            true,
            false));
        var deps = Build(
            deliverySha,
            backgroundIntegration: true,
            gateRunner: gate);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        deps.Mutations.SetJobPhase(reviewed.FolderPath, LifecyclePhases.Integrating);
        Assert.True(deps.AcceptedQueue!.Enqueue(new AcceptedIntegrationRequest(
            Project,
            Slug,
            reviewed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge)));

        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            deps.Merge,
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline);
        await worker.StartAsync(CancellationToken.None);
        await gate.Entered.WaitAsync(AsyncTestDeadline);
        Assert.True(deps.Merge.IsMergeGateBusy);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _tempDir,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<MergeIntoDevelopRunner>();
                    services.AddSingleton(deps.Merge);
                });
            });
        using var client = factory.CreateClient();
        Assert.Equal("gate-busy", await client.GetStringAsync("/healthz/drain"));

        var stopTask = worker.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);
        Assert.True(deps.Merge.IsMergeGateBusy);

        gate.Release();
        await stopTask.WaitAsync(AsyncTestDeadline);
        Assert.False(deps.Merge.IsMergeGateBusy);
        Assert.Equal("idle", await client.GetStringAsync("/healthz/drain"));

        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        Assert.Equal(TaskStates.HumanReview, completed!.State);
        Assert.Null(completed.Phase);
        var mergeStep = deps.Pipeline.Read(completed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Failed, mergeStep!.Status);
        Assert.Equal("gate-failed", mergeStep.Verdict);
        worker.Dispose();
    }

    [Fact]
    public async Task ConflictRecoveryAction_QueuesFocusedSteerRoundOnExistingDelivery()
    {
        var deliverySha = PublishDelivery("shared.txt", "delivery version\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "develop version\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "develop edits shared");
        var deps = Build(deliverySha);

        var conflicted = deps.Scanner.FindJob(Slug, _watchPath)!;
        deps.Pipeline.RecordStep(conflicted.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "conflict",
            VerdictSummary = "Conflicted: shared.txt.",
            Reason = "Merge conflict in one file; merge aborted.",
            FailureCode = AcceptedIntegrationFailureCodes.MergeConflict,
        });

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)?.State);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _tempDir,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                    });
                });
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PostAsync(
            $"/api/tasks/{Slug}/integration/rebase?watchPath={Uri.EscapeDataString(_watchPath)}",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(ContinueModes.Steer, body.RootElement.GetProperty("mode").GetString());
        Assert.Equal(DeliveryRef, body.RootElement.GetProperty("deliveryRef").GetString());
        Assert.Equal("develop", body.RootElement.GetProperty("integrationBranch").GetString());

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, Slug);
        Assert.True(Directory.Exists(readyFolder));
        var intent = JsonSerializer.Deserialize<PendingIntent>(
            File.ReadAllText(Path.Combine(readyFolder, "pending-intent.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(intent);
        Assert.Equal(ContinueModes.Steer, intent!.Mode);
        Assert.Equal(AcceptedIntegrationFailureCodes.MergeConflict, intent.SavedReason);
        Assert.Contains(DeliveryRef, intent.Prompt, StringComparison.Ordinal);
        Assert.Contains("rebase", intent.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            TimelineEventKinds.IntegrationRecoveryQueued,
            File.ReadAllText(TaskPaths.TimelineLog(readyFolder)));
        using var readyJson = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(readyFolder, "task.json")));
        var supersededCommit = readyJson.RootElement
            .GetProperty("commits")
            .EnumerateArray()
            .Single();
        Assert.Equal(
            TaskCommitSupersession.PendingAttempt,
            supersededCommit.GetProperty("supersededByAttempt").GetString());
    }

    [Fact]
    public async Task SourceNeedsRebaseRecovery_QueuesFocusedSteerRound()
    {
        var deliverySha = PublishDelivery("behind-main.txt", "delivery version\n");
        var deps = Build(deliverySha);
        var job = deps.Scanner.FindJob(Slug, _watchPath)!;
        deps.Pipeline.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "error",
            Reason = "Release source 'origin/result' must be rebased onto 'develop' before the full-suite gate.",
            FailureCode = AcceptedIntegrationFailureCodes.SourceNeedsRebase,
        });

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _tempDir,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                    });
                });
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PostAsync(
            $"/api/tasks/{Slug}/integration/rebase?watchPath={Uri.EscapeDataString(_watchPath)}",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, Slug);
        var intent = JsonSerializer.Deserialize<PendingIntent>(
            File.ReadAllText(Path.Combine(readyFolder, "pending-intent.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(AcceptedIntegrationFailureCodes.SourceNeedsRebase, intent?.SavedReason);
        Assert.Contains("rebase", intent?.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IntegrationTagMutation_NeverExposesTruncatedTaskJsonToConcurrentReader()
    {
        var folder = Path.Combine(_tempDir, "atomic-task-json");
        var path = Path.Combine(folder, "task.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, """{"id":"remote-delivery","tags":[]}""");

        var failures = new ConcurrentQueue<Exception>();
        using var started = new ManualResetEventSlim();
        var firstSuccessfulRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulReadAfterWrites = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = true;
        var writesCompleted = false;
        long successfulReads = 0;
        var reader = Task.Run(() =>
        {
            started.Set();
            while (Volatile.Read(ref reading))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    Assert.Equal(Slug, document.RootElement.GetProperty("id").GetString());
                    Interlocked.Increment(ref successfulReads);
                    firstSuccessfulRead.TrySetResult();
                    if (Volatile.Read(ref writesCompleted))
                        successfulReadAfterWrites.TrySetResult();
                }
                catch (IOException ex) when (IsRetryableFileSharingViolation(ex))
                {
                    // Windows: a plain open that lands on the instant of the
                    // rename-with-replace can be refused with a sharing
                    // violation. That is OS behaviour, not a truncation: the
                    // invariant under test is that a reader that GETS the file
                    // never sees partial JSON, a foreign id, or a missing name.
                    // Only sharing/lock collisions retry; every other
                    // IOException (FileNotFound included) remains a failure.
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                    if (!firstSuccessfulRead.TrySetException(ex))
                        successfulReadAfterWrites.TrySetException(ex);
                }
                // Consumer cadence: open, read, close, come back a moment later.
                // A reader re-opening the file back-to-back starves the single
                // rename on a saturated CPU and is not a pattern any task.json
                // consumer has.
                Thread.Sleep(1);
            }
        });
        started.Wait();
        try
        {
            await firstSuccessfulRead.Task.WaitAsync(AsyncTestDeadline);

            var tags = Enumerable.Range(0, 2_000).Select(i => $"integration:test-{i:D4}").ToArray();
            for (var i = 0; i < 100; i++)
            {
                TaskJsonFile.UpdateField(
                    folder,
                    "tags",
                    i % 2 == 0 ? tags : new[] { IntegrationStatuses.PendingTag },
                    NullLogger.Instance);
            }

            Volatile.Write(ref writesCompleted, true);
            await successfulReadAfterWrites.Task.WaitAsync(AsyncTestDeadline);
        }
        finally
        {
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(AsyncTestDeadline);
        }

        Assert.Empty(failures);
        Assert.True(Interlocked.Read(ref successfulReads) >= 2);
        using var finalDocument = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(Slug, finalDocument.RootElement.GetProperty("id").GetString());
        Assert.Equal(
            [IntegrationStatuses.PendingTag],
            finalDocument.RootElement.GetProperty("tags")
                .EnumerateArray()
                .Select(tag => tag.GetString()));
    }

    [Fact]
    public async Task IntegrationPushQueue_DroppedByRestart_IsRecoveredFromDurablePipelineFacts()
    {
        var deliverySha = PublishDelivery("restart.txt", "survives restart\n");
        var deps = Build(deliverySha);
        var immediate = await deps.Merge.RunAsync(
            Project,
            Slug,
            deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath,
            _watchPath,
            "develop",
            CancellationToken.None,
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, immediate.Outcome);
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);

        var localDevelop = Git(_repo, "rev-parse", "develop").Out.Trim();
        var remoteBefore = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.NotEqual(localDevelop, remoteBefore);

        // Archiving must not erase the durable recovery fact. Production's
        // ScanAllJobs snapshot deliberately omits 7-archive, so the backstop
        // must use the archive-inclusive scanner.
        var archived = deps.States.MoveJob(Slug, TaskStates.Archive, _watchPath);
        Assert.Equal(MoveJobStatus.Success, archived.Status);

        // Simulate a fresh backend process: the in-memory IntegrationPushQueue
        // is gone, but pipeline-execution.json still proves merge=Passed and
        // push=Pending. The startup backstop must re-drive the push.
        var backstop = new IntegrationPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Pipeline,
            deps.Merge,
            deps.Configuration,
            NullLogger<IntegrationPushBackstopHostedService>.Instance);
        var recovered = await backstop.RunOnceAsync();

        Assert.Equal(1, recovered);
        var remoteAfter = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.Equal(localDevelop, remoteAfter);
    }

    [Fact]
    public void AcceptanceMerge_InterruptedByRestart_IsRecoveredFromCompletedLane()
    {
        var deliverySha = PublishDelivery("accept-restart.txt", "durable lane move\n");
        var deps = Build(deliverySha);

        // Simulate a process that durably moved the lane but stopped before
        // TaskTransitionService could run its post-move merge side effect.
        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance);
        var recovered = backstop.RunOnce();

        Assert.Equal(1, recovered);
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        Assert.DoesNotContain(
            completed!.Tags,
            IntegrationStatuses.IsPendingTag);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void AcceptedAlert_ExternalRecordAppendConvergesOnNextRefreshDespiteStaleBoardCache()
    {
        var deliverySha = PublishDelivery("alert-refresh.txt", "stalled acceptance\n");
        var deps = Build(deliverySha);
        Assert.Equal(
            MoveJobStatus.Success,
            deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath).Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var acceptedAt = DateTime.UtcNow.AddMinutes(-45);
        deps.Timeline.Append(completed.FolderPath, new TimelineEvent
        {
            Kind = TimelineEventKinds.IntegrationStarted,
            Actor = TimelineActors.System,
            Summary = "Acceptance integration started.",
            Ts = acceptedAt,
        });

        var cache = new TaskIndexCache(
            deps.Scanner,
            NullLogger<TaskIndexCache>.Instance,
            deps.Configuration);
        deps.Scanner.SetIndexCache(cache);
        Assert.Null(TaskIntegrationRecordDetector.LatestVerification(
            Assert.Single(deps.Scanner.ScanAllAutomationJobsWithArchive())));
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            timeline: deps.Timeline,
            pipelineLog: deps.Pipeline);
        Assert.Equal(1, backstop.RefreshAlert(DateTime.UtcNow).StalledTaskCount);

        TaskJsonFile.UpdateFieldOrThrow(
            completed.FolderPath,
            "integrationRecords",
            new[]
            {
                new TaskIntegrationRecord
                {
                    Id = "operator-legacy-backfill-v1",
                    Version = 1,
                    Classification = IntegrationRecordClasses.NoAttributionLegacy,
                    RecordedAtUtc = DateTime.UtcNow,
                    AcceptedAtUtc = acceptedAt,
                    Evidence = "Legacy card predates commit attribution and has no surviving delivery evidence.",
                },
            });
        Assert.Null(TaskIntegrationRecordDetector.LatestVerification(
            Assert.Single(deps.Scanner.ScanAllAutomationJobsWithArchive())));

        var refreshed = backstop.RefreshAlert(DateTime.UtcNow.AddMinutes(15));

        Assert.False(refreshed.Active);
        Assert.Equal(0, refreshed.StalledTaskCount);
    }

    [Fact]
    public void LegacyTransactionalAcceptance_InterruptedByRestart_ResumesFromIntegratingReview()
    {
        var deliverySha = PublishDelivery("transactional-restart.txt", "durable integrating phase\n");
        var deps = Build(deliverySha, backgroundIntegration: true);
        var initial = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.False(File.Exists(Path.Combine(initial.FolderPath, "status.md")));

        deps.Mutations.SetJobPhase(initial.FolderPath, LifecyclePhases.Integrating);
        var integrating = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(integrating);
        Assert.Equal(TaskStates.HumanReview, integrating!.State);
        Assert.Equal(LifecyclePhases.Integrating, integrating.Phase);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        // Simulate a restart that drops the volatile queue item. The durable
        // Human Review + integrating phase is the transaction recovery record.
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions,
            deps.Timeline);
        var recovered = backstop.RunOnce();

        Assert.Equal(1, recovered);
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        Assert.Equal(TaskStates.Completed, completed!.State);
        Assert.Null(completed.Phase);
        Assert.DoesNotContain(completed.Tags, IntegrationStatuses.IsPendingTag);
        Assert.Contains(
            deps.Timeline.ReadAll(completed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationSucceeded);
        var status = File.ReadAllText(Path.Combine(completed.FolderPath, "status.md"));
        Assert.Contains("<!-- agent-studio:result-scaffold -->", status);
        Assert.Contains("- Result: Success", status);
        Assert.Contains("- Integration: `integrated`", status);
    }

    [Fact]
    public void AcceptanceMerge_PassedStepWithoutGitPresence_IsRevalidated()
    {
        var deliverySha = PublishDelivery("lying-step.txt", "git truth wins\n");
        var deps = Build(deliverySha);

        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var completedBefore = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completedBefore);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        // Reproduce the status contradiction from AGT-2424: the pipeline says
        // Passed while the reviewed ResultSha is not reachable from develop.
        deps.Pipeline.RecordStep(completedBefore!.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            CompletedAt = DateTime.UtcNow,
            Verdict = "merged",
            Reason = "stale success fixture",
        });

        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance);
        var recovered = backstop.RunOnce();

        Assert.Equal(1, recovered);
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        Assert.DoesNotContain(
            deps.Scanner.FindJob(Slug, _watchPath)!.Tags,
            IntegrationStatuses.IsPendingTag);
    }

    [Fact]
    public void AcceptanceMerge_LocalDeliveryWithoutReviewSubject_IsRecoveredFromCompletedLane()
    {
        var deliverySha = PublishLocalDelivery("local-restart.txt", "local delivery\n");
        var deps = Build(deliverySha, writeReviewSubject: false);

        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);

        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance);
        var recovered = backstop.RunOnce();

        Assert.Equal(1, recovered);
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "develop").Code);
        Assert.Null(ReviewSubjectStore.Read(
            deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath));
    }

    [Fact]
    public async Task AcceptanceMerge_LandedBeforeRestart_RecoversMissingRecordAndPush()
    {
        var deliverySha = PublishDelivery("merge-record-restart.txt", "local merge survived\n");
        var deps = Build(deliverySha);

        // Simulate the narrower crash window: the lane move and local git merge
        // landed, but the process stopped before MergeIntoDevelopRunner recorded
        // the durable merge step or enqueued the integration push.
        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        var localDevelop = Git(_repo, "rev-parse", "develop").Out.Trim();
        var remoteBefore = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.NotEqual(localDevelop, remoteBefore);
        var pendingMerge = deps.Pipeline.Read(Path.Combine(_watchPath, TaskStates.Completed, Slug))?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(pendingMerge);
        Assert.Equal(PipelineStepStatus.Pending, pendingMerge!.Status);

        var mergeBackstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance);
        var recoveredMerges = mergeBackstop.RunOnce();

        Assert.Equal(0, recoveredMerges);
        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        var mergeStep = deps.Pipeline.Read(completed!.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Passed, mergeStep!.Status);
        Assert.Equal("already-merged", mergeStep.Verdict);

        var pushBackstop = new IntegrationPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Pipeline,
            deps.Merge,
            deps.Configuration,
            NullLogger<IntegrationPushBackstopHostedService>.Instance);
        var recoveredPushes = await pushBackstop.RunOnceAsync();

        Assert.Equal(1, recoveredPushes);
        var remoteAfter = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.Equal(localDevelop, remoteAfter);
        Assert.DoesNotContain(
            deps.Scanner.FindJob(Slug, _watchPath)!.Tags,
            IntegrationStatuses.IsPendingTag);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task AcceptanceMerge_LandedBeforeGateVerdict_BackstopWaitsForExactGateBeforePassingOrPush()
    {
        var deliverySha = PublishDelivery("merge-gate-restart.txt", "merge survived before gate verdict\n");
        var gate = new BlockingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "recovery gate passed",
            true,
            false));
        var pushQueue = new IntegrationPushQueue();
        var deps = Build(deliverySha, gateRunner: gate, pushQueue: pushQueue);

        // Simulate a hard process death after the merge commit exists but before
        // the exact-SHA gate verdict, pipeline step, and push request are durable.
        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        var localDevelop = Git(_repo, "rev-parse", "develop").Out.Trim();
        var remoteBefore = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.NotEqual(localDevelop, remoteBefore);

        var mergeBackstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Merge,
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance);
        var recovery = Task.Run(() => mergeBackstop.RunOnce());

        await gate.Entered.WaitAsync(AsyncTestDeadline);
        Assert.NotNull(gate.Request);
        Assert.Equal(localDevelop, gate.Request!.ExpectedSha);

        var pending = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(pending);
        Assert.Contains(pending!.Tags, IntegrationStatuses.IsPendingTag);
        var pendingMerge = deps.Pipeline.Read(pending.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(pendingMerge);
        Assert.Equal(PipelineStepStatus.Pending, pendingMerge!.Status);
        Assert.False(pushQueue.Reader.TryRead(out _));

        var pushBackstop = new IntegrationPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Pipeline,
            deps.Merge,
            deps.Configuration,
            NullLogger<IntegrationPushBackstopHostedService>.Instance);
        Assert.Equal(0, await pushBackstop.RunOnceAsync());
        Assert.Equal(remoteBefore, Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim());

        gate.Release();
        Assert.Equal(0, await recovery.WaitAsync(AsyncTestDeadline));

        var completed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(completed);
        var passedMerge = deps.Pipeline.Read(completed!.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(passedMerge);
        Assert.Equal(PipelineStepStatus.Passed, passedMerge!.Status);
        Assert.Equal("already-merged", passedMerge.Verdict);
        Assert.True(pushQueue.Reader.TryRead(out var pushRequest));
        Assert.Equal(localDevelop, pushRequest!.ApprovedSha);

        Assert.Equal(1, await pushBackstop.RunOnceAsync());
        Assert.Equal(localDevelop, Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim());
    }

    private string PublishDelivery(string relativePath, string content)
    {
        RunGit(_repo, "checkout", "-q", "-b", DeliveryRef, "origin/develop");
        File.WriteAllText(Path.Combine(_repo, relativePath), content);
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", $"feat({TaskKey}): remote delivery");
        var sha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "push", "-q", "origin", $"{DeliveryRef}:{DeliveryRef}");
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", DeliveryRef);
        RunGit(_repo, "update-ref", "-d", $"refs/remotes/origin/{DeliveryRef}");
        return sha;
    }

    private string PublishLocalDelivery(string relativePath, string content)
    {
        var branch = WorktreeTaskLifecycle.BranchFor(Slug);
        RunGit(_repo, "branch", "develop", "origin/develop");
        RunGit(_repo, "checkout", "-q", "-b", branch, "develop");
        File.WriteAllText(Path.Combine(_repo, relativePath), content);
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", $"feat({TaskKey}): local delivery");
        var sha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "checkout", "-q", "main");
        return sha;
    }

    private Deps Build(
        string deliverySha,
        string integrationStrategy = IntegrationStrategies.DirectMerge,
        bool backgroundIntegration = false,
        IBuildTestGateRunner? gateRunner = null,
        bool writeReviewSubject = true,
        IntegrationPushQueue? pushQueue = null,
        bool configureIntegrationBranch = true,
        string? recordedIntegrationBranch = null,
        string initialState = TaskStates.HumanReview)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repo,
                ["WatchPaths:0:RepositoryPath"] = _repo,
                ["TaskRepository"] = _tempDir,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        // The state machine writes the lane_changed rows; the same ledger
        // instance is read back by the integration assertions below.
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        if (configureIntegrationBranch)
            settings.SetIntegrationBranch(Project, "develop");
        settings.SetIntegrationStrategy(Project, integrationStrategy);
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        if (gateRunner != null)
            settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var authority = new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance);
        var sourceRun = authority.AcquireRun(
            TaskKey,
            "PROJ-FIXTURE",
            null,
            "agent-runner-01",
            "host-fixture",
            60,
            "claim-fixture").RunAttempt!;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                sourceRun.AttemptId,
                sourceRun.LastFence,
                sourceRun.AuthorityEpoch,
                "settle-fixture"),
            Outcome = "done",
            ResultSha = deliverySha,
            ResultEnvelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
                "PROJ-FIXTURE",
                sourceRun.AttemptId,
                Git(_repo, "rev-parse", $"{deliverySha}^").Out.Trim(),
                deliverySha,
                $"refs/heads/{DeliveryRef}",
                null,
                new string('a', 64),
                RepositoryUrl: _origin),
        });
        Assert.True(settled.Accepted);
        var merge = new MergeIntoDevelopRunner(
            git,
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: pushQueue,
            attemptAuthority: authority,
            projectSettings: settings,
            preMainTestGate: gateRunner == null ? null : new PreMainTestGate(gateRunner),
            preDevelopBuildGate: gateRunner == null ? null : new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var provenance = new TaskProvenanceService(
            git,
            settings,
            mutations,
            NullLogger<TaskProvenanceService>.Instance);
        var acceptedQueue = backgroundIntegration ? new AcceptedIntegrationQueue() : null;

        var jobFolder = Path.Combine(_watchPath, initialState, Slug);
        Directory.CreateDirectory(jobFolder);
        var job = new
        {
            id = Slug,
            key = TaskKey,
            title = "Remote delivery",
            state = initialState,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            integrationBranch = recordedIntegrationBranch,
            codeActivityDetected = true,
            tags = initialState == TaskStates.HumanReview
                ? new[] { IntegrationStatuses.PendingTag }
                : Array.Empty<string>(),
            commit = Commit(deliverySha),
            commits = new[] { Commit(deliverySha) },
        };
        File.WriteAllText(
            Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(job, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        if (writeReviewSubject)
        {
            ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
            {
                TaskKey = TaskKey,
                RunAttemptId = sourceRun.AttemptId,
                Project = Project,
                Repository = _origin,
                ResultSha = deliverySha,
                ResultRef = DeliveryRef,
                AttemptChainId = sourceRun.Lease!.LeaseId,
                Executor = "agent-runner-01",
                LeaseId = sourceRun.Lease.LeaseId,
                FencingToken = sourceRun.LastFence,
                ImmutableResultRef = DeliveryRef,
                IntegrationBranch = recordedIntegrationBranch,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        pipeline.Begin(jobFolder, PipelineCatalogue.Standard, Project, Slug);
        Assert.NotNull(scanner.FindJob(Slug, _watchPath));

        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            provenance: provenance,
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline,
            attemptAuthority: authority);
        return new Deps(
            scanner,
            states,
            mutations,
            transitions,
            pipeline,
            integration,
            settings,
            merge,
            config,
            timeline,
            provenance,
            acceptedQueue,
            authority);
    }

    private static object Commit(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "remote delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    private static IntegrationAgentRoundService AgentRounds(Deps deps)
        => new(
            deps.Scanner,
            deps.Mutations,
            deps.States,
            deps.Timeline,
            NullLogger<IntegrationAgentRoundService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = Git(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Err}");
    }

    private static bool IsRetryableFileSharingViolation(IOException exception)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var nativeErrorCode = exception.HResult & 0xffff;
        return nativeErrorCode is 32 or 33;
    }

    private static (string Out, string Err, int Code) Git(string cwd, params string[] args)
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
        return (stdout, stderr, process.ExitCode);
    }

    private sealed record Deps(
        TaskScannerService Scanner,
        TaskStateMachine States,
        TaskMutationService Mutations,
        TaskTransitionService Transitions,
        PipelineExecutionLog Pipeline,
        TaskIntegrationStatusService Integration,
        ProjectSettingsService Settings,
        MergeIntoDevelopRunner Merge,
        IConfiguration Configuration,
        TimelineLog Timeline,
        TaskProvenanceService Provenance,
        AcceptedIntegrationQueue? AcceptedQueue,
        AttemptAuthorityService Authority);

    private sealed class BlockingBuildTestGateRunner(BuildTestGateResult result)
        : IBuildTestGateRunner
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public BuildTestGateRequest? Request { get; private set; }

        public void Release() => _released.TrySetResult();

        public async Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Request = request;
            _entered.TrySetResult();
            await _released.Task.WaitAsync(ct);
            return result with
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            };
        }
    }

    /// <summary>
    /// Reproduces the CAC-18 gate: the suite dies inside vite's case-insensitive
    /// filesystem probe before a single test runs, on a dependency-cache hit.
    /// </summary>
    private sealed class GateEnvironmentBuildTestGateRunner : IBuildTestGateRunner
    {
        public const string Reason =
            "gate environment: vite case-insensitive FS probe failed; "
            + "dependency-cache hit scope=. reason=lock-unchanged; "
            + "`npm test` (frontend) exit 1";

        public int Invocations { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Fail,
                1,
                20,
                "at testCaseInsensitiveFS (node_modules/vite/dist/node/chunks/config.js:1911:42)",
                Reason,
                false,
                true)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
                FailureKind = BuildTestGateFailureKind.GateEnvironment,
                GateEnvironmentReason = "gate environment: vite case-insensitive FS probe failed",
            });
        }
    }

    private sealed class CountingBuildTestGateRunner : IBuildTestGateRunner
    {
        public int Invocations { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok,
                0,
                0,
                string.Empty,
                "gate passed",
                true,
                false)
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
}
