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

    private sealed class MergeCancellationClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
