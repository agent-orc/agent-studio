using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class MergeIntoDevelopRunnerTests
{
    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData("shutdown")]
    [InlineData("operator")]
    [InlineData("superseded")]
    public async Task Merge_gate_cancellation_rolls_back_and_routes_by_lifecycle(string reason)
    {
        var repo = SeedRepo("merge-cancel");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/cancel");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var before = RunGit(repo, "rev-parse develop").Out.Trim();
        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var folder = BeginRun(log, repo, jobId: "cancel");
        var clock = new MergeCancellationClock();
        var oldSubject = new ReviewSubjectRecord
        {
            ResultSha = RunGit(repo, "rev-parse task/cancel").Out.Trim(), AttemptChainId = "old", RunAttemptId = "old", Executor = "",
            ResultRef = "task/cancel", Project = "Fixture", TaskKey = "cancel", Repository = repo,
            CompletedAtUtc = clock.GetUtcNow(),
        };
        ReviewSubjectStore.Write(folder, oldSubject);
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, "", "cancelled", false, false)
            { FailureKind = BuildTestGateFailureKind.Cancellation,
                CancellationReason = reason == "operator" ? reason : "shutdown" },
            duringRun: () =>
            {
                if (reason == "superseded") ReviewSubjectStore.Write(folder,
                    oldSubject with { ResultSha = new string('b', 40) });
            });
        var queue = new IntegrationPushQueue();
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue, projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gate), timeline: timeline, timeProvider: clock);

        var outcome = await runner.RunAsync("Fixture", "cancel", folder, repo, "develop", CancellationToken.None);

        Assert.Equal(before, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.False(queue.Reader.TryRead(out _));
        var step = ReadMergeStep(log, folder)!;
        Assert.Equal(reason, Assert.Single(timeline.ReadAll(folder), e => e.Kind == "gate-cancelled").Details!["reason"]);
        if (reason == "shutdown") Assert.Equal(AcceptedIntegrationFailureCodes.GateInterrupted, step.FailureCode);
        else if (reason == "superseded")
        {
            Assert.Equal("superseded", step.Verdict);
            Assert.Equal(PipelineStepStatus.Skipped, step.Status);
            Assert.Null(step.FailureCode);
        }
        else Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, outcome.Outcome);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Lane_move_during_merge_gate_records_cancellation_on_live_card_only()
    {
        var repo = SeedRepo("merge-cancel-lane-move");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/moved");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var watchPath = Path.Combine(_tempDir, "merge-cancel-lane-move-jobs");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, state));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var oldFolder = Path.Combine(watchPath, TaskStates.AutoReview, "moved");
        var liveFolder = Path.Combine(watchPath, TaskStates.HumanReview, "moved");
        Directory.CreateDirectory(oldFolder);
        void WriteTask(string folder, string state) => File.WriteAllText(Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = "moved", key = "AGT-MOVED", title = "moved", state, order = 1,
                projectName = "Fixture", mode = TaskModes.Coding,
            }));
        WriteTask(oldFolder, TaskStates.AutoReview);
        log.Begin(oldFolder, PipelineCatalogue.Standard, "Fixture", "moved");
        var clock = new MergeCancellationClock();
        ReviewSubjectStore.Write(oldFolder, new ReviewSubjectRecord
        {
            ResultSha = RunGit(repo, "rev-parse task/moved").Out.Trim(),
            AttemptChainId = "old", RunAttemptId = "old", Executor = "",
            ResultRef = "task/moved", Project = "Fixture", TaskKey = "moved", Repository = repo,
            CompletedAtUtc = clock.GetUtcNow(),
        });
        Assert.Equal(oldFolder, scanner.FindJob("moved", watchPath)?.FolderPath);
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, "", "cancelled", false, false)
        { FailureKind = BuildTestGateFailureKind.Cancellation,
            CancellationReason = GateCancellationReasons.Shutdown },
            duringRun: () =>
            {
                Directory.Move(oldFolder, liveFolder);
                WriteTask(liveFolder, TaskStates.HumanReview);
            });
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings, preDevelopBuildGate: new PreDevelopBuildGate(gate),
            taskScanner: scanner, timeline: timeline, timeProvider: clock);

        await runner.RunAsync("Fixture", "moved", oldFolder, watchPath, "develop", CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Equal("superseded", ReadMergeStep(log, liveFolder)?.Verdict);
        Assert.Single(Directory.GetFiles(Path.Combine(liveFolder, "post-steps"),
            "pre-develop-build-gate-*.log"));
        Assert.Null(IntegrationGateJournal.Read(liveFolder));
        Assert.Equal(GateCancellationReasons.Superseded,
            Assert.Single(timeline.ReadAll(liveFolder), e => e.Kind == "gate-cancelled").Details!["reason"]);
    }

    private sealed class MergeCancellationClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
