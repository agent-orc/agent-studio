using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Backwards-compatibility coverage for the optional <c>phase</c> field added
/// by the expanded-lifecycle-lanes hybrid V1 model
/// (<c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c>).
///
/// The compatibility contract: existing job folders that predate the
/// <c>phase</c> field must keep parsing and must render in the default lane
/// of their state. Mixed boards (some jobs with <c>phase</c>, some without)
/// must coexist. A hand-edited / corrupt phase value must be ignored, never
/// surfaced as a fatal scan error.
/// </summary>
public class LifecyclePhaseCompatibilityTests : IDisposable
{
    private readonly string _watchPath;

    public LifecyclePhaseCompatibilityTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "rdo-phase-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---- DefaultFor: pure mapping -------------------------------------------

    [Fact]
    public void DefaultFor_Ready_IsHumanReady()
    {
        Assert.Equal(LifecyclePhases.HumanReady,
            LifecyclePhases.DefaultFor(TaskStates.Ready, executionStatus: null, TaskSummaryStatus.None));
    }

    [Fact]
    public void DefaultFor_ProgressWithRunningExecution_IsExecutionRunning()
    {
        Assert.Equal(LifecyclePhases.ExecutionRunning,
            LifecyclePhases.DefaultFor(TaskStates.Progress, "running", TaskSummaryStatus.None));
    }

    [Fact]
    public void DefaultFor_ProgressWithGeneratingSummary_IsPostProcessingRunning()
    {
        Assert.Equal(LifecyclePhases.PostProcessingRunning,
            LifecyclePhases.DefaultFor(TaskStates.Progress, executionStatus: null, TaskSummaryStatus.Generating));
    }

    [Fact]
    public void DefaultFor_ProgressWithStoppedRun_FallsBackToExecution()
    {
        // Today's UI treats a stopped / failed card in 3-progress as the
        // execution lane. The lane projection preserves that.
        Assert.Equal(LifecyclePhases.ExecutionRunning,
            LifecyclePhases.DefaultFor(TaskStates.Progress, executionStatus: null, TaskSummaryStatus.None));
    }

    [Theory]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void DefaultFor_StatesWithoutSubstates_ReturnNull(string state)
    {
        Assert.Null(LifecyclePhases.DefaultFor(state, executionStatus: null, TaskSummaryStatus.None));
    }

    [Fact]
    public void DefaultFor_AutoReview_IsPostProcessingRunning()
    {
        Assert.Equal(LifecyclePhases.PostProcessingRunning,
            LifecyclePhases.DefaultFor(TaskStates.AutoReview, executionStatus: null, TaskSummaryStatus.None));
    }

    // ---- IsAllowed -----------------------------------------------------------

    [Fact]
    public void IsAllowed_NullPhase_AlwaysOk()
    {
        Assert.True(LifecyclePhases.IsAllowed(TaskStates.Ready, null));
        Assert.True(LifecyclePhases.IsAllowed(TaskStates.AutoReview, null));
    }

    [Fact]
    public void IsAllowed_StateMismatch_RejectsPhase()
    {
        Assert.False(LifecyclePhases.IsAllowed(TaskStates.Ready, LifecyclePhases.ExecutionRunning));
        Assert.False(LifecyclePhases.IsAllowed(TaskStates.Progress, LifecyclePhases.IntakeBlocked));
    }

    [Fact]
    public void IsAllowed_ProgressAcceptsVisibleNoSlotWaitPhases()
    {
        Assert.True(LifecyclePhases.IsAllowed(TaskStates.Progress, LifecyclePhases.LoopWaiting));
        Assert.True(LifecyclePhases.IsAllowed(TaskStates.Progress, LifecyclePhases.SteerPending));
        Assert.False(LifecyclePhases.IsAllowed(TaskStates.AutoReview, LifecyclePhases.LoopWaiting));
        Assert.True(LifecyclePhases.IsAllowed(TaskStates.HumanReview, LifecyclePhases.Integrating));
        Assert.False(LifecyclePhases.IsAllowed(TaskStates.Completed, LifecyclePhases.Integrating));
    }

    [Fact]
    public void IsAllowed_KnownUnconstrainedStatesRejectNonEmptyPhase()
    {
        Assert.False(LifecyclePhases.IsAllowed(TaskStates.Preparation, LifecyclePhases.HumanReady));
    }

    [Fact]
    public void IsAllowed_UnknownFutureStatesRemainPermissive()
    {
        Assert.True(LifecyclePhases.IsAllowed("8-future", LifecyclePhases.HumanReady));
    }

    // ---- Scanner: existing jobs without phase --------------------------------

    [Fact]
    public void Scanner_LegacyJob_NoPhaseField_ParsesWithNullPhase()
    {
        WriteJob(TaskStates.Ready, "alpha", phase: null);

        var info = BuildScanner().FindJob("alpha");

        Assert.NotNull(info);
        Assert.Null(info!.Phase);
        // Default-lane fallback covers the legacy folder.
        Assert.Equal(LifecyclePhases.HumanReady,
            LifecyclePhases.DefaultFor(info.State, info.Execution?.Status, TaskSummaryStatus.None));
    }

    [Fact]
    public void Scanner_LegacyJobInProgress_NoPhase_DefaultsToExecutionLane()
    {
        WriteJob(TaskStates.Progress, "stopped-run", phase: null);

        var info = BuildScanner().FindJob("stopped-run");

        Assert.NotNull(info);
        Assert.Null(info!.Phase);
        Assert.Equal(LifecyclePhases.ExecutionRunning,
            LifecyclePhases.DefaultFor(info.State, info.Execution?.Status, TaskSummaryStatus.None));
    }

    // ---- Scanner: jobs with explicit phase -----------------------------------

    [Fact]
    public void Scanner_JobWithIntakePhase_RoundTrips()
    {
        WriteJob(TaskStates.Ready, "intake-card", phase: LifecyclePhases.IntakeRunning);

        var info = BuildScanner().FindJob("intake-card");

        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakeRunning, info!.Phase);
    }

    [Fact]
    public void Scanner_JobWithPostProcessingPhase_RoundTrips()
    {
        WriteJob(TaskStates.Progress, "post-card", phase: LifecyclePhases.PostProcessingRunning);

        var info = BuildScanner().FindJob("post-card");

        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.PostProcessingRunning, info!.Phase);
    }

    [Fact]
    public void Scanner_AutoReviewJobWithPostProcessingPhase_RoundTrips()
    {
        WriteJob(TaskStates.AutoReview, "post-review-card", phase: LifecyclePhases.PostProcessingRunning);

        var info = BuildScanner().FindJob("post-review-card");

        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.PostProcessingRunning, info!.Phase);
    }

    [Fact]
    public void Scanner_PhaseFromWrongState_IsDroppedNotFatal()
    {
        // Hand-edited task.json puts an execution phase on a 2-ready card.
        // The scanner must drop it (logging a warning) rather than wedge the
        // board or echo nonsense back to the UI.
        WriteJob(TaskStates.Ready, "bad-phase", phase: LifecyclePhases.ExecutionRunning);

        var info = BuildScanner().FindJob("bad-phase");

        Assert.NotNull(info);
        Assert.Null(info!.Phase);
    }

    [Fact]
    public void Scanner_UnknownPhaseString_IsDroppedNotFatal()
    {
        WriteJob(TaskStates.Ready, "garbled", phase: "totally-made-up");

        var info = BuildScanner().FindJob("garbled");

        Assert.NotNull(info);
        Assert.Null(info!.Phase);
    }

    // ---- Scanner: mixed boards -----------------------------------------------

    [Fact]
    public void Scanner_MixedBoard_LegacyAndPhased_AllParse()
    {
        // Legacy cards (no phase) and phased cards coexist across multiple
        // states. The scan must surface every one with its expected phase
        // value, and every legacy card must default-resolve to the right
        // lane via DefaultFor.
        WriteJob(TaskStates.Preparation, "draft-1",  phase: null);
        WriteJob(TaskStates.Ready,       "ready-legacy", phase: null);
        WriteJob(TaskStates.Ready,       "ready-intake", phase: LifecyclePhases.IntakeRunning);
        WriteJob(TaskStates.Progress,    "exec-legacy",  phase: null);
        WriteJob(TaskStates.Progress,    "exec-post",    phase: LifecyclePhases.PostProcessingRunning);
        WriteJob(TaskStates.AutoReview,  "review-card",  phase: null);
        WriteJob(TaskStates.Completed,   "done-card",    phase: null);

        var jobs = BuildScanner().ScanAllJobs().ToDictionary(j => j.Id);

        Assert.Equal(7, jobs.Count);

        Assert.Null(jobs["draft-1"].Phase);
        Assert.Null(jobs["ready-legacy"].Phase);
        Assert.Equal(LifecyclePhases.IntakeRunning, jobs["ready-intake"].Phase);
        Assert.Null(jobs["exec-legacy"].Phase);
        Assert.Equal(LifecyclePhases.PostProcessingRunning, jobs["exec-post"].Phase);
        Assert.Null(jobs["review-card"].Phase);
        Assert.Null(jobs["done-card"].Phase);

        // Default-lane projection: legacy cards land in the right lane.
        Assert.Equal(LifecyclePhases.HumanReady,
            LifecyclePhases.DefaultFor(jobs["ready-legacy"].State, null, TaskSummaryStatus.None));
        Assert.Equal(LifecyclePhases.ExecutionRunning,
            LifecyclePhases.DefaultFor(jobs["exec-legacy"].State, null, TaskSummaryStatus.None));
        Assert.Equal(LifecyclePhases.PostProcessingRunning,
            LifecyclePhases.DefaultFor(jobs["review-card"].State, null, TaskSummaryStatus.None));
    }

    [Fact]
    public void Scanner_DoesNotRewriteJobJsonForLegacyJobs()
    {
        // The compatibility constraint from the task prompt: do not rewrite
        // every job folder just to add default metadata. The scan path must
        // leave a phase-less task.json alone so an idle backend boot does not
        // touch every card on the board.
        WriteJob(TaskStates.Ready, "untouched", phase: null);
        var path = Path.Combine(_watchPath, TaskStates.Ready, "untouched", "task.json");
        var before = File.ReadAllText(path);
        var beforeMtime = File.GetLastWriteTimeUtc(path);

        // Run a couple of full scans.
        var scanner = BuildScanner();
        scanner.ScanAllJobs();
        scanner.ScanAllJobs();

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(beforeMtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void LaneChange_DoesNotCarryTheIntegrationPhaseIntoHumanReview()
    {
        // AGT-3621: a promotion out of Auto Review kept phase `integrating`. In
        // Human Review that reads as an interrupted operator acceptance, and the
        // backstop completed the card although nobody had accepted it.
        WriteJob(TaskStates.AutoReview, "promoted", phase: LifecyclePhases.Integrating);
        var scanner = BuildScanner();
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);

        var moved = states.MoveJob("promoted", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        scanner.InvalidateCache();
        var promoted = scanner.FindJob("promoted", _watchPath);
        Assert.Equal(TaskStates.HumanReview, promoted!.State);
        Assert.Null(promoted.Phase);
    }

    // ---- helpers -------------------------------------------------------------

    private void WriteJob(string state, string slug, string? phase)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var phaseField = phase is null ? "" : $",\"phase\":\"{phase}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"ownerClientId\":\"default\"{phaseField}}}");
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }
}
