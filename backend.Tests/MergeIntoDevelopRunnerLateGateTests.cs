using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class MergeIntoDevelopRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_during_publication_fence_never_enqueues_stale_gate_merge(bool blockRollback)
    {
        const string jobId = "late-gate";
        var fixture = SeedLateGateFixture("late-gate-publication", jobId, publishDelivery: false);
        var (repo, watchPath, folder) = (fixture.Repo, fixture.WatchPath, fixture.Folder);
        var before = RunGit(repo, "rev-parse develop").Out.Trim();
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 10, "", "green", true, false));
        var queue = new IntegrationPushQueue();
        var runner = new MergeIntoDevelopRunner(fixture.Git, fixture.Log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue, projectSettings: fixture.Settings, preDevelopBuildGate: new PreDevelopBuildGate(gate),
            taskScanner: fixture.Scanner, timeline: fixture.Timeline, timeProvider: fixture.Clock);
        var timeline = fixture.Timeline;
        var completedFolder = Path.Combine(watchPath, TaskStates.Completed, jobId);
        var lockPath = Path.Combine(repo, ".git", LaneRef("develop") + ".lock");
        var fenceCalls = 0;
        MergeIntoIntegrationResult result;
        try
        {
            result = await runner.RunAsync("Fixture", jobId, folder, watchPath, "develop",
                CancellationToken.None, publicationFence: apply =>
                {
                    fenceCalls++;
                    Directory.Move(folder, completedFolder);
                    if (blockRollback)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                        File.WriteAllText(lockPath, "locked");
                    }
                    apply();
                    return true;
                });
        }
        finally
        {
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }

        Assert.Equal(1, gate.Invocations);
        Assert.Equal(1, fenceCalls);
        Assert.False(queue.Reader.TryRead(out _));
        Assert.True(Directory.Exists(completedFolder));
        Assert.Single(timeline.ReadAll(completedFolder), item => item.Kind == "gate-result-superseded");
        if (blockRollback)
        {
            Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationError, result.FailureCode);
            Assert.Contains("manual repair", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Single(timeline.ReadAll(completedFolder), item => item.Kind == "integration-rollback-failed");
            Assert.NotEqual(before, Lane(repo, "develop"));
        }
        else
        {
            Assert.Equal(AcceptedIntegrationFailureCodes.StaleAttempt, result.FailureCode);
            Assert.Equal(before, Lane(repo, "develop"));
        }
    }

    [Fact]
    public async Task Delivery_contained_before_the_attempt_is_not_a_stale_gate_result()
    {
        // AGT-3194: containment that this attempt started from is its premise.
        // Only an integration that lands while the gate runs supersedes it.
        const string jobId = "contained-gate";
        var fixture = SeedLateGateFixture("contained-gate-publication", jobId, publishDelivery: true);
        var tip = RunGit(fixture.Repo, "rev-parse develop").Out.Trim();
        var evidence = Path.Combine(fixture.Folder, "post-steps");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "pre-develop-build-gate-1.log"),
            "verdict=Ok exit=0 durationMs=20\n" +
            $"expectedSha={tip} testedSha={tip}\n" +
            "reason=exact gate passed\n");
        var gate = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 10, "", "a contained delivery reuses its exact receipt", true, false));
        var queue = new IntegrationPushQueue();
        var runner = new MergeIntoDevelopRunner(fixture.Git, fixture.Log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue, projectSettings: fixture.Settings, preDevelopBuildGate: new PreDevelopBuildGate(gate),
            taskScanner: fixture.Scanner, timeline: fixture.Timeline, timeProvider: fixture.Clock);
        var fenceCalls = 0;

        var result = await runner.RunAsync("Fixture", jobId, fixture.Folder, fixture.WatchPath, "develop",
            CancellationToken.None, publicationFence: apply =>
            {
                fenceCalls++;
                apply();
                return true;
            });

        Assert.True(result.Outcome.IsAlreadyContained(), result.Error);
        Assert.Null(result.FailureCode);
        Assert.Equal(tip, result.ContainmentSha);
        Assert.Equal(0, gate.Invocations);
        Assert.Equal(1, fenceCalls);
        Assert.False(queue.Reader.TryRead(out _));
        Assert.DoesNotContain(fixture.Timeline.ReadAll(fixture.Folder),
            item => item.Kind == "gate-result-superseded");
    }

    private sealed record LateGateFixture(string Repo, string WatchPath, string Folder,
        TaskScannerService Scanner, GitService Git, PipelineExecutionLog Log, TimelineLog Timeline,
        ProjectSettingsService Settings, FakeTimeProvider Clock);

    private LateGateFixture SeedLateGateFixture(string name, string jobId, bool publishDelivery)
    {
        var deliveryRef = $"agent-studio/results/run-{jobId}/result";
        var (repo, _) = SeedRepoWithOrigin(name);
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, $"checkout -q -b {deliveryRef} develop");
        File.WriteAllText(Path.Combine(repo, "delivery.txt"), "reviewed delivery");
        Commit(repo, "feat: reviewed delivery");
        var deliverySha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q origin {deliveryRef}:{deliveryRef}");
        RunGit(repo, "checkout -q develop");
        if (publishDelivery)
        {
            RunGit(repo, $"merge -q --ff-only {deliverySha}");
            RunGit(repo, "push -q origin develop");
        }

        var watchPath = Path.Combine(_tempDir, name + "-jobs");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, state));
        var folder = Path.Combine(watchPath, TaskStates.AutoReview, jobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = jobId, key = "AGT-LATE-GATE", title = "Late gate", state = TaskStates.AutoReview,
            order = 1, agent = "codex", cliType = "codex", mode = TaskModes.Coding,
            projectName = "Fixture", ownerClientId = DefaultClientIdentity.Id,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Integrate delivery.\n");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["TaskRepository"] = Path.Combine(_tempDir, name + "-store"),
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var clock = new FakeTimeProvider();
        log.Begin(folder, PipelineCatalogue.Standard, "Fixture", jobId);
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-LATE-GATE", RunAttemptId = "run-late-gate", Project = "Fixture",
            Repository = repo, ResultSha = deliverySha, AttemptChainId = "attempt-late-gate",
            Executor = "agent-runner-01", LeaseId = "lease-late-gate", FencingToken = 1,
            ImmutableResultRef = deliveryRef, IntegrationBranch = "develop",
            CompletedAtUtc = clock.GetUtcNow(),
        });
        return new LateGateFixture(repo, watchPath, folder, scanner, git, log, timeline, settings, clock);
    }
}
