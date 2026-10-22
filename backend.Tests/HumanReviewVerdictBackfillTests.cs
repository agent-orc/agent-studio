using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the migration half of the bug
/// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
/// the boot-time sweep <see cref="ReviewDecisionOrchestrator.BackfillVerdictlessHumanReview"/>
/// gives every <c>5-human-review</c> card that carries NO decision-journal
/// record a retroactive <see cref="ReviewDecisionKind.Escalate"/> verdict,
/// moves it to <c>5e-escalated</c>,
/// (category <see cref="HumanReviewEscalationCategories.UnknownLegacy"/>) and a
/// <c>status.md</c> stub, while leaving already-explained cards untouched. The
/// sweep is idempotent: a second run is a no-op.
/// </summary>
public sealed class HumanReviewVerdictBackfillTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly TimelineLog _timeline;
    private readonly ReviewDecisionOrchestrator _orchestrator;

    public HumanReviewVerdictBackfillTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-hrvb-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _states = new TaskStateMachine(
            _scanner,
            NullLogger<TaskStateMachine>.Instance,
            timeline: _timeline);
        var clients = new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance);
        _mutations = new TaskMutationService(
            _scanner, clients, new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, prompts);
        _transitions = new TaskTransitionService(
            _scanner,
            _states,
            _mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            timeline: _timeline);
        var indexCache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            _scanner, _mutations, _states, _transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var funnel = new HumanReviewEscalation(
            _states,
            _transitions,
            _workspaceRoot,
            NullLogger<HumanReviewEscalation>.Instance);
        var sessions = new TaskSessionLog(_scanner, NullLogger<TaskSessionLog>.Instance);

        _orchestrator = new ReviewDecisionOrchestrator(
            _scanner, _states, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), _config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            sessions: sessions,
            timeline: _timeline,
            humanReviewEscalation: funnel);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Backfill_RepairsVerdictlessCard_AndLeavesExplainedCardUntouched()
    {
        // Legacy card: old, parked in 5-human-review after a real run, with NO
        // verdict and no status.
        WriteJob(
            TaskStates.HumanReview,
            "legacy-verdictless",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);
        // Already-explained card: carries a prior accept verdict + a real summary.
        WriteJob(TaskStates.HumanReview, "already-explained");
        ReviewDecisionLog.Append(_workspaceRoot, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow, JobId: "already-explained", Project: ProjectName,
            Kind: ReviewDecisionKind.AcceptAsDone, Reason: "looks good", Prompt: "p", Response: "r", FollowUp: ""));
        const string realSummary = "# Status\n\n- Result: done well.\n";
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "already-explained", "status.md"), realSummary);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        // Legacy card got an escalate verdict (unknown-legacy), moved to
        // 5e-escalated, and received a status stub.
        var legacy = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "legacy-verdictless").ToList();
        Assert.Single(legacy);
        Assert.Equal(ReviewDecisionKind.Escalate, legacy[0].Kind);
        Assert.Contains(HumanReviewEscalationCategories.UnknownLegacy, legacy[0].Reason);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "legacy-verdictless")));
        var escalatedPath = Path.Combine(_watchPath, TaskStates.Escalated, "legacy-verdictless");
        Assert.True(Directory.Exists(escalatedPath));
        var stub = File.ReadAllText(Path.Combine(escalatedPath, "status.md"));
        Assert.False(string.IsNullOrWhiteSpace(stub));
        var backfillMove = Assert.Single(
            _timeline.ReadAll(escalatedPath),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TimelineActors.System, backfillMove.Actor);
        Assert.Contains("Parked in human review", backfillMove.Details!["reason"]);

        // Explained card untouched: no extra verdict, summary preserved.
        var explained = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "already-explained").ToList();
        Assert.Single(explained);
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, explained[0].Kind);
        Assert.Equal(realSummary,
            File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "already-explained", "status.md")));
    }

    [Fact]
    public void Backfill_IsIdempotent_SecondRunAddsNoNewRecords()
    {
        WriteJob(
            TaskStates.HumanReview,
            "legacy-verdictless",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);
        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var records = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "legacy-verdictless").ToList();
        Assert.Single(records);
    }

    [Fact]
    public void Backfill_DoesNotTouchFreshVerdictlessCardWithoutRun()
    {
        var id = _mutations.CreateJob(new CreateTaskRequest
        {
            Id = "fresh-operator-card",
            Title = "Fresh operator card",
            WatchPath = _watchPath,
            TargetState = TaskStates.HumanReview,
        });
        Assert.Equal("fresh-operator-card", id);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var card = _scanner.FindJob(id!, _watchPath);
        Assert.NotNull(card);
        Assert.Equal(TaskStates.HumanReview, card!.State);
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
    }

    [Fact]
    public void Backfill_RequiresBothMinimumAgeAndRunProvenance()
    {
        WriteJob(
            TaskStates.HumanReview,
            "old-without-run",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: false);
        WriteJob(
            TaskStates.HumanReview,
            "fresh-with-run",
            createdAt: DateTime.UtcNow,
            hasRun: true);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        Assert.True(Directory.Exists(
            Path.Combine(_watchPath, TaskStates.HumanReview, "old-without-run")));
        Assert.True(Directory.Exists(
            Path.Combine(_watchPath, TaskStates.HumanReview, "fresh-with-run")));
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
    }

    [Fact]
    public void Backfill_DoesNotTouchCardsOutsideHumanReview()
    {
        WriteJob(TaskStates.AutoReview, "in-auto-review");
        WriteJob(TaskStates.Progress, "in-progress");

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
    }

    [Fact]
    public async Task Backfill_LatestOperatorMoveIsVerdict_AndSurvivesBootInChosenLane()
    {
        WriteJob(
            TaskStates.AutoReview,
            "operator-placed-human-review",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);

        const string reason = "Operator reviewed the evidence and wants this card held here.";
        var moved = await _transitions.MoveAsync(
            "operator-placed-human-review",
            TaskStates.HumanReview,
            _watchPath,
            cause: TimelineActors.Human("night-operator"),
            reason: reason);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var card = _scanner.FindJob("operator-placed-human-review", _watchPath);
        Assert.NotNull(card);
        Assert.Equal(TaskStates.HumanReview, card!.State);
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));

        var latestMove = _timeline.ReadAll(card.FolderPath)
            .Last(row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TimelineActors.Human("night-operator"), latestMove.Actor);
        Assert.Equal(reason, latestMove.Details!["reason"]);
    }

    [Fact]
    public async Task Backfill_OperatorRequeuedReadyCard_RemainsReady()
    {
        WriteJob(
            TaskStates.HumanReview,
            "operator-requeued-ready",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);

        const string reason = "Night-wave infrastructure is healthy; run this card again.";
        var moved = await _transitions.MoveAsync(
            "operator-requeued-ready",
            TaskStates.Ready,
            _watchPath,
            cause: TimelineActors.Human("night-operator"),
            reason: reason);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var card = _scanner.FindJob("operator-requeued-ready", _watchPath);
        Assert.NotNull(card);
        Assert.Equal(TaskStates.Ready, card!.State);
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
        var latestMove = _timeline.ReadAll(card.FolderPath)
            .Last(row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(reason, latestMove.Details!["reason"]);
    }

    [Fact]
    public void Backfill_UsesLatestLaneChange_NotEarlierOperatorMove()
    {
        WriteJob(
            TaskStates.AutoReview,
            "system-parked-after-operator-history",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);

        Assert.Equal(
            MoveJobStatus.Success,
            _states.MoveJob(
                "system-parked-after-operator-history",
                TaskStates.HumanReview,
                _watchPath,
                cause: TimelineActors.Human("night-operator"),
                reason: "Inspect this intermediate result.").Status);
        Assert.Equal(
            MoveJobStatus.Success,
            _states.MoveJob(
                "system-parked-after-operator-history",
                TaskStates.AutoReview,
                _watchPath,
                cause: TimelineActors.System,
                reason: "Runner resumed review processing.").Status);
        Assert.Equal(
            MoveJobStatus.Success,
            _states.MoveJob(
                "system-parked-after-operator-history",
                TaskStates.HumanReview,
                _watchPath,
                cause: TimelineActors.System,
                reason: "Runner parked without an automated verdict.").Status);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var card = _scanner.FindJob("system-parked-after-operator-history", _watchPath);
        Assert.NotNull(card);
        Assert.Equal(TaskStates.Escalated, card!.State);
        Assert.Single(
            ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName),
            row => row.JobId == card.Id && row.Kind == ReviewDecisionKind.Escalate);
    }

    [Fact]
    public void Backfill_CompletedCardWithRunProvenance_RemainsUntouched()
    {
        WriteJob(
            TaskStates.Completed,
            "accepted-terminal",
            createdAt: DateTime.UtcNow.AddDays(-30),
            hasRun: true);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var card = _scanner.FindJob("accepted-terminal", _watchPath);
        Assert.NotNull(card);
        Assert.Equal(TaskStates.Completed, card!.State);
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
        Assert.Empty(_timeline.ReadAll(card.FolderPath));
    }


    private void WriteJob(
        string state,
        string slug,
        DateTime? createdAt = null,
        bool hasRun = false,
        string kind = TaskKinds.Task)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var created = createdAt ?? DateTime.UtcNow.AddDays(-30);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"createdAt\":\"{created:o}\",\"enteredLaneAt\":\"{created:o}\"," +
            $"\"agent\":\"claude\",\"cliType\":\"claude\",\"kind\":\"{kind}\"," +
            "\"ownerClientId\":\"local-default\"}");

        if (!hasRun) return;

        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(
            Path.Combine(logs, "session-events.jsonl"),
            $"{{\"ts\":\"{created.AddMinutes(1):o}\",\"kind\":\"start\",\"cli\":\"claude\"}}{Environment.NewLine}");
    }
}
