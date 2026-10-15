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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Contract = AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Integration-before-review regression coverage. Remote deliveries are fenced
/// by <c>logs/review-subject.json</c>; immediate integration consumes that exact
/// ref and SHA, while acceptance validates current lineage, Git ancestry, and
/// exact-tree gate evidence.
/// </summary>
public sealed class AcceptanceIntegrationRoundTripTests : IDisposable
{
    // Generous on purpose: several call sites wait on a WebApplicationFactory's
    // first HTTP round trip, which JITs and spins up the whole app in-process.
    // Under CPU contention (e.g. two backend suites running concurrently) that
    // startup alone can eat tens of seconds, so a short fixed budget flakes
    // under load even though the awaited condition is genuinely reached.
    private static readonly TimeSpan AsyncTestDeadline = TimeSpan.FromSeconds(120);
    private const string Project = "Fixture";
    private const string Slug = "remote-delivery";
    private const string TaskKey = "AGT-2227";
    private const string DeliveryRef = "runner/agent-runner-01/AGT-2227";
    // Exact-tree evidence is independent of wall-clock recency in these fixtures.
    private static readonly DateTime VerificationFixtureAtUtc = DateTime.UnixEpoch.AddDays(1);

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
        Directory.CreateDirectory(Path.Combine(epicFolder, "results"));
        File.WriteAllText(Path.Combine(epicFolder, "results", "deliverables.md"),
            "The planned child cards are the deliverable.\n");
        BriefVersionStore.Record(epicFolder, "Plan the child cards.");
        CompletionContentEvidence.StampLocalRun(epicFolder);
        File.WriteAllText(Path.Combine(epicFolder, "aspect-requirement-fit.json"),
            "{\"status\":\"pass\",\"summary\":\"Child cards cover the plan.\"}");
        File.WriteAllText(Path.Combine(epicFolder, CompletionContentEvidence.LocalContextFile),
            JsonSerializer.Serialize(new
            {
                briefVersion = CompletionContentEvidence.ReadLocalRunVersion(epicFolder),
            }));
        deps.Scanner.InvalidateCache();

        var outcome = await deps.Transitions.MoveAsync("planned-epic", TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var accepted = deps.Scanner.FindJob("planned-epic", _watchPath);
        Assert.NotNull(accepted);
        Assert.Equal(TaskStates.Completed, accepted!.State);
        Assert.NotNull(accepted.CompletionClaim);
        Assert.Equal("pass", accepted.CompletionClaim.ContentStatus);
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(accepted.CompletionClaim, accepted.Mode));
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            deps.Authority);
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
        // AGT-2849: the merge landed in the local develop; the origin push is the
        // next, separately backstopped step, so the honest verdict until then is
        // merged-locally rather than a claim that the delivery is integrated.
        Assert.Equal(
            IntegrationStatuses.MergedLocally,
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
            IntegrationStatuses.MergedLocally,
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
        Assert.NotNull(completed.CompletionClaim);
        Assert.Equal("pass", completed.CompletionClaim.ContentStatus);
        Assert.Equal("requirement-fit", completed.CompletionClaim.ContentAspect);
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(completed.CompletionClaim, completed.Mode));
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
        // AGT-2996: the integration lane follows origin, so the conflicting
        // develop commit must be published for the conflict to be real.
        RunGit(_repo, "push", "-q", "origin", "develop");
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
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            deps.Authority);
        var reviewedSubject = ReviewSubjectStore.Read(autoReview.FolderPath)!;

        var immediate = await coordinator.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            Project,
            autoReview.Id,
            autoReview.FolderPath,
            autoReview.WatchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            reviewedSubject.CompletedAtUtc));
        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, immediate.Outcome);
        var queued = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent?.Mode);
        Assert.Equal(
            IntegrationAgentRoundService.AttributionAmbiguousReason,
            queued.PendingIntent?.SavedReason);
        Assert.Contains("direct merge", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merging the latest integration branch", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared.txt", queued.PendingIntent?.Prompt, StringComparison.Ordinal);
        Assert.Contains("one-to-one", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        var recoveryRound = Assert.Single(
            deps.Timeline.ReadAll(queued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                     && entry.Summary.Contains("Automatically started a new agent round", StringComparison.Ordinal));
        Assert.Equal(
            IntegrationRecoveryBudget.DeliveryChainId(reviewedSubject),
            recoveryRound.Details![IntegrationRecoveryBudget.DeliveryChainIdKey]);
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
        Assert.Contains("merging the latest integration branch", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-to-one", queued.PendingIntent?.Prompt, StringComparison.OrdinalIgnoreCase);
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
    public async Task IntegrationConflict_WithRecoveryBudgetSpent_StaysParkedWithExactReason()
    {
        var deliverySha = PublishDelivery("budget-spent.txt", "delivery version\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview);
        var job = deps.Scanner.FindJob(Slug, _watchPath)!;
        var subject = ReviewSubjectStore.Read(job.FolderPath)!;
        var deliveryChainId = IntegrationRecoveryBudget.DeliveryChainId(subject);
        for (var attempt = 1; attempt <= 1; attempt++)
        {
            deps.Timeline.Append(
                job.FolderPath,
                TimelineEventKinds.IntegrationRecoveryQueued,
                TimelineActors.System,
                $"Automatic integration recovery {attempt}.",
                details: new Dictionary<string, string>
                {
                    ["automatic"] = "true",
                    ["attemptEpoch"] = attempt.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    [IntegrationRecoveryBudget.DeliveryChainIdKey] = deliveryChainId,
                });
        }
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
            ["budget-spent.txt"],
            "Merge into develop conflicted.");

        var started = await AgentRounds(deps).TryStartAsync(request, result);

        Assert.False(started.Started);
        Assert.True(started.BudgetExhausted);
        Assert.Equal(1, started.BudgetUsed);
        Assert.Equal(1, started.BudgetLimit);
        Assert.Equal(
            $"automatic recovery budget used: 1/1 for delivery {deliverySha[..12]}",
            started.Reason);
        Assert.Equal(TaskStates.AutoReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
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

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.Contains("delivery brief version is missing", outcome.Message);
        Assert.NotEqual(staleSubject.RunAttemptId, currentRun!.AttemptId);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(reviewed);
        Assert.Equal(TaskStates.HumanReview, reviewed!.State);
        Assert.Null(reviewed.Phase);
    }

    /// <summary>
    /// AGT-3076 - AGT-3042 and three siblings were integrated and deployed,
    /// but the rebuilt authority store had lost their RunAttempts. Without an
    /// override the refusal is a 409 that names the missing run attempt, not
    /// the content gaps that are only its symptoms.
    /// </summary>
    [Fact]
    public async Task Accept_RunAttemptLostFromRebuiltAuthorityStore_WithoutOverride_IsRefusedWithTheReason()
    {
        var deliverySha = PublishDelivery("lost-attempt.txt", "integrated before the crash\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var deps = Build(deliverySha, authorityRebuilt: true, guarded: true);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(IntegrationStatuses.Integrated, deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey].Status);

        var refused = await deps.Transitions.MoveAsync(
            Slug, TaskStates.Completed, _watchPath, cause: TimelineActors.Human("operator"));

        Assert.Equal(MoveJobStatus.IntegrationFailed, refused.Status);
        Assert.StartsWith(AcceptanceAttemptLineagePolicy.MissingRunAttemptRefusal, refused.Message);
        Assert.Contains("(2)", refused.Message);
        Assert.Contains("operator override", refused.Message);
        var stillReviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, stillReviewed.State);
        Assert.Null(stillReviewed.CompletionClaim);
        Assert.DoesNotContain(deps.Timeline.ReadAll(stillReviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptedWithoutRunAttempt);
    }

    /// <summary>
    /// AGT-3076 - the same card accepted with a written override completes and
    /// says in its timeline that no run attempt was available, next to the
    /// delivery evidence the acceptance rests on.
    /// </summary>
    [Fact]
    public async Task Accept_RunAttemptLostFromRebuiltAuthorityStore_WithOverride_RecordsDeliveryEvidence()
    {
        var deliverySha = PublishDelivery("lost-attempt-override.txt", "integrated before the crash\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var deps = Build(deliverySha, authorityRebuilt: true, guarded: true);
        const string reason = "Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.";

        var accepted = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            cause: TimelineActors.Human("operator"),
            reason: reason,
            operatorOverride: true);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Equal(CompletionClaimBases.OperatorOverride, completed.CompletionClaim?.Basis);
        Assert.Equal(reason, completed.CompletionClaim?.Reason);
        Assert.Equal(deliverySha, completed.CompletionClaim?.ResultSha);
        Assert.Contains("No run attempt was available", completed.CompletionClaim?.Evidence);
        var recorded = Assert.Single(deps.Timeline.ReadAll(completed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptedWithoutRunAttempt);
        Assert.Contains("No run attempt was available", recorded.Summary);
        Assert.Contains("authority epoch 2", recorded.Summary);
        var details = recorded.Details!;
        Assert.Equal(reason, details["reason"]);
        Assert.Equal("2", details["authorityEpoch"]);
        Assert.Equal(IntegrationStatuses.Integrated, details["integrationStatus"]);
        Assert.Equal(deliverySha, details["resultSha"]);
        Assert.Contains(DeliveryRef, details["deliveryRef"]);
        Assert.Contains(deliverySha, details["commits"]);
        Assert.False(string.IsNullOrWhiteSpace(details["subjectRunAttemptId"]));
    }

    /// <summary>
    /// AGT-3076 - the HTTP contract the operator hit: 409 with a typed code
    /// instead of 500, the card detail says why an override is needed, and
    /// the override is accepted. AGT-3096 - the core-first detail reads the
    /// same gap from <c>/details/overview</c>, not only the legacy detail.
    /// </summary>
    [Fact]
    public async Task AcceptHttp_RunAttemptLostFromRebuiltAuthorityStore_Answers409ThenAcceptsOverride()
    {
        var deliverySha = PublishDelivery("lost-attempt-http.txt", "integrated before the crash\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var deps = Build(deliverySha, authorityRebuilt: true, guarded: true);

        using var factory = new TestHostFactory()
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
                        ["DeliveryChain:Guarded"] = "true",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<ProjectSettingsService>();
                    services.AddSingleton(deps.Settings);
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(deps.Authority);
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var query = $"?watchPath={Uri.EscapeDataString(_watchPath)}";
        var route = $"/api/tasks/{Slug}/move{query}";

        using (var detail = await client.GetAsync($"/api/tasks/{Slug}{query}").WaitAsync(AsyncTestDeadline))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var gap = body.RootElement.GetProperty("info").GetProperty("acceptanceAttemptGap");
            Assert.Equal(2, gap.GetProperty("authorityEpoch").GetInt64());
            Assert.Equal(deliverySha, gap.GetProperty("resultSha").GetString());
            Assert.StartsWith(AcceptanceAttemptLineagePolicy.MissingRunAttemptRefusal,
                gap.GetProperty("reason").GetString());
        }

        var projectId = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByStorageLocation(_watchPath)!.Id;
        using (var overview = await ReadOverviewAsync(client, projectId))
        {
            var gap = overview.RootElement.GetProperty("data").GetProperty("acceptanceAttemptGap");
            Assert.Equal(2, gap.GetProperty("authorityEpoch").GetInt64());
            Assert.Equal(deliverySha, gap.GetProperty("resultSha").GetString());
            Assert.False(string.IsNullOrWhiteSpace(gap.GetProperty("subjectRunAttemptId").GetString()));
            Assert.StartsWith(AcceptanceAttemptLineagePolicy.MissingRunAttemptRefusal,
                gap.GetProperty("reason").GetString());
        }

        using (var refused = await client.PostAsJsonAsync(route,
                   new { targetState = TaskStates.Completed }).WaitAsync(AsyncTestDeadline))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("run-attempt-missing", body.RootElement.GetProperty("code").GetString());
            Assert.Contains("no run attempt in the current authority epoch",
                body.RootElement.GetProperty("error").GetString());
        }

        using var accepted = await client.PostAsJsonAsync(route, new
        {
            targetState = TaskStates.Completed,
            operatorOverride = true,
            reason = "Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.",
        }).WaitAsync(AsyncTestDeadline);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var completed = factory.Services.GetRequiredService<TaskScannerService>().FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Contains(factory.Services.GetRequiredService<TimelineLog>().ReadAll(completed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.AcceptedWithoutRunAttempt);
        // An accepted card is no longer in Human Review; the gap is gone.
        using (var overview = await ReadOverviewAsync(client, projectId))
            Assert.Equal(JsonValueKind.Null,
                overview.RootElement.GetProperty("data").GetProperty("acceptanceAttemptGap").ValueKind);
    }

    /// <summary>The core-first overview read; the task index hydrates lazily and answers 202 until then.</summary>
    private static async Task<JsonDocument> ReadOverviewAsync(HttpClient client, string projectId)
    {
        var url = $"/api/tasks/{Slug}/details/overview?project={Uri.EscapeDataString(projectId)}";
        var waited = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(url).WaitAsync(AsyncTestDeadline);
            if (response.StatusCode == HttpStatusCode.OK)
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(response.StatusCode == HttpStatusCode.Accepted && waited.Elapsed < AsyncTestDeadline,
                $"overview answered {(int)response.StatusCode}");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// AGT-3098 - AGT-2886 and seven siblings: the override acceptance of a
    /// card with a lost run attempt and an unknown delivery brief version came
    /// back two seconds later as <c>delivery-verdict-stale</c>. The delivery
    /// chain reconciler read the card after the lane write and before the
    /// claim write, saw a Completed card without a claim, and reopened it.
    /// Here the reconciler runs inside that window, on the first card update
    /// the acceptance publishes after its lane landed.
    /// </summary>
    [Fact]
    public async Task OverrideAccept_LostRunAttemptAndUnknownBriefVersion_ReconcilerDuringAcceptanceKeepsItCompleted()
    {
        var deliverySha = PublishDelivery("lost-attempt-reconcile.txt", "integrated before the crash\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var deps = Build(deliverySha, authorityRebuilt: true, guarded: true);
        var reconciler = new DeliveryChainReconciler(deps.Scanner, deps.Integration, deps.Transitions,
            deps.Configuration, NullLogger<DeliveryChainReconciler>.Instance);
        var movedDuringAcceptance = new List<int>();
        deps.Notifier.TaskUpdated += _ =>
        {
            if (movedDuringAcceptance.Count > 0
                || deps.Scanner.FindJob(Slug, _watchPath)?.State != TaskStates.Completed) return;
            movedDuringAcceptance.Add(reconciler.RunOnceAsync().GetAwaiter().GetResult());
        };
        const string reason = "Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.";

        var accepted = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            cause: TimelineActors.Human("operator"),
            reason: reason,
            operatorOverride: true);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        // The reconciler did run inside the acceptance, and moved nothing.
        Assert.Equal([0], movedDuringAcceptance);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Equal(CompletionClaimBases.OperatorOverride, completed.CompletionClaim?.Basis);
        Assert.Equal(deliverySha, completed.CompletionClaim?.ResultSha);
        var timeline = deps.Timeline.ReadAll(completed.FolderPath);
        Assert.Contains(timeline, entry => entry.Kind == TimelineEventKinds.AcceptedWithoutRunAttempt);
        var contentOverride = Assert.Single(timeline,
            entry => entry.Kind == TimelineEventKinds.CompletionContentOverridden);
        Assert.Equal("unknown", contentOverride.Details!["deliveryBriefVersion"]);

        // A later pass leaves the acceptance alone as well.
        Assert.Equal(0, await reconciler.RunOnceAsync());
        var settled = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, settled.State);
        Assert.DoesNotContain(deps.Timeline.ReadAll(settled.FolderPath), entry =>
            entry.Kind == TimelineEventKinds.LaneChanged
            && entry.Details?.GetValueOrDefault(LaneChangeCauses.DetailKey)
                == LaneChangeCauses.AcceptanceIntegrationFailed);
    }

    /// <summary>
    /// AGT-3098 - AGT-2882: accepted with an override against its delivery
    /// evidence (run attempt lost, delivery integrated), but the merge step
    /// that marks the override never reached a pipeline record. The backstop
    /// replayed the integration, the runner refused the earlier-epoch run
    /// attempt with Error, and the card went back to Human Review six minutes
    /// after it was accepted. The recorded acceptance is the outcome.
    /// </summary>
    [Fact]
    public async Task Backstop_AcceptedWithoutRunAttempt_RecordsTheOutcomeInsteadOfFailingOnTheEarlierEpochAttempt()
    {
        var deliverySha = PublishDelivery("lost-attempt-backstop.txt", "integrated before the crash\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(deliverySha, gateRunner: gate, authorityRebuilt: true, guarded: true);
        var subject = ReviewSubjectStore.Read(Path.Combine(_watchPath, TaskStates.HumanReview, Slug))!;
        File.Delete(Path.Combine(_watchPath, TaskStates.HumanReview, Slug, PipelineExecutionLog.FileName));

        var accepted = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            cause: TimelineActors.Human("operator"),
            reason: "Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.",
            operatorOverride: true);

        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Null(deps.Integration.ReadLatestMergeStep(completed));
        var status = deps.Integration.BuildLookup([completed])[completed.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(AcceptedIntegrationRecoveryAction.Finalize,
            deps.Integration.ResolveAcceptedIntegrationRecovery(
                completed, status, () => deps.Timeline.ReadAll(completed.FolderPath)).Action);
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            VerifyingRunner(deps, gate),
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions,
            deps.Timeline,
            deps.Pipeline);

        backstop.RunOnce();

        Assert.Equal(0, gate.Invocations);
        var kept = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, kept.State);
        var timeline = deps.Timeline.ReadAll(kept.FolderPath);
        Assert.DoesNotContain(timeline, entry => entry.Kind == TimelineEventKinds.IntegrationFailed);
        Assert.Contains(timeline, entry => entry.Kind == TimelineEventKinds.AcceptedWithoutRunAttempt
            && entry.Details?.GetValueOrDefault("subjectRunAttemptId") == subject.RunAttemptId);
        Assert.Contains(kept.IntegrationRecords, entry => entry.Classification == IntegrationRecordClasses.IntegratedVerified
            && entry.IntegrationSha == deliverySha);

        // The outcome is recorded once; the next sweep leaves the card alone.
        backstop.RunOnce();
        var again = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.Completed, again.State);
        Assert.DoesNotContain(deps.Timeline.ReadAll(again.FolderPath), entry =>
            entry.Kind == TimelineEventKinds.IntegrationFailed);
        Assert.Equal(0, gate.Invocations);
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

        using var factory = new TestHostFactory()
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
                        ["DeliveryChain:Guarded"] = "false",
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

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    public async Task FailureContinueHttp_ExtendsSameCardWithPinsAndTimelineEvidence(string parkedLane)
    {
        var deliverySha = PublishDelivery("continuation.txt", "pending delivery\n");
        var deps = Build(deliverySha, initialState: parkedLane);
        deps.Mutations.SetJobModel(Slug, "pinned-model", _watchPath);
        deps.Mutations.SetJobThinkingLevel(Slug, "high", _watchPath);
        var folder = Path.Combine(_watchPath, parkedLane, Slug);
        var subject = ReviewSubjectStore.Read(folder);
        Assert.NotNull(subject);
        var clock = new FakeTimeProvider(subject.CompletedAtUtc.AddMinutes(1));
        var recordedAt = clock.GetUtcNow().UtcDateTime;
        ParkedBlockerMarker.Write(folder, ParkedBlockerCatalog.Build(
            parkedLane, "Parked for an operator decision.", recordedAt)!);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Original task direction.\n");
        File.WriteAllText(Path.Combine(folder, PipelineExecutionLog.FileName),
            JsonSerializer.Serialize(new PipelineExecutionRecord
            {
                PipelineId = PipelineCatalogue.Standard.Id,
                JobId = Slug,
                StartedAt = recordedAt,
                Steps = [new PipelineStepExecution
                {
                    StepId = "review-aspects",
                    Status = PipelineStepStatus.Failed,
                    Verdict = "block",
                    VerdictSummary = "Correct the continuation guard.",
                    CompletedAt = clock.GetUtcNow().AddSeconds(1).UtcDateTime,
                }],
            }));

        using var factory = new TestHostFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _tempDir,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                        ["Runner:Role"] = "test-subject",
                        ["ClaudeCli:Path"] = Path.Combine(_tempDir, "cli-not-installed"),
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TaskRunnerService>());
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/{Slug}/failure/continue?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { }).WaitAsync(AsyncTestDeadline);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted,
            $"Expected 202 but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("queued", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("lane-not-runnable", payload.RootElement.GetProperty("savedReason").GetString());
        var queued = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(Slug, _watchPath);
        Assert.NotNull(queued);
        Assert.Equal(TaskKey, queued!.Key);
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Null(ParkedBlockerMarker.TryRead(queued.FolderPath));
        Assert.Equal("pinned-model", queued.Model);
        Assert.Equal("high", queued.ThinkingLevel);
        Assert.Equal("codex", queued.CliType);
        Assert.Contains("review-aspects", File.ReadAllText(Path.Combine(queued.FolderPath, "prompt-1.md")));
        Assert.Contains(factory.Services.GetRequiredService<TimelineLog>().ReadAll(queued.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                && entry.Details?.GetValueOrDefault("failureStage") == "review-aspects");
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

    /// <summary>
    /// AGT-2793 round 4: proves <c>FinalizeAcceptedTaskAsync</c> actually calls
    /// <see cref="BranchReclaimTriggerService.ReclaimAfterIntegration"/> after a
    /// landed merge. The worker is given a <see cref="GitService"/> pointed at a
    /// path that is never created on disk, so
    /// <see cref="GitBranchRetentionService.ReclaimForTask"/> fails
    /// deterministically on its very first check (no git process, no dependency
    /// on merge/retention-policy state) and logs a warning - an observable,
    /// unambiguous signal that the call site fired. The sibling test below
    /// proves the same setup stays silent on a failed merge.
    /// </summary>
    [Fact]
    public async Task SuccessfulIntegration_TriggersBranchReclaimAfterIntegration()
    {
        var deliverySha = PublishDelivery("wiring-success.txt", "remote work\n");
        var deps = Build(deliverySha, backgroundIntegration: true);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var (brokenGit, branchReclaim, recorder) = BuildBrokenBranchReclaim(deps);

        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            deps.Merge,
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline,
            brokenGit,
            branchReclaim);

        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project,
            Slug,
            reviewed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge));

        Assert.True(result.Outcome.IsSuccessfulIntegration(), result.Outcome.ToString());
        Assert.Contains(recorder.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Branch reclaim after integration had errors")
            && e.Message.Contains(Project));
    }

    [Fact]
    public async Task FailedIntegration_NeverTriggersBranchReclaim()
    {
        var deliverySha = PublishDelivery("wiring-failure.txt", "fenced work\n");
        var deps = Build(deliverySha, backgroundIntegration: true, writeReviewSubject: false);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        File.WriteAllText(
            Path.Combine(reviewed.FolderPath, "status.md"),
            "# Status\n\n- Result: Success\n");
        var legacyAccept = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, legacyAccept.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var (brokenGit, branchReclaim, recorder) = BuildBrokenBranchReclaim(deps);

        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            deps.Merge,
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline,
            brokenGit,
            branchReclaim);

        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project,
            Slug,
            completed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge));

        Assert.Equal(MergeIntoIntegrationOutcome.NoTaskBranch, result.Outcome);
        Assert.False(result.Outcome.IsSuccessfulIntegration());
        Assert.Empty(recorder.Entries);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task DivergedLane_CompletedCardFallsBackOnlyWhenDeliveryIsMissing(bool publishDelivery, bool patchEqual)
    {
        var deliverySha = PublishDelivery("guarded-delivery.txt", "reviewed work\n");
        var deps = Build(deliverySha, backgroundIntegration: true);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        File.WriteAllText(Path.Combine(reviewed.FolderPath, "status.md"), "# Status\n\n- Result: Success\n");
        var moved = deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        RunGit(_repo, "checkout", "-q", "-b", "unpublished", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "unpublished.txt"), "still pending\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "pending delivery");
        RunGit(_repo, "checkout", "-q", "-b", "lane-build", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "-m", "pending merge", "unpublished");
        var laneSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), laneSha);
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "lane-build");

        RunGit(_repo, "checkout", "-q", "-b", "operator-candidate", "origin/develop");
        if (patchEqual)
            RunGit(_repo, "cherry-pick", deliverySha);
        else if (publishDelivery)
            RunGit(_repo, "merge", "-q", "--no-ff", "-m", "published delivery", deliverySha);
        File.WriteAllText(Path.Combine(_repo, "operator.txt"), "operator change\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "operator candidate");
        RunGit(_repo, "push", "-q", "origin", "operator-candidate:develop");
        RunGit(_repo, "checkout", "-q", "main");

        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        Assert.Equal(_repo, git.ResolveRepoRootForWatchPath(_watchPath));
        Assert.Equal(publishDelivery ? PublishedDeliveryContainment.Contained : PublishedDeliveryContainment.Missing,
            git.CheckPublishedDelivery(_repo, "develop", deliverySha));
        var workerLog = new RecordingLogger<AcceptedIntegrationWorker>();
        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!, deps.Merge, deps.Scanner, deps.Mutations, deps.Provenance,
            workerLog, deps.Transitions, deps.Timeline, git);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(_watchPath, completed.WatchPath);
        Assert.True(AcceptanceIntegrationPolicy.IsIntegrationRequired(completed));
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project, Slug, completed.FolderPath, _watchPath, "develop", IntegrationStrategies.DirectMerge));

        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.Contains("unpublished", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(publishDelivery ? PublishedDeliveryContainment.Contained : PublishedDeliveryContainment.Missing,
            git.CheckPublishedDelivery(_repo, "develop", deliverySha));
        Assert.True(deps.Scanner.FindJob(Slug, _watchPath)!.State
                    == (publishDelivery ? TaskStates.Completed : TaskStates.HumanReview),
            string.Join("\n", workerLog.Snapshot().Select(entry => entry.Message)));
    }

    [Fact]
    public async Task DivergedLane_MergeResolutionMissingFromOrigin_KeepsLaneAndReturnsCompletedCardToReview()
    {
        var ordinarySha = PublishDelivery("ordinary.txt", "published patch\n");
        var baseSha = Git(_repo, "rev-parse", "origin/develop").Out.Trim();
        RunGit(_repo, "checkout", "-q", "-b", "delivery-merge", ordinarySha);
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "delivery side\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "delivery side change");
        var deliverySideSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();

        RunGit(_repo, "checkout", "-q", "-b", "other-side", baseSha);
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "other side\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "other side change");
        var otherSideSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();

        RunGit(_repo, "checkout", "-q", "delivery-merge");
        Assert.NotEqual(0, Git(_repo, "merge", "--no-ff", "other-side").Code);
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "delivery resolution\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "delivery conflict resolution");
        var deliverySha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "push", "-q", "--force", "origin", $"{deliverySha}:refs/heads/{DeliveryRef}");

        var deps = Build(deliverySha, backgroundIntegration: true);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        File.WriteAllText(Path.Combine(reviewed.FolderPath, "status.md"), "# Status\n\n- Result: Success\n");
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath).Status);

        RunGit(_repo, "checkout", "-q", "-b", "lane-build", baseSha);
        RunGit(_repo, "merge", "-q", "--no-ff", "-m", "lane delivery", deliverySha);
        var laneSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), laneSha);

        RunGit(_repo, "checkout", "-q", "-b", "published-main", baseSha);
        RunGit(_repo, "cherry-pick", ordinarySha);
        RunGit(_repo, "cherry-pick", deliverySideSha);
        RunGit(_repo, "checkout", "-q", "-b", "published-side", baseSha);
        RunGit(_repo, "cherry-pick", otherSideSha);
        RunGit(_repo, "checkout", "-q", "published-main");
        Assert.NotEqual(0, Git(_repo, "merge", "--no-ff", "published-side").Code);
        File.WriteAllText(Path.Combine(_repo, "shared.txt"), "published resolution\n");
        RunGit(_repo, "add", "shared.txt");
        RunGit(_repo, "commit", "-q", "-m", "published conflict resolution");
        RunGit(_repo, "push", "-q", "origin", "HEAD:refs/heads/develop");
        RunGit(_repo, "checkout", "-q", "main");

        var integrationWorktree = Path.Combine(_tempDir, "integration-worktree");
        RunGit(_repo, "worktree", "add", "-q", "--detach", integrationWorktree, "main");
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        Assert.Equal(PublishedDeliveryContainment.Missing,
            git.CheckPublishedDelivery(_repo, "develop", deliverySha));
        var sync = git.SynchronizeIntegrationBranch(integrationWorktree, "develop");
        Assert.Equal(IntegrationBranchSyncOutcome.Diverged, sync.Outcome);
        Assert.Contains(deliverySha, sync.Error);
        Assert.Equal(laneSha, Git(_repo, "rev-parse", GitService.IntegrationLaneRef("develop")).Out.Trim());

        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!, deps.Merge, deps.Scanner, deps.Mutations, deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance, deps.Transitions, deps.Timeline, git);
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project, Slug, completed.FolderPath, _watchPath, "develop", IntegrationStrategies.DirectMerge));

        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    /// <summary>
    /// AGT-2793 requirement 2(a): the live pre-Human-Review merge, not the
    /// legacy <see cref="AcceptedIntegrationWorker"/>, is the path every Remote
    /// delivery takes. Same broken-repository signal as above, through the
    /// coordinator's DI constructor. The reclaim runs on the drain after the
    /// result is handed back, so the test waits for the warning.
    /// </summary>
    [Fact]
    public async Task RemoteDeliveryIntegration_LandedMerge_TriggersBranchReclaimAfterIntegration()
    {
        var deliverySha = PublishDelivery("remote-reclaim.txt", "remote work\n");
        var deps = Build(deliverySha, initialState: TaskStates.AutoReview);
        var autoReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        var (brokenGit, branchReclaim, recorder) = BuildBrokenBranchReclaim(deps);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            deps.Merge,
            deps.Scanner,
            deps.Provenance,
            deps.Pipeline,
            deps.Timeline,
            AgentRounds(deps),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            deps.Authority,
            brokenGit,
            branchReclaim);

        var result = await coordinator.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            Project,
            autoReview.Id,
            autoReview.FolderPath,
            autoReview.WatchPath,
            "develop",
            IntegrationStrategies.DirectMerge,
            PipelineTypes.Task,
            ReviewSubjectStore.Read(autoReview.FolderPath)!.CompletedAtUtc)).WaitAsync(AsyncTestDeadline);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        await recorder.Logged.WaitAsync(AsyncTestDeadline);
        Assert.Contains(recorder.Snapshot(), e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Branch reclaim after integration had errors")
            && e.Message.Contains(Project));
    }

    private (GitService BrokenGit, BranchReclaimTriggerService BranchReclaim, RecordingLogger<BranchReclaimTriggerService> Recorder)
        BuildBrokenBranchReclaim(Deps deps)
    {
        var brokenConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = Path.Combine(_tempDir, "not-a-repo"),
            ["WatchPaths:0:RepositoryPath"] = Path.Combine(_tempDir, "not-a-repo"),
        }).Build();
        // GitService.ResolveRepoRootForWatchPath reads the watch-path list off
        // its TaskScannerService's own config, not off the IConfiguration passed
        // here - deps.Scanner still points at the real fixture repo, so this
        // needs its own scanner bound to brokenConfig for the broken RepositoryPath
        // to actually be the one resolved.
        var brokenSummary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, brokenConfig);
        var brokenScanner = new TaskScannerService(brokenConfig, NullLogger<TaskScannerService>.Instance, brokenSummary);
        var brokenGit = new GitService(NullLogger<GitService>.Instance, brokenScanner, brokenConfig);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            brokenConfig, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var retention = new GitBranchRetentionService(
            brokenGit, registry, brokenConfig, NullLogger<GitBranchRetentionService>.Instance);
        var recorder = new RecordingLogger<BranchReclaimTriggerService>();
        var branchReclaim = new BranchReclaimTriggerService(retention, brokenConfig, recorder);
        return (brokenGit, branchReclaim, recorder);
    }

    [Fact]
    public async Task OperatorOverride_CompletesWithoutStartingIntegration_AndRecordsAuditReason()
    {
        var deliverySha = PublishDelivery("override.txt", "operator accepted\n");
        var deps = Build(deliverySha, backgroundIntegration: true, seedContent: false);

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
        Assert.Equal(CompletionClaimBases.OperatorOverride, completed.CompletionClaim?.Basis);
        Assert.Equal("Concept outcome has no branch to merge.", completed.CompletionClaim?.Reason);
        Assert.NotNull(completed.CompletionClaim?.CurrentBriefVersion);
        Assert.Contains(deps.Timeline.ReadAll(completed.FolderPath), entry =>
            entry.Kind == TimelineEventKinds.CompletionContentOverridden
            && entry.Details?.GetValueOrDefault("reason") == "Concept outcome has no branch to merge."
            && entry.Details?.ContainsKey("currentBriefVersion") == true
            && entry.Details?.ContainsKey("deliveryBriefVersion") == true);
    }

    /// <summary>
    /// AGT-2817 - the completion contract runs at the lane change, including
    /// the ones the acceptance rail never sees. AGT-2795 was dragged into the
    /// delivered lane while its attributed delivery had failed review and was
    /// never integrated; the rail's own guard
    /// (<c>ValidateIntegratedAcceptance</c>) only covers
    /// <c>5-human-review -> 6-completed</c>, so a drag from any other lane used
    /// to need no proof at all. Now the contract refuses it: the card stays
    /// where it was and records no claim.
    /// </summary>
    [Fact]
    public async Task OperatorMoveToCompleted_FromAnotherLane_IsRefusedByTheContract()
    {
        var deliverySha = PublishDelivery("never-integrated.txt", "abandoned\n");
        var deps = Build(deliverySha, backgroundIntegration: true, initialState: TaskStates.AutoReview);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, "origin/develop").Code);

        var refused = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            cause: TimelineActors.Human("operator"));

        Assert.Equal(MoveJobStatus.IntegrationFailed, refused.Status);
        Assert.Contains("not contained in develop", refused.Message ?? "", StringComparison.Ordinal);
        Assert.Contains("written reason", refused.Message ?? "", StringComparison.Ordinal);
        var stillInReview = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(stillInReview);
        Assert.Equal(TaskStates.AutoReview, stillInReview!.State);
        Assert.Null(stillInReview.CompletionClaim);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConceptWithoutContentVerdict_CannotComplete()
    {
        var deliverySha = PublishDelivery("concept-review.txt", "dossier\n");
        var deps = Build(deliverySha, backgroundIntegration: true, mode: TaskModes.Concept, seedContent: false);
        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath,
            cause: TimelineActors.Human("operator"));
        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.Contains("no content verdict", outcome.Message);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task DeliveryForOlderBrief_CannotComplete()
    {
        var deliverySha = PublishDelivery("older-brief.txt", "old result\n");
        var deps = Build(deliverySha, backgroundIntegration: true, briefVersion: "brief-v1", seedContent: false);
        BriefVersionStore.Record(deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath, "brief v2");
        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath,
            cause: TimelineActors.Human("operator"));
        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.Contains("delivered against brief", outcome.Message);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    /// <summary>
    /// Automated moves use the same completion contract and cannot land
    /// without a claim, even when the legacy delivery guard is disabled.
    /// </summary>
    [Fact]
    public async Task AutomatedMoveToCompleted_WithUnintegratedDelivery_IsRefusedWithoutClaim()
    {
        var deliverySha = PublishDelivery("automated.txt", "runner path\n");
        var deps = Build(deliverySha, backgroundIntegration: true, initialState: TaskStates.AutoReview);

        var outcome = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            cause: TimelineActors.Orchestrator);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        var stillInReview = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, stillInReview.State);
        Assert.Null(stillInReview.CompletionClaim);
    }

    /// <summary>
    /// AGT-2817 - an override is a claim the card carries and shows, so the
    /// boundary refuses it without a written reason before the transition runs.
    /// The identical request with a reason is accepted, which is what makes the
    /// missing reason - and not the lane or the delivery - the cause of the 400.
    /// </summary>
    [Fact]
    public async Task OperatorOverrideWithoutWrittenReason_IsRefusedAtTheHttpBoundary()
    {
        var deliverySha = PublishDelivery("override-boundary.txt", "abandoned\n");
        var deps = Build(deliverySha, backgroundIntegration: true, briefVersion: "brief-v1", seedContent: false);
        BriefVersionStore.Record(deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath, "brief v2");

        using var factory = new TestHostFactory()
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
                        ["DeliveryChain:Guarded"] = "false",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<ProjectSettingsService>();
                    services.AddSingleton(deps.Settings);
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(deps.Authority);
                });
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var route = $"/api/tasks/{Slug}/move?watchPath={Uri.EscapeDataString(_watchPath)}";

        // Both HTTP move verbs must reach the shared content gate.
        foreach (var (verb, url) in new[]
        {
            (HttpMethod.Post, route),
            (HttpMethod.Put, $"/api/tasks/{Slug}/state?watchPath={Uri.EscapeDataString(_watchPath)}"),
        })
        {
            using var request = new HttpRequestMessage(verb, url)
            {
                Content = JsonContent.Create(new { targetState = TaskStates.Completed }),
            };
            using var refusedContent = await client.SendAsync(request).WaitAsync(AsyncTestDeadline);
            Assert.Equal(HttpStatusCode.Conflict, refusedContent.StatusCode);
            var body = await refusedContent.Content.ReadAsStringAsync();
            Assert.Contains("no content verdict", body);
            Assert.Contains("delivered against brief", body);
        }

        foreach (var unusable in new object[]
        {
            new { targetState = TaskStates.Completed, operatorOverride = true },
            new { targetState = TaskStates.Completed, operatorOverride = true, reason = "   " },
            new { targetState = TaskStates.Completed, operatorOverride = true, reason = "no" },
        })
        {
            using var refused = await client.PostAsJsonAsync(route, unusable).WaitAsync(AsyncTestDeadline);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Contains(
                "needs a written reason",
                body.RootElement.GetProperty("error").GetString() ?? "",
                StringComparison.Ordinal);
        }

        var untouched = factory.Services.GetRequiredService<TaskScannerService>().FindJob(Slug, _watchPath);
        Assert.NotNull(untouched);
        Assert.Equal(TaskStates.HumanReview, untouched!.State);
        Assert.Null(untouched.CompletionClaim);

        using var accepted = await client.PostAsJsonAsync(
            route,
            new
            {
                targetState = TaskStates.Completed,
                operatorOverride = true,
                reason = "Delivery failed review and was abandoned; closing the card.",
            }).WaitAsync(AsyncTestDeadline);

        Assert.NotEqual(HttpStatusCode.BadRequest, accepted.StatusCode);
        var claim = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(Slug, _watchPath)?.CompletionClaim;
        Assert.NotNull(claim);
        Assert.Equal(CompletionClaimBases.OperatorOverride, claim!.Basis);
        Assert.Equal("Delivery failed review and was abandoned; closing the card.", claim.Reason);

        using var report = await client.GetAsync(
            "/api/projects/Fixture/completed-lane/content-review-gaps").WaitAsync(AsyncTestDeadline);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using var inventory = JsonDocument.Parse(await report.Content.ReadAsStringAsync());
        Assert.Equal(1, inventory.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(TaskKey, inventory.RootElement.GetProperty("rows")[0]
            .GetProperty("taskKey").GetString());
        Assert.Equal(TaskStates.Completed,
            factory.Services.GetRequiredService<TaskScannerService>()
                .FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task AcceptRemoteDelivery_UsesConfiguredPullRequestStrategy_AndReturnsToReview()
    {
        var deliverySha = PublishDelivery("pull-request.txt", "review handoff\n");
        var deps = Build(deliverySha, IntegrationStrategies.PullRequest);

        var outcome = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, outcome.Status);
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);

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
        // AGT-2996: the integration lane follows origin, so the conflicting
        // develop commit must be published for the conflict to be real.
        RunGit(_repo, "push", "-q", "origin", "develop");
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
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            deps.Authority);
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
        Assert.NotNull(integration.Failure?.ConflictReport);
        Assert.Equal(["shared.txt"], integration.Failure!.ConflictReport!.ConflictedFiles);
        Assert.StartsWith(
            "Merge into develop conflicted (direct merge, mechanical merge and rebase fallback all failed)",
            integration.Detail,
            StringComparison.Ordinal);
        Assert.Equal(3, integration.Detail!.Split('\n').Length);
        Assert.Contains("shared.txt", integration.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hint:", integration.Detail, StringComparison.OrdinalIgnoreCase);
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
    public async Task AcceptOutOfBandIntegratedCard_RequiresVerificationBeforeCompletion()
    {
        var deliverySha = PublishDelivery("out-of-band.txt", "already integrated\n");
        RunGit(_repo, "checkout", "-q", "-b", "develop", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        RunGit(_repo, "checkout", "-q", "main");
        var deps = Build(deliverySha, backgroundIntegration: true);

        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var statusBeforeAccept = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        // AGT-2849: the out-of-band merge is local only, so it reads
        // merged-locally; the out-of-band merge still needs exact-tree gate
        // evidence before acceptance can complete the card.
        Assert.Equal(IntegrationStatuses.MergedLocally, statusBeforeAccept.Status);
        Assert.Equal(IntegrationVerificationStates.Unverified, statusBeforeAccept.Verification?.State);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        var stillReviewed = deps.Scanner.FindJob(Slug, _watchPath);
        Assert.NotNull(stillReviewed);
        Assert.Equal(TaskStates.HumanReview, stillReviewed!.State);
        Assert.False(deps.AcceptedQueue!.Reader.TryRead(out _));
        var mergeStep = deps.Pipeline.Read(stillReviewed.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.NotNull(mergeStep);
        Assert.Equal(PipelineStepStatus.Pending, mergeStep!.Status);
        Assert.DoesNotContain(
            deps.Timeline.ReadAll(stillReviewed.FolderPath),
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
    public async Task AcceptAlreadyIntegratedMainOnlyCard_RequiresVerificationBeforeCompletion()
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
        // AGT-2849: merged into the local main, not pushed.
        Assert.Equal(IntegrationStatuses.MergedLocally, before.Status);
        Assert.Equal("main", before.IntegrationBranch);
        Assert.Equal(IntegrationVerificationStates.Unverified, before.Verification?.State);

        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);

        Assert.Equal(MoveJobStatus.IntegrationFailed, accepted.Status);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)?.State);
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
    public async Task DivergedLane_SubjectlessLocalDeliveryPublishedToOrigin_StaysCompleted()
    {
        var deliverySha = PublishLocalDelivery("local-published.txt", "local delivery\n");
        var deps = Build(deliverySha, backgroundIntegration: true, writeReviewSubject: false);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Null(ReviewSubjectStore.Read(reviewed.FolderPath));
        var delivery = DeliveryRefResolver.Resolve(Slug, reviewed.FolderPath);
        Assert.Equal(DeliveryRefSource.LocalTaskFallback, delivery.Source);
        Assert.Null(delivery.ExpectedResultSha);
        File.WriteAllText(Path.Combine(reviewed.FolderPath, "status.md"), "# Status\n\n- Result: Success\n");
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath).Status);

        RunGit(_repo, "checkout", "-q", "-b", "unpublished", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "unpublished.txt"), "another delivery\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "pending delivery");
        RunGit(_repo, "checkout", "-q", "-b", "lane-build", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "-m", "pending merge", "unpublished");
        var laneSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), laneSha);
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "lane-build");

        RunGit(_repo, "checkout", "-q", "-b", "operator-candidate", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "-m", "published local delivery", deliverySha);
        RunGit(_repo, "push", "-q", "origin", "operator-candidate:develop");
        RunGit(_repo, "checkout", "-q", "main");

        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        Assert.Equal(PublishedDeliveryContainment.Contained,
            git.CheckPublishedDelivery(_repo, "develop", deliverySha));
        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!, deps.Merge, deps.Scanner, deps.Mutations, deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance, deps.Transitions, deps.Timeline, git);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project, Slug, completed.FolderPath, _watchPath, "develop", IntegrationStrategies.DirectMerge));

        Assert.Equal(AcceptedIntegrationFailureCodes.BranchSyncFailed, result.FailureCode);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)!.State);
        Assert.Null(ReviewSubjectStore.Read(completed.FolderPath));
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

        using var factory = new TestHostFactory()
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

        using var factory = new TestHostFactory()
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
        Assert.Contains("merging 'origin/develop'", intent.Prompt, StringComparison.OrdinalIgnoreCase);
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

        using var factory = new TestHostFactory()
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
        Assert.Contains("merging 'origin/develop'", intent?.Prompt, StringComparison.OrdinalIgnoreCase);
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
        var mergedCard = deps.Scanner.FindJob(Slug, _watchPath)!;
        var mergedStatus = deps.Integration.BuildLookup([mergedCard])[mergedCard.TaskKey];
        Assert.Equal(IntegrationStatuses.MergedLocally, mergedStatus.Status);
        Assert.Equal(IntegrationVerificationStates.Verified, mergedStatus.Verification?.State);
        Assert.Equal(immediate.MergedSha, mergedStatus.Verification?.Sha);
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);

        // AGT-2996: the merge sits on the Studio integration lane until pushed.
        var localDevelop = Git(_repo, "rev-parse", GitService.IntegrationLaneRef("develop")).Out.Trim();
        var remoteBefore = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.NotEqual(localDevelop, remoteBefore);

        // Archiving must not erase the durable recovery fact. Production's
        // ScanAllJobs snapshot deliberately omits 7-archive, so the backstop
        // must use the archive-inclusive scanner.
        var archived = deps.States.MoveJob(Slug, TaskStates.Archive, _watchPath);
        Assert.Equal(MoveJobStatus.Success, archived.Status);

        var archivedFolder = deps.Scanner.FindJob(Slug, _watchPath)!.FolderPath;
        var mergeStep = deps.Pipeline.Read(archivedFolder)!.Steps.Single(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(localDevelop, mergeStep.ApprovedIntegrationSha);

        // A legacy or torn record with no exact approval cannot authorize the
        // backstop to publish whatever happens to be at the lane tip.
        deps.Pipeline.RecordStep(archivedFolder, mergeStep with { ApprovedIntegrationSha = null });
        var recoveredPipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var backstop = new IntegrationPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            recoveredPipeline,
            deps.Merge,
            deps.Configuration,
            NullLogger<IntegrationPushBackstopHostedService>.Instance);
        Assert.Equal(0, await backstop.RunOnceAsync());
        Assert.Equal(remoteBefore, Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim());
        deps.Pipeline.RecordStep(archivedFolder, mergeStep);

        // A later, unverified lane commit must not ride along with the earlier
        // card's approval, even when the backstop sees no in-memory busy gate.
        RunGit(_repo, "checkout", "-q", "-b", "unverified-tip", localDevelop);
        File.WriteAllText(Path.Combine(_repo, "unverified.txt"), "not gated\n");
        RunGit(_repo, "add", "unverified.txt");
        RunGit(_repo, "commit", "-q", "-m", "test: unverified lane tip");
        var unverifiedSha = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), unverifiedSha);
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "unverified-tip");

        // Simulate a fresh backend process: the in-memory IntegrationPushQueue
        // is gone, but pipeline-execution.json still proves merge=Passed and
        // push=Pending. The startup backstop must re-drive the push.
        var recovered = await backstop.RunOnceAsync();

        Assert.Equal(1, recovered);
        var remoteAfter = Git(_origin, "-c", "safe.bareRepository=all", "rev-parse", "develop").Out.Trim();
        Assert.Equal(localDevelop, remoteAfter);
        Assert.NotEqual(unverifiedSha, remoteAfter);
        var pushStep = recoveredPipeline.Read(archivedFolder)!.Steps.Single(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
        Assert.Equal(PipelineStepStatus.Passed, pushStep.Status);
        Assert.Equal("pushed", pushStep.Verdict);
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);

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
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);

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
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
        Assert.Contains("- Integration: `merged-locally`", status);
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);

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
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
        Assert.NotEqual(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);

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
        Assert.Equal(0, Git(_repo, "merge-base", "--is-ancestor", deliverySha, GitService.IntegrationLaneRef("develop")).Code);
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
        // AGT-2996: integration merges land on the Studio-owned lane, never on
        // the checkout's develop, so the crash leaves the merge on the lane.
        RunGit(_repo, "checkout", "-q", "-b", "crash-merge", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        var localDevelop = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), localDevelop);
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "crash-merge");
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
        // AGT-2996: integration merges land on the Studio-owned lane, never on
        // the checkout's develop, so the crash leaves the merge on the lane.
        RunGit(_repo, "checkout", "-q", "-b", "crash-merge", "origin/develop");
        RunGit(_repo, "merge", "-q", "--no-ff", "--no-edit", deliverySha);
        var localDevelop = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "update-ref", GitService.IntegrationLaneRef("develop"), localDevelop);
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "crash-merge");
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

    /// <summary>
    /// AGT-3002 - replay of the 2026-09-28 incident. An operator script pushed
    /// develop carrying a delivery whose only gate had failed, and the lane
    /// completed the card as AlreadyMerged. Containment alone never completes
    /// it. AGT-3103: origin/develop already carries the delivery, so the
    /// backstop's re-drive is a no-op integration. It runs no gate, records
    /// integrated-unverified with the containment SHA, and returns the card to
    /// Human Review. Plain acceptance is refused; an operator override is not.
    /// The unpublished-lane gate and its cause card are covered by
    /// <see cref="ContainedDeliveryCauseCardTests"/>.
    /// </summary>
    [Fact]
    public async Task Backstop_CompletedAlreadyMergedWithoutGateEvidence_ReturnsToReviewUnverifiedWithoutAGate()
    {
        var deliverySha = PublishDelivery("ungated.txt", "pushed by an operator script\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "2 tests failed on the pushed tree");
        var deps = Build(deliverySha, gateRunner: gate);
        var completed = CompleteAsAlreadyMergedWithoutEvidence(deps);
        var before = deps.Integration.BuildLookup([completed])[completed.TaskKey];
        Assert.True(IntegrationStatuses.IsMerged(before.Status));
        Assert.Equal(IntegrationVerificationStates.Unverified, before.Verification?.State);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(before.Verification));
        Assert.Equal(deliverySha, before.Verification?.Sha);
        Assert.Equal(AcceptedIntegrationRecoveryAction.Retry,
            deps.Integration.ResolveAcceptedIntegrationRecovery(completed, before).Action);

        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            VerifyingRunner(deps, gate),
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions,
            deps.Timeline,
            deps.Pipeline,
            git: new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration));

        Assert.Equal(0, backstop.RunOnce());

        Assert.Equal(0, gate.Invocations);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, reviewed.State);
        var verification = IntegrationVerificationStore.Read(reviewed.FolderPath);
        Assert.NotNull(verification);
        Assert.Equal(IntegrationVerificationStates.Unverified, verification!.State);
        Assert.Equal(deliverySha, verification.Sha);
        Assert.Equal(deliverySha, verification.ContainmentSha);
        Assert.Equal(IntegrationVerificationEvidence.None, verification.Evidence);
        Assert.Null(verification.GateVerdict);
        Assert.False(verification.GateFailed);
        var step = deps.Pipeline.Read(reviewed.FolderPath)!.Steps.Last(
            item => item.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal("already-merged", step.Verdict);
        Assert.Contains($"Containment proven by {deliverySha[..7]}", step.Reason);
        Assert.Contains("integrated-unverified", step.Reason);
        Assert.Contains(
            deps.Timeline.ReadAll(reviewed.FolderPath),
            entry => entry.Kind == TimelineEventKinds.IntegrationVerificationRecorded
                     && entry.Details?.GetValueOrDefault("state") == IntegrationVerificationStates.Unverified
                     && entry.Details?.GetValueOrDefault("containmentSha") == deliverySha);

        var projected = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.True(IntegrationStatuses.IsMerged(projected.Status));
        Assert.Equal(IntegrationVerificationStates.Unverified, projected.Verification?.State);
        Assert.False(projected.Verification!.GateFailed);

        // Back in Human Review the card is no longer a recovery candidate.
        backstop.RunOnce();
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(TaskStates.HumanReview, deps.Scanner.FindJob(Slug, _watchPath)!.State);

        var refused = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath);
        Assert.Equal(MoveJobStatus.IntegrationFailed, refused.Status);
        Assert.Contains("integrated-unverified", refused.Message);
        var overridden = await deps.Transitions.MoveAsync(
            Slug,
            TaskStates.Completed,
            _watchPath,
            reason: "Operator repaired develop by hand and takes responsibility for the tree.",
            operatorOverride: true);
        Assert.Equal(MoveJobStatus.Success, overridden.Status);
    }

    /// <summary>
    /// AGT-3103 - the accepted-integration worker never completes a no-op
    /// integration that is still integrated-unverified. It returns the card to
    /// Human Review with the missing verification as the named outcome.
    /// </summary>
    [Fact]
    public async Task Worker_NoOpIntegrationWithoutGateEvidence_DoesNotCompleteTheCard()
    {
        var deliverySha = PublishDelivery("worker-unverified.txt", "pushed before the review settled\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, backgroundIntegration: true, gateRunner: gate);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        File.WriteAllText(Path.Combine(reviewed.FolderPath, "status.md"), "# Status\n\n- Result: Success\n");
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath).Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        var worker = new AcceptedIntegrationWorker(
            deps.AcceptedQueue!,
            VerifyingRunner(deps, gate),
            deps.Scanner,
            deps.Mutations,
            deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance,
            deps.Transitions,
            deps.Timeline,
            git);

        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project,
            Slug,
            completed.FolderPath,
            _watchPath,
            "develop",
            IntegrationStrategies.DirectMerge));

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, result.Outcome);
        Assert.True(result.VerificationPending);
        Assert.Equal(deliverySha, result.ContainmentSha);
        Assert.Equal(0, gate.Invocations);
        var returned = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, returned.State);
        Assert.Contains(
            $"- Outcome: `{MergeIntoDevelopRunner.IntegrationUnverifiedFailureCode}`",
            File.ReadAllText(Path.Combine(returned.FolderPath, "status.md")));
    }

    [Fact]
    public void Backstop_CompletedAlreadyMergedWithStaleVerifiedRecord_StaysUnverifiedWithoutAGate()
    {
        var deliverySha = PublishDelivery("ungated-green.txt", "pushed by an operator script\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(deliverySha, gateRunner: gate);
        CompleteAsAlreadyMergedWithoutEvidence(deps);
        var before = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Null(IntegrationVerificationStore.Read(before.FolderPath));
        IntegrationVerificationStore.Write(before.FolderPath, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = new string('a', 40),
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.GateRun,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            Reason = "A gate passed on an older integration tree.",
        });
        var stale = deps.Integration.BuildLookup([before])[before.TaskKey].Verification;
        Assert.Equal(IntegrationVerificationStates.Unverified, stale?.State);
        Assert.Equal(deliverySha, stale?.Sha);
        Assert.Contains("differs", stale?.Reason);
        Assert.False(IntegrationVerificationStates.PermitsCompletion(
            stale));
        Assert.Equal(AcceptedIntegrationRecoveryAction.Retry,
            deps.Integration.ResolveAcceptedIntegrationRecovery(before,
                deps.Integration.BuildLookup([before])[before.TaskKey]).Action);
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            VerifyingRunner(deps, gate),
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions,
            deps.Timeline,
            deps.Pipeline);

        backstop.RunOnce();
        backstop.RunOnce();

        // AGT-3103: a verdict for a tree the branch never carried proves
        // nothing, and the no-op re-drive produces no new evidence.
        Assert.Equal(0, gate.Invocations);
        var reviewed = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, reviewed.State);
        var verification = IntegrationVerificationStore.Read(reviewed.FolderPath);
        Assert.Equal(IntegrationVerificationStates.Unverified, verification?.State);
        Assert.Equal(IntegrationVerificationEvidence.None, verification?.Evidence);
        Assert.Equal(deliverySha, verification?.Sha);
        var projected = deps.Integration.BuildLookup([reviewed])[reviewed.TaskKey];
        Assert.Equal(IntegrationVerificationStates.Unverified, projected.Verification?.State);
    }

    [Fact]
    public async Task ContainedDelivery_WithIntegratedVerifiedRecordForTheExactTree_CompletesWithoutAGateRun()
    {
        var deliverySha = PublishDelivery("recorded.txt", "verified by an earlier gate\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, gateRunner: gate);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        TaskJsonFile.UpdateFieldOrThrow(
            card.FolderPath,
            "integrationRecords",
            new[]
            {
                new TaskIntegrationRecord
                {
                    Id = "gate-verified-" + deliverySha[..8],
                    Classification = IntegrationRecordClasses.IntegratedVerified,
                    RecordedAtUtc = VerificationFixtureAtUtc,
                    IntegrationBranch = "develop",
                    IntegrationSha = deliverySha,
                    Evidence = "The pre-develop gate passed on this exact tree.",
                },
            });
        var result = await VerifyingRunner(deps, gate).RunAsync(
            Project, Slug, card.FolderPath, _watchPath, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, result.Outcome);
        Assert.Equal(deliverySha, result.MergedSha);
        Assert.Equal(0, gate.Invocations);
        var verification = IntegrationVerificationStore.Read(card.FolderPath);
        Assert.Equal(IntegrationVerificationStates.Verified, verification?.State);
        Assert.Equal(IntegrationVerificationEvidence.IntegrationRecord, verification?.Evidence);
    }

    [Fact]
    public async Task OperatorAcceptance_SurvivesWorkerAndRestartBackstop()
    {
        var deliverySha = PublishDelivery("operator-durable.txt", "accepted candidate\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, backgroundIntegration: true, gateRunner: gate);
        const string reason = "Operator ran the full suite on the published candidate.";
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath,
            cause: TimelineActors.Human("operator"), reason: reason, operatorOverride: true);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var record = Assert.Single(completed.IntegrationRecords,
            item => item.Id.StartsWith("operator-acceptance-", StringComparison.Ordinal));
        Assert.Equal(IntegrationRecordClasses.IntegratedVerified, record.Classification);
        Assert.Equal(deliverySha, record.IntegrationSha);
        Assert.Equal(deliverySha, record.SourceSha);
        Assert.Equal(TimelineActors.Human("operator"), record.Actor);
        Assert.Equal(reason, record.Reason);

        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        var worker = new AcceptedIntegrationWorker(deps.AcceptedQueue!, VerifyingRunner(deps, gate),
            deps.Scanner, deps.Mutations, deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance, deps.Transitions, deps.Timeline, git);
        var result = await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project, Slug, completed.FolderPath, _watchPath, "develop", IntegrationStrategies.DirectMerge));
        Assert.False(result.VerificationPending);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)!.State);
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner, deps.Settings, VerifyingRunner(deps, gate), deps.Integration,
            deps.Mutations, deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions, deps.Timeline, deps.Pipeline);
        backstop.RunOnce();
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    [Fact]
    public async Task ExternalSuiteRecord_VerifiesEveryDeliveryInPublishedCandidateWithoutGate()
    {
        var deliverySha = PublishDelivery("external-suite.txt", "verified candidate\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, gateRunner: gate);
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _tempDir,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repo,
                ["WatchPaths:0:RepositoryPath"] = _repo,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ProjectSettingsService>();
                services.AddSingleton(deps.Settings);
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var projectId = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByStorageLocation(_watchPath)!.Id;
        using var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/external-verifications",
            new AttachExternalPublishedVerificationRequest(deliverySha, "verification-host",
                "Full build and test suite passed.", new string('a', 64))).WaitAsync(AsyncTestDeadline);
        response.EnsureSuccessStatusCode();
        Assert.Equal(deliverySha, Assert.Single(ExternalPublishedVerificationStore.Read(_watchPath)).CandidateSha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var result = await VerifyingRunner(deps, gate).RunAsync(
            Project, Slug, card.FolderPath, _watchPath, "develop", CancellationToken.None);
        Assert.False(result.VerificationPending);
        Assert.Equal(0, gate.Invocations);
        var status = deps.Integration.BuildLookup([card])[card.TaskKey];
        Assert.Equal(IntegrationVerificationStates.Verified, status.Verification?.State);
    }

    [Fact]
    public async Task OperatorAcceptance_WhenPublishedBranchLosesContainingTree_FallsBack()
    {
        var deliverySha = PublishDelivery("rewritten-candidate.txt", "lost candidate\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "rewritten branch");
        var deps = Build(deliverySha, backgroundIntegration: true, gateRunner: gate);
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath,
            cause: TimelineActors.Human("operator"), reason: "Operator verified the published tree.", operatorOverride: true);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var seed = Git(_repo, "rev-parse", "main").Out.Trim();
        RunGit(_repo, "push", "-q", "--force", "origin", $"{seed}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin");
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        var projected = deps.Integration.BuildLookup([completed])[completed.TaskKey];
        Assert.False(IntegrationVerificationStates.PermitsCompletion(projected.Verification));
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        var worker = new AcceptedIntegrationWorker(deps.AcceptedQueue!, VerifyingRunner(deps, gate),
            deps.Scanner, deps.Mutations, deps.Provenance,
            NullLogger<AcceptedIntegrationWorker>.Instance, deps.Transitions, deps.Timeline, git);
        await worker.ProcessAsync(new AcceptedIntegrationRequest(
            Project, Slug, completed.FolderPath, _watchPath, "develop", IntegrationStrategies.DirectMerge));
        var returned = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, returned.State);
        Assert.Contains("no longer in published develop",
            File.ReadAllText(Path.Combine(returned.FolderPath, "status.md")));
        Assert.Equal(0, gate.Invocations);
    }

    [Fact]
    public async Task Backstop_OperatorAcceptanceAfterPublishedRewrite_ReturnsToReviewWithoutReintegration()
    {
        var deliverySha = PublishDelivery("rewritten-backstop.txt", "lost candidate\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner(BuildTestGateVerdict.Fail, "must not run");
        var deps = Build(deliverySha, gateRunner: gate);
        var accepted = await deps.Transitions.MoveAsync(Slug, TaskStates.Completed, _watchPath,
            cause: TimelineActors.Human("operator"), reason: "Operator verified the published tree.", operatorOverride: true);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var seed = Git(_repo, "rev-parse", "main").Out.Trim();
        RunGit(_repo, "push", "-q", "--force", "origin", $"{seed}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin");
        var git = new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration);
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner, deps.Settings, VerifyingRunner(deps, gate), deps.Integration,
            deps.Mutations, deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions, deps.Timeline, deps.Pipeline, git: git);
        backstop.RunOnce();
        var returned = deps.Scanner.FindJob(Slug, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, returned.State);
        Assert.Contains("no longer in published develop",
            File.ReadAllText(Path.Combine(returned.FolderPath, "status.md")));
        Assert.Equal(0, gate.Invocations);
    }

    /// <summary>
    /// The state the old lane left behind: Completed with a Passed
    /// <c>already-merged</c> step that carries no gate verdict.
    /// </summary>
    /// <summary>
    /// AGT-3002 - once a gate passed on a tree carrying this delivery, later
    /// commits on develop belong to other cards. The card keeps its verdict for
    /// that exact tree, so the backstop neither re-gates every completed card
    /// on each tip change nor lets a red tip it did not cause reopen it.
    /// </summary>
    [Fact]
    public void Backstop_VerifiedCardStaysVerifiedAfterDevelopAdvances_WithoutAnotherGate()
    {
        var deliverySha = PublishDelivery("verified-then-advanced.txt", "gated delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        var gate = new CountingBuildTestGateRunner();
        var deps = Build(deliverySha, gateRunner: gate);
        var completed = CompleteAsAlreadyMergedWithoutEvidence(deps);
        // The exact-tree gate receipt the lane's own merge gate left behind.
        var evidenceDir = Path.Combine(completed.FolderPath, "post-steps");
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(
            Path.Combine(evidenceDir, "pre-develop-build-gate-1.log"),
            $"verdict=Ok exit=0 durationMs=20\n" +
            $"expectedSha={deliverySha} testedSha={deliverySha}\n" +
            "reason=the merge gate passed on this exact tree\n");
        var backstop = new AcceptedIntegrationBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            VerifyingRunner(deps, gate),
            deps.Integration,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AcceptedIntegrationBackstopHostedService>.Instance,
            deps.Transitions,
            deps.Timeline,
            deps.Pipeline);

        backstop.RunOnce();

        Assert.Equal(0, gate.Invocations);
        var verified = deps.Scanner.FindJob(Slug, _watchPath)!;
        var record = IntegrationVerificationStore.Read(verified.FolderPath);
        Assert.Equal(IntegrationVerificationStates.Verified, record?.State);
        Assert.Equal(deliverySha, record?.Sha);
        Assert.Contains(deliverySha, record!.DeliveryShas);

        // Another card's commit lands on develop after the verification.
        RunGit(_repo, "fetch", "-q", "origin");
        RunGit(_repo, "checkout", "-q", "-B", "advance-develop", "origin/develop");
        File.WriteAllText(Path.Combine(_repo, "someone-else.txt"), "a later card\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "feat(OTHER-1): a later delivery");
        var laterTip = Git(_repo, "rev-parse", "HEAD").Out.Trim();
        RunGit(_repo, "push", "-q", "origin", "HEAD:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin");
        RunGit(_repo, "checkout", "-q", "main");
        RunGit(_repo, "branch", "-D", "advance-develop");
        Assert.NotEqual(deliverySha, laterTip);

        var projected = deps.Integration.BuildLookup([verified])[verified.TaskKey];
        Assert.True(IntegrationStatuses.IsMerged(projected.Status));
        Assert.Equal(IntegrationVerificationStates.Verified, projected.Verification?.State);
        Assert.Equal(deliverySha, projected.Verification?.Sha);
        Assert.Contains(laterTip, projected.Verification?.Reason);
        Assert.Equal(AcceptedIntegrationRecoveryAction.Finalize,
            deps.Integration.ResolveAcceptedIntegrationRecovery(verified, projected).Action);

        backstop.RunOnce();

        Assert.Equal(0, gate.Invocations);
        Assert.Equal(TaskStates.Completed, deps.Scanner.FindJob(Slug, _watchPath)!.State);
    }

    private TaskInfo CompleteAsAlreadyMergedWithoutEvidence(Deps deps)
    {
        Assert.Equal(MoveJobStatus.Success, deps.States.MoveJob(Slug, TaskStates.Completed, _watchPath).Status);
        var completed = deps.Scanner.FindJob(Slug, _watchPath)!;
        deps.Pipeline.RecordStep(completed.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            StartedAt = VerificationFixtureAtUtc,
            CompletedAt = VerificationFixtureAtUtc.AddSeconds(1),
            Verdict = "already-merged",
            Reason = "Task branch already contained in develop; no merge needed.",
        });
        return deps.Scanner.FindJob(Slug, _watchPath)!;
    }

    /// <summary>The harness runner plus the task scanner and timeline the verification reads and writes.</summary>
    private static MergeIntoDevelopRunner VerifyingRunner(Deps deps, IBuildTestGateRunner gate)
        => new(
            new GitService(NullLogger<GitService>.Instance, deps.Scanner, deps.Configuration),
            deps.Pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: deps.Settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gate),
            preDevelopTimeout: TimeSpan.FromSeconds(30),
            attemptAuthority: deps.Authority,
            taskScanner: deps.Scanner,
            timeline: deps.Timeline);

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
        string initialState = TaskStates.HumanReview,
        string mode = TaskModes.Coding,
        string? briefVersion = null,
        bool seedContent = true,
        bool authorityRebuilt = false,
        bool guarded = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repo,
                ["WatchPaths:0:RepositoryPath"] = _repo,
                ["TaskRepository"] = _tempDir,
                ["DeliveryChain:Guarded"] = guarded ? "true" : "false",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        // The state machine writes the lane_changed rows; the same ledger
        // instance is read back by the integration assertions below.
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            notifier,
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        if (configureIntegrationBranch)
            settings.SetIntegrationBranch(Project, "develop");
        settings.SetIntegrationStrategy(Project, integrationStrategy);
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        if (gateRunner != null)
            settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var authority = new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance);
        var deliveryBriefVersion = briefVersion ?? RunDispositionPolicy.BriefVersion("Fixture brief");
        var sourceRun = authority.AcquireRun(
            TaskKey,
            "PROJ-FIXTURE",
            null,
            "agent-runner-01",
            "host-fixture",
            60,
            "claim-fixture",
            briefVersion: deliveryBriefVersion).RunAttempt!;
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
        BriefVersionStore.Record(jobFolder, "Fixture brief");
        var job = new
        {
            id = Slug,
            key = TaskKey,
            title = "Remote delivery",
            state = initialState,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode,
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
                ImmutableResultRef = $"refs/heads/{DeliveryRef}",
                IntegrationBranch = recordedIntegrationBranch,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        if (seedContent)
            SeedPassedContentReview(authority, sourceRun, jobFolder, deliverySha, deliveryBriefVersion);
        pipeline.Begin(jobFolder, PipelineCatalogue.Standard, Project, Slug);
        Assert.NotNull(scanner.FindJob(Slug, _watchPath));
        // AGT-3076: the 11.10. crash rebuilt the attempt authority store into
        // epoch 2; the card keeps its review subject and delivery evidence,
        // but the store no longer holds the RunAttempt the subject names.
        if (authorityRebuilt) authority = RebuiltAuthority();

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
            attemptAuthority: authority,
            configuration: config);
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
            authority,
            notifier);
    }

    private AttemptAuthorityService RebuiltAuthority()
    {
        var root = Path.Combine(_tempDir, "rebuilt-authority");
        var store = Path.Combine(root, AttemptAuthorityService.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, """{"authorityEpoch":2}""");
        var rebuilt = new AttemptAuthorityService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = root })
                .Build(),
            NullLogger<AttemptAuthorityService>.Instance);
        Assert.Equal(2, rebuilt.AuthorityEpoch);
        return rebuilt;
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

    private void SeedPassedContentReview(
        AttemptAuthorityService authority, RunAttemptDto sourceRun,
        string folder, string deliverySha, string briefVersion)
    {
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey, "PROJ-FIXTURE", deliverySha, sourceRun.AttemptId,
            briefVersion, AttemptAuthorityService.Hash("fixture-review-policy"), [],
            "content-fixture", RepositoryUrl: _origin, ResultRef: DeliveryRef));
        Assert.True(created.Accepted, created.Message);
        var claimed = authority.ClaimReview(created.AttemptId, "fixture-reviewer", "fixture-host",
            60, "claim-content-fixture");
        Assert.True(claimed.Accepted, claimed.Message);
        var review = claimed.ReviewAttempt!;
        var report = new Contract.ReviewReportRequest(
            "fixture-reviewer", "fixture-instance", review.Lease!.LeaseId,
            review.LastFence, "settle-content-fixture", "Pass", null, "Content fits the brief.",
            new Contract.ReviewWorkspaceProofDto("PROJ-FIXTURE", deliverySha, deliverySha,
                "fixture-tree", false, false, "fixture-workspace", "fixture-namespace"),
            new Contract.ReviewEnvironmentDto("fixture-host", "fixture-reviewer", "fixture-instance",
                "fixture-os", "fixture-arch", "fixture-runtime",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [new Contract.ReviewVerdictDto("requirement-fit", "pass", "Product",
                "The fixture delivery answers its brief.")],
            AuthorityEpoch: review.AuthorityEpoch, BriefSha256: briefVersion);
        var entry = new RemoteReviewSettlementEntry
        {
            AttemptId = review.AttemptId,
            TaskKey = TaskKey,
            IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Report = report,
            ReceivedAtUtc = VerificationFixtureAtUtc,
        };
        RemoteReviewSettlementJournal.Write(folder, entry);
        var settled = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(review.AttemptId, review.LastFence,
                review.AuthorityEpoch, report.IdempotencyKey),
            deliverySha, ReviewTerminalOutcome.Pass));
        Assert.True(settled.Accepted, settled.Message);
    }

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
                CreateNoWindow = true,
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
        AttemptAuthorityService Authority,
        TaskChangeNotifier Notifier);

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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <summary>Completes on the first entry, for loggers written off the test thread.</summary>
        public Task Logged => _logged.Task;

        public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
        {
            lock (Entries) return [.. Entries];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
            _logged.TrySetResult();
        }
    }

    private sealed class CountingBuildTestGateRunner(
        BuildTestGateVerdict verdict = BuildTestGateVerdict.Ok,
        string reason = "gate passed") : IBuildTestGateRunner
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
                verdict,
                verdict == BuildTestGateVerdict.Ok ? 0 : 1,
                0,
                string.Empty,
                reason,
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
