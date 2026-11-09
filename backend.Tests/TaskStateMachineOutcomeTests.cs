using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// <see cref="TaskStateMachine.MoveJob"/> must return the absolute path of the
/// post-move folder in <see cref="MoveJobOutcome.NewFolderPath"/> so callers
/// (chat-log writes, follow-up file emission) can target the moved folder
/// without round-tripping through the scanner cache. The cache may not yet
/// reflect the move when those callers run; falling back to the stale source
/// path resurrects 4-auto-review as a one-line skeleton (2026-05-16 incident).
/// </summary>
public class TaskStateMachineOutcomeTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskStateMachineOutcomeTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-outcome-tests-" + Guid.NewGuid().ToString("N"));
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
    public void MoveJob_Success_ReturnsAuthoritativeNewFolderPath()
    {
        SeedJob(TaskStates.AutoReview, "promote-me");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("promote-me", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.HumanReview, "promote-me"),
            outcome.NewFolderPath);

        // The path the outcome reports actually exists on disk; the source path is gone.
        Assert.True(Directory.Exists(outcome.NewFolderPath));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "promote-me")));
    }

    [Fact]
    public void MoveJob_NoOpWhenAlreadyInTargetState_ReturnsCurrentFolderPath()
    {
        SeedJob(TaskStates.HumanReview, "already-here");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("already-here", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.HumanReview, "already-here"),
            outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_NotFound_NewFolderPathIsNull()
    {
        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("does-not-exist", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
        Assert.Null(outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_TargetFolderExists_AutoSuffixesAndReturnsSuffixedPath()
    {
        // FindJob resolves the earliest-state copy (4-auto-review) as the move
        // source. The move into 5-human-review collides on the occupied slug;
        // Layer 2 suffixes the moved folder and returns the authoritative
        // suffixed path rather than failing.
        SeedJob(TaskStates.AutoReview, "collide");
        SeedJob(TaskStates.HumanReview, "collide");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("collide", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.HumanReview, "collide-2"),
            outcome.NewFolderPath);
        Assert.True(Directory.Exists(outcome.NewFolderPath));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "collide")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "collide")));
    }

    [Fact]
    public void MoveJob_ClearsPostProcessingPhase_WhenMovingToHumanReview()
    {
        SeedJob(TaskStates.AutoReview, "phase-task", LifecyclePhases.PostProcessingRunning);

        var (machine, scanner) = BuildMachine();
        var outcome = machine.MoveJob("phase-task", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = scanner.FindJob("phase-task", _watchPath);
        Assert.NotNull(moved);
        Assert.Equal(TaskStates.HumanReview, moved!.State);
        Assert.Null(moved.Phase);

        var taskJson = File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "phase-task", "task.json"));
        Assert.Contains("\"phase\": \"\"", taskJson);
    }

    [Theory]
    [InlineData(TaskStates.AutoReview, TaskStates.Ready)]
    [InlineData(TaskStates.Ready, TaskStates.Progress)]
    public void MoveJob_NewAttemptState_InvalidatesRemoteReviewSubject(
        string sourceState,
        string targetState)
    {
        const string slug = "new-attempt";
        SeedJob(sourceState, slug);
        var sourceFolder = Path.Combine(_watchPath, sourceState, slug);
        ReviewSubjectStore.Write(sourceFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-NEW-ATTEMPT",
            RunAttemptId = "run-old",
            Project = Project,
            Repository = "fixture",
            ResultSha = new string('a', 40),
            AttemptChainId = "lease-old",
            Executor = "remote-runner",
            LeaseId = "lease-old",
            FencingToken = 1,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob(slug, targetState, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);
        Assert.Null(ReviewSubjectStore.Read(outcome.NewFolderPath!));
        Assert.True(File.Exists(ReviewSubjectStore.PathFor(outcome.NewFolderPath!) + ".invalidated"));
    }

    // AGT-3586: the park marker records the lane-change cause, so the park copy
    // can say the acceptance audit, not "the parking run", parked the card.
    [Fact]
    public void MoveJob_IntoParkedLane_RecordsTheTransitionCauseOnTheMarker()
    {
        SeedJob(TaskStates.Completed, "audit-park");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("audit-park", TaskStates.Escalated, _watchPath,
            cause: TimelineActors.System,
            reason: "No delivery branch was found for this card, so its work cannot be integrated.",
            transitionCause: LaneChangeCauses.AcceptanceIntegrationFailed,
            transitionDetail: "missing-delivery");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var marker = ParkedBlockerMarker.TryRead(outcome.NewFolderPath!);
        Assert.NotNull(marker);
        Assert.Equal(LaneChangeCauses.AcceptanceIntegrationFailed, marker!.Cause);
        Assert.Equal("missing-delivery", marker.CauseDetail);
        var status = ParkedBlockerMarker.ToStatus(marker, marker.ParkedAt)!;
        Assert.Equal(LaneChangeCauses.AcceptanceIntegrationFailed, status.Cause);
        Assert.Equal("missing-delivery", status.CauseDetail);
    }

    [Fact]
    public void MoveJob_OperatorParkWithoutExplicitCause_RecordsTheDerivedOperatorCause()
    {
        SeedJob(TaskStates.Escalated, "operator-park");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("operator-park", TaskStates.HumanReview, _watchPath,
            cause: TimelineActors.Human("operator"));

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var marker = ParkedBlockerMarker.TryRead(outcome.NewFolderPath!)!;
        Assert.Equal(LaneChangeCauses.OperatorDecision, marker.Cause);
        Assert.Null(marker.CauseDetail);
        // The reason fallback still reads the explicit cause only.
        Assert.Equal("Parked with no recorded cause.", marker.Reason);
    }

    private void SeedJob(string state, string slug, string? phase = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var phaseJson = phase == null ? "" : $",\"phase\":\"{phase}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"{phaseJson}}}");
    }

    private (TaskStateMachine, TaskScannerService) BuildMachine()
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
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        return (machine, scanner);
    }
}
