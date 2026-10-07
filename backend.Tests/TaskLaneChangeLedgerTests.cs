using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// T2b (ASS-1740) Part 1: every lane crossing must append one
/// <see cref="TimelineEventKinds.LaneChanged"/> row to the task's
/// <c>logs/timeline.jsonl</c> ledger - the append-only transition HISTORY that
/// the single <c>enteredLaneAt</c> field on <c>task.json</c> could never hold
/// (it only ever remembers the latest move). The row carries von / nach (in
/// <see cref="TimelineEvent.Details"/>), wann (the event <c>Ts</c>), and the
/// ausloeser (the <see cref="TimelineEvent.Actor"/>).
/// </summary>
public class TaskLaneChangeLedgerTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskLaneChangeLedgerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-lanechange-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MoveJob_AppendsLaneChangedRow_WithFromToAndTrigger()
    {
        SeedJob(TaskStates.AutoReview, "promote-me");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "promote-me",
            TaskStates.HumanReview,
            _watchPath,
            cause: TimelineActors.Human("alice@example.com"),
            reason: "Operator accepted the reviewed placement.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);

        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        var laneChange = Assert.Single(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal("human:alice@example.com", laneChange.Actor);
        Assert.NotNull(laneChange.Details);
        Assert.Equal(TaskStates.AutoReview, laneChange.Details!["from"]);
        Assert.Equal(TaskStates.HumanReview, laneChange.Details!["to"]);
        Assert.Equal("Operator accepted the reviewed placement.", laneChange.Details!["reason"]);
        Assert.Contains(TaskStates.HumanReview, laneChange.Summary);
    }

    [Fact]
    public void MoveJob_NullCause_RecordsSystemActor()
    {
        SeedJob(TaskStates.Ready, "auto-pick");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob("auto-pick", TaskStates.Progress, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        var laneChange = Assert.Single(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TimelineActors.System, laneChange.Actor);
    }

    [Fact]
    public void MoveJob_NoOp_DoesNotAppendLaneChangedRow()
    {
        SeedJob(TaskStates.HumanReview, "already-here");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob("already-here", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        Assert.DoesNotContain(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
    }

    [Fact]
    public void MoveJob_ExpectedSourceMismatch_DoesNotReopenCompletedTask()
    {
        SeedJob(TaskStates.Completed, "accepted");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "accepted",
            TaskStates.HumanReview,
            _watchPath,
            expectedSourceState: TaskStates.AutoReview);

        Assert.Equal(MoveJobStatus.SourceStateMismatch, outcome.Status);
        var completedFolder = Path.Combine(_watchPath, TaskStates.Completed, "accepted");
        Assert.True(Directory.Exists(completedFolder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "accepted")));
        Assert.DoesNotContain(
            timeline.ReadAll(completedFolder),
            row => row.Kind == TimelineEventKinds.LaneChanged);
    }

    [Fact]
    public void MoveJob_ExplicitTransitionCause_WritesCauseAndQualifier()
    {
        SeedJob(TaskStates.Ready, "claim-me");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "claim-me",
            TaskStates.Progress,
            _watchPath,
            cause: "remote-runner:agent-runner-01",
            transitionCause: LaneChangeCauses.Claimed,
            transitionDetail: "claim-replay");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(timeline.ReadAll(outcome.NewFolderPath!), r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.Claimed, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("claim-replay", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
        Assert.Equal("remote-runner:agent-runner-01", laneChange.Actor);
    }

    [Fact]
    public void MoveJob_SystemActorWithoutTransitionCause_LeavesCauseAbsent()
    {
        SeedJob(TaskStates.Progress, "legacy-row");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob("legacy-row", TaskStates.Ready, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(timeline.ReadAll(outcome.NewFolderPath!), r => r.Kind == TimelineEventKinds.LaneChanged);
        // An automatic move whose site does not name its cause stays a legacy
        // row for the analysis to infer, instead of carrying a generic id.
        Assert.False(laneChange.Details!.ContainsKey(LaneChangeCauses.DetailKey));
        Assert.False(laneChange.Details!.ContainsKey(LaneChangeCauses.DetailQualifierKey));
    }

    [Theory]
    [InlineData(TaskStates.HumanReview, TaskStates.Ready, LaneChangeCauses.OperatorRequeue)]
    [InlineData(TaskStates.AutoReview, TaskStates.Ready, LaneChangeCauses.OperatorRequeue)]
    [InlineData(TaskStates.Escalated, TaskStates.Ready, LaneChangeCauses.EscalationRequeue)]
    [InlineData(TaskStates.Completed, TaskStates.Backlog, LaneChangeCauses.CompletedReopen)]
    [InlineData(TaskStates.Progress, TaskStates.Ready, LaneChangeCauses.OperatorMove)]
    [InlineData(TaskStates.Backlog, TaskStates.Ready, LaneChangeCauses.Promoted)]
    [InlineData(TaskStates.Backlog, TaskStates.Preparation, LaneChangeCauses.Promoted)]
    [InlineData(TaskStates.Ready, TaskStates.Progress, LaneChangeCauses.OperatorMove)]
    [InlineData(TaskStates.Progress, TaskStates.AutoReview, LaneChangeCauses.OperatorMove)]
    [InlineData(TaskStates.HumanReview, TaskStates.Escalated, LaneChangeCauses.Escalated)]
    [InlineData(TaskStates.Escalated, TaskStates.HumanReview, LaneChangeCauses.OperatorDecision)]
    [InlineData(TaskStates.AutoReview, TaskStates.HumanReview, LaneChangeCauses.OperatorMove)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, LaneChangeCauses.Accepted)]
    [InlineData(TaskStates.Completed, TaskStates.Archive, LaneChangeCauses.Archived)]
    [InlineData(TaskStates.Archive, TaskStates.Completed, LaneChangeCauses.OperatorMove)]
    public void MoveJob_HumanActorWithoutTransitionCause_DerivesOperatorCauseFromLanePair(
        string from, string to, string expectedCause)
    {
        SeedJob(from, "operator-move");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "operator-move",
            to,
            _watchPath,
            cause: TimelineActors.Human("alice@example.com"),
            reason: "Operator decision.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(timeline.ReadAll(outcome.NewFolderPath!), r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(expectedCause, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("Operator decision.", laneChange.Details!["reason"]);
        Assert.False(laneChange.Details!.ContainsKey(LaneChangeCauses.DetailQualifierKey));
    }

    [Fact]
    public void MoveJob_HumanActorWithExplicitTransitionCause_KeepsTheExplicitCause()
    {
        SeedJob(TaskStates.HumanReview, "explicit-wins");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "explicit-wins",
            TaskStates.Ready,
            _watchPath,
            cause: TimelineActors.Human("alice@example.com"),
            transitionCause: LaneChangeCauses.IntegrationRecovery,
            transitionDetail: "Conflict");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(timeline.ReadAll(outcome.NewFolderPath!), r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.IntegrationRecovery, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("Conflict", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public void PromoteToReadyTop_StampsTheTransitionCauseOnTheLaneRow()
    {
        SeedJob(TaskStates.HumanReview, "recover-me");

        var (machine, _, timeline) = BuildMachine();
        var position = machine.PromoteToReadyTop(
            "recover-me",
            _watchPath,
            transitionCause: LaneChangeCauses.IntegrationRecovery,
            transitionDetail: "conflict-skipped");

        Assert.Equal(1, position);
        var folder = Path.Combine(_watchPath, TaskStates.Ready, "recover-me");
        var laneChange = Assert.Single(timeline.ReadAll(folder), r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TaskStates.HumanReview, laneChange.Details!["from"]);
        Assert.Equal(TaskStates.Ready, laneChange.Details!["to"]);
        Assert.Equal(LaneChangeCauses.IntegrationRecovery, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("conflict-skipped", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public void PromoteToReadyTop_HumanActor_DerivesTheOperatorCause()
    {
        SeedJob(TaskStates.Backlog, "do-next");

        var (machine, _, timeline) = BuildMachine();
        var position = machine.PromoteToReadyTop("do-next", _watchPath, cause: TimelineActors.Human("bob@example.com"));

        Assert.Equal(1, position);
        var folder = Path.Combine(_watchPath, TaskStates.Ready, "do-next");
        var laneChange = Assert.Single(timeline.ReadAll(folder), r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal("human:bob@example.com", laneChange.Actor);
        Assert.Equal(LaneChangeCauses.Promoted, laneChange.Details![LaneChangeCauses.DetailKey]);
    }

    [Fact]
    public void PromoteToReadyTop_MissingTask_ReturnsZero()
    {
        var (machine, _, _) = BuildMachine();

        Assert.Equal(0, machine.PromoteToReadyTop("missing", _watchPath));
    }

    [Fact]
    public void PromoteToReadyTop_RejectedLaneMove_ReturnsZero()
    {
        SeedJob(TaskStates.HumanReview, "not-ready");
        var (machine, _, _) = BuildMachine();

        var position = machine.PromoteToReadyTop(
            "not-ready", _watchPath, expectedSourceState: TaskStates.Ready);

        Assert.Equal(0, position);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "not-ready")));
    }

    [Fact]
    public void ArchiveFolder_AndFailedPickupMoves_NameTheirCause()
    {
        SeedJob(TaskStates.Progress, "stale-one");
        SeedJob(TaskStates.Progress, "stale-two");
        var (machine, _, timeline) = BuildMachine();

        var archived = machine.ArchiveFolder(Path.Combine(_watchPath, TaskStates.Progress, "stale-one"), "stale-one");
        var deadLettered = machine.MoveFolderToFailedPickup(Path.Combine(_watchPath, TaskStates.Progress, "stale-two"), "stale-two-pickup-failed-2026-08-23");

        Assert.Equal(MoveJobStatus.Success, archived.Status);
        Assert.Equal(MoveJobStatus.Success, deadLettered.Status);
        var archiveRow = Assert.Single(
            timeline.ReadAll(Path.Combine(_watchPath, TaskStates.Archive, "stale-one")),
            r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.Archived, archiveRow.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("stale-folder-archive", archiveRow.Details![LaneChangeCauses.DetailQualifierKey]);
        var deadLetterRow = Assert.Single(
            timeline.ReadAll(Path.Combine(_watchPath, TaskStates.FailedPickup, "stale-two-pickup-failed-2026-08-23")),
            r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.SystemMove, deadLetterRow.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("dead-letter-failed-pickup", deadLetterRow.Details![LaneChangeCauses.DetailQualifierKey]);

        var restored = machine.RestoreFromFailedPickup("stale-two-pickup-failed-2026-08-23", _watchPath, keepDeadLetterSlug: false);
        Assert.Equal(RestoreFromFailedPickupStatus.Success, restored.Status);
        var restoreRow = timeline.ReadAll(Path.Combine(_watchPath, TaskStates.Ready, "stale-two"))
            .Last(r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TaskStates.Ready, restoreRow.Details!["to"]);
        Assert.Equal(LaneChangeCauses.OperatorMove, restoreRow.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("restore-from-failed-pickup", restoreRow.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public void MoveJob_MultipleCrossings_AccumulateAppendOnlyHistory()
    {
        SeedJob(TaskStates.Ready, "round-trip");

        var (machine, _, timeline) = BuildMachine();
        var toProgress = machine.MoveJob("round-trip", TaskStates.Progress, _watchPath);
        var toReview = machine.MoveJob("round-trip", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, toReview.Status);
        var rows = timeline.ReadAll(toReview.NewFolderPath!);
        var laneChanges = rows.Where(r => r.Kind == TimelineEventKinds.LaneChanged).ToList();
        Assert.Equal(2, laneChanges.Count);
        Assert.Equal(TaskStates.Progress, laneChanges[0].Details!["to"]);
        Assert.Equal(TaskStates.AutoReview, laneChanges[1].Details!["to"]);
    }

    private void SeedJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private (TaskStateMachine, TaskScannerService, TimelineLog) BuildMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        return (machine, scanner, timeline);
    }
}
