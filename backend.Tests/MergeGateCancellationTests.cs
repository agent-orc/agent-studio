using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
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
        Assert.Null(IntegrationGateJournal.Read(folder));
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
    public async Task Merge_gate_lane_move_records_cancellation_in_live_folder()
    {
        var repo = SeedRepo("merge-cancel-lane-move");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/lane-move");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var (_, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var watchPath = Path.Combine(_tempDir, "jobs");
        var oldFolder = Path.Combine(watchPath, TaskStates.Completed, "lane-move");
        var liveFolder = Path.Combine(watchPath, TaskStates.AutoReview, "lane-move");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFolder)!);
        Directory.CreateDirectory(Path.GetDirectoryName(liveFolder)!);
        Directory.Move(BeginRun(log, repo, "lane-move"), oldFolder);
        void WriteTask(string folder, string state) => File.WriteAllText(Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new { id = "lane-move", key = "lane-move", title = "Lane move", state, order = 1 }));
        WriteTask(oldFolder, TaskStates.Completed);
        var clock = new MergeCancellationClock();
        ReviewSubjectStore.Write(oldFolder, new ReviewSubjectRecord
        {
            ResultSha = RunGit(repo, "rev-parse task/lane-move").Out.Trim(),
            AttemptChainId = "old", RunAttemptId = "old", Executor = "",
            ResultRef = "task/lane-move", Project = "Fixture", TaskKey = "lane-move",
            Repository = repo, CompletedAtUtc = clock.GetUtcNow(),
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture", ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repo, ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        Assert.Equal(oldFolder, scanner.FindJob("lane-move", watchPath)?.FolderPath);
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, null, 0, "", "cancelled", false, false)
            { FailureKind = BuildTestGateFailureKind.Cancellation },
            duringRun: () =>
            {
                Directory.Move(oldFolder, liveFolder);
                WriteTask(liveFolder, TaskStates.AutoReview);
                var newer = log.Begin(liveFolder, PipelineCatalogue.Standard, "Fixture", "lane-move",
                    clock.GetUtcNow().UtcDateTime);
                using var attempt = log.EnterAttempt(liveFolder, newer.Attempt);
                log.RecordStep(liveFolder, new PipelineStepExecution
                {
                    StepId = PipelineCatalogue.MergeIntoDevelopStepId,
                    Kind = StepKind.Tool,
                    Status = PipelineStepStatus.Passed,
                    StartedAt = clock.GetUtcNow().UtcDateTime,
                    CompletedAt = clock.GetUtcNow().UtcDateTime,
                    Verdict = "newer-attempt",
                });
                IntegrationGateJournal.Open(liveFolder, new IntegrationGateJournalEntry
                {
                    Owner = "newer-attempt", Project = "Fixture", JobId = "lane-move", RepoRoot = repo,
                    StartedAt = clock.GetUtcNow(),
                });
            });
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings, preDevelopBuildGate: new PreDevelopBuildGate(gate),
            taskScanner: scanner, timeline: timeline, timeProvider: clock);

        await runner.RunAsync("Fixture", "lane-move", oldFolder, watchPath, "develop", CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Equal("newer-attempt", ReadMergeStep(log, liveFolder)?.Verdict);
        Assert.Equal(PipelineStepStatus.Passed, ReadMergeStep(log, liveFolder)?.Status);
        Assert.Equal(2, log.Read(liveFolder)?.Attempt);
        Assert.Single(Directory.GetFiles(Path.Combine(liveFolder, "post-steps"), "pre-develop-build-gate-*.log"));
        Assert.Contains("Gate cancelled: superseded.",
            File.ReadAllText(Directory.GetFiles(Path.Combine(liveFolder, "post-steps"),
                "pre-develop-build-gate-*.log").Single()));
        Assert.Equal("superseded", Assert.Single(timeline.ReadAll(liveFolder), e => e.Kind == "gate-cancelled").Details!["reason"]);
        Assert.Equal("newer-attempt", IntegrationGateJournal.Read(liveFolder)?.Owner);
    }

    private sealed class MergeCancellationClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
