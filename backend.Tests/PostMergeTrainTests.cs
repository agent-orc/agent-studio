using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-4020: in strategy fast a reviewed card integrates behind a compile of the
/// merged tree only; the cards waiting for integration compile once, together,
/// and a red combined compile is searched one card at a time.
/// </summary>
public sealed class PostMergeTrainTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 15, 41, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-train-" + Guid.NewGuid().ToString("N"));

    public PostMergeTrainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SixReviewedFastDeliveriesIntegrateWithoutStartingATestCommand()
    {
        var fixture = CreateFixture();
        var jobs = Enumerable.Range(1, 6).Select(index => $"AGT-90{index:00}").ToArray();
        foreach (var job in jobs) fixture.Deliver(job, $"src/{job}.txt");
        var windowFrom = fixture.CompleteWindowRun();

        var plan = await fixture.Runner.PlanPostMergeTrainAsync("Fixture", fixture.Repo, "develop",
            jobs.Select(job => new PostMergeTrainRequest(job, fixture.JobFolder(job), null)).ToArray(),
            CancellationToken.None);

        Assert.NotNull(plan);
        Assert.Single(plan!.Compiles);
        Assert.All(plan.Entries, entry => Assert.Equal(PostMergeTrainStatuses.Covered, entry.Status));
        foreach (var job in jobs)
        {
            var outcome = await fixture.Runner.RunAsync("Fixture", job, fixture.JobFolder(job), fixture.Repo,
                "develop", CancellationToken.None);
            Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
            Assert.True(outcome.GateVerdictReused);
        }

        // One gate run for six cards, and it ran no test command.
        var request = Assert.Single(fixture.Gate.Requests);
        Assert.Equal(TestExecutionLevels.CompileOnly, request.RequiredTestLevel);
        Assert.Equal(plan.Compiles[0].Sha, request.ExpectedSha);

        // Each card has its own merge commit on the lane, in queue order.
        var lane = GitService.IntegrationLaneRef("develop");
        var firstParent = Git(fixture.Repo, "rev-list", "--first-parent", "--reverse", $"{windowFrom}..{lane}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(jobs.Length, firstParent.Length);
        for (var index = 0; index < jobs.Length; index++)
            Assert.Equal(fixture.Tip($"task/{jobs[index]}"), Git(fixture.Repo, "rev-parse", firstParent[index] + "^2").Trim());

        // Every card names the merge-window run that tests it after integration.
        foreach (var job in jobs)
        {
            var coverage = PostMergeWindowCoverage.Read(fixture.JobFolder(job));
            Assert.NotNull(coverage);
            Assert.Equal(windowFrom, coverage!.WindowFromSha);
            Assert.Equal(TestExecutionLevels.CompileOnly, coverage.PreMergeLevel);
            Assert.Equal(PostMergeWindowSuites.Affected, coverage.WindowSuites);
            Assert.Equal(plan.TrainId, coverage.TrainId);
        }
        var laneTip = fixture.Tip(lane);
        var state = fixture.CompleteWindowRun(laneTip, "window-after-train");
        foreach (var job in jobs)
        {
            var covering = PostMergeWindowCoverage.Read(fixture.JobFolder(job))!.CoveringRun(state,
                (merged, tip) => fixture.IsAncestor(merged, tip));
            Assert.Equal("window-after-train", covering?.Id);
        }
    }

    [Fact]
    public async Task ACardWhoseMergeDiffersFromTheTrainCompilesOnItsOwnAtCompileOnly()
    {
        var fixture = CreateFixture();
        fixture.Deliver("AGT-9101", "src/a.txt");
        fixture.Deliver("AGT-9102", "src/b.txt");
        await fixture.Runner.PlanPostMergeTrainAsync("Fixture", fixture.Repo, "develop",
            [new("AGT-9101", fixture.JobFolder("AGT-9101"), null), new("AGT-9102", fixture.JobFolder("AGT-9102"), null)],
            CancellationToken.None);
        // develop moves before the train lands: the planned trees no longer match.
        fixture.AdvanceDevelop("src/other.txt");

        foreach (var job in new[] { "AGT-9101", "AGT-9102" })
        {
            var outcome = await fixture.Runner.RunAsync("Fixture", job, fixture.JobFolder(job), fixture.Repo,
                "develop", CancellationToken.None);
            Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        }

        Assert.Equal(3, fixture.Gate.Requests.Count);
        Assert.All(fixture.Gate.Requests, request =>
            Assert.Equal(TestExecutionLevels.CompileOnly, request.RequiredTestLevel));
    }

    [Fact]
    public async Task ARedCombinedCompileFindsTheCardByAddingThemOneAtATime()
    {
        var members = new[] { "A", "B", "C", "D", "E" }
            .Select(id => new PostMergeTrainMember(id, "/jobs/" + id, new string(char.ToLowerInvariant(id[0]), 40)))
            .ToArray();
        var compiled = new List<IReadOnlyList<string>>();
        var plan = await PostMergeTrainSearch.RunAsync("train-1", "base", members,
            (tip, member) => ($"{tip}+{member.JobId}", $"tree:{tip}+{member.JobId}", false, null),
            (sha, ids) =>
            {
                compiled.Add(ids);
                return Task.FromResult(ids.Contains("C") ? (false, "CS0103 in C") : (true, "build passed"));
            },
            Now);

        Assert.Equal(
            ["A,B,C,D,E", "A", "A,B", "A,B,C", "A,B,D", "A,B,D,E"],
            compiled.Select(ids => string.Join(",", ids)).ToArray());
        Assert.Equal(
            [PostMergeTrainStatuses.Covered, PostMergeTrainStatuses.Covered, PostMergeTrainStatuses.CompileRed,
             PostMergeTrainStatuses.Covered, PostMergeTrainStatuses.Covered],
            plan.Entries.Select(entry => entry.Status).ToArray());
        var culprit = plan.Entries.Single(entry => entry.JobId == "C");
        Assert.Contains("CS0103 in C", culprit.Reason);
        Assert.Null(culprit.CompiledSha);
        // D and E are planned on the tree without C, which is how they will merge.
        Assert.Equal("tree:base+A+B+D", plan.Entries.Single(entry => entry.JobId == "D").MergedTreeSha);
        Assert.Equal("base+A+B+D+E", plan.Entries.Single(entry => entry.JobId == "E").CompiledSha);
    }

    [Fact]
    public async Task AConflictingCardLeavesTheTrainAndTheRestStayInOrder()
    {
        var members = new[] { "A", "B", "C" }
            .Select(id => new PostMergeTrainMember(id, "/jobs/" + id, new string('a', 40))).ToArray();
        var plan = await PostMergeTrainSearch.RunAsync("train-2", "base", members,
            (tip, member) => member.JobId == "B"
                ? (null, null, true, "conflict")
                : ($"{tip}+{member.JobId}", $"tree:{tip}+{member.JobId}", false, null),
            (_, _) => Task.FromResult((true, "ok")),
            Now);

        Assert.Single(plan.Compiles);
        Assert.Equal(["A", "C"], plan.Compiles[0].JobIds);
        Assert.Equal(PostMergeTrainStatuses.Conflict, plan.Entries[1].Status);
        Assert.Equal("tree:base+A+C", plan.Entries[2].MergedTreeSha);
    }

    [Theory]
    [InlineData(TestingStrategies.Fast, TestExecutionLevels.CompileOnly)]
    [InlineData(TestingStrategies.Balanced, TestExecutionLevels.WorkPackage)]
    public void FastNeverRunsATestProjectBeforeTheMergeBalancedKeepsTheOverlapRule(string strategy, string expected)
    {
        var review = new ReviewVerificationRecord
        {
            Outcome = "Pass",
            BuildTestGate = ReviewBuildTestGateClasses.Passed,
            IntegrationTipSha = new string('a', 40),
        };
        // develop moved over a path of the delivery, and the merge was not replayed.
        Assert.Equal(expected, PostMergeIntegrationPolicy.ForStrategy(strategy, review, new string('b', 40),
            ["backend/Shared.cs"], ["backend/Shared.cs"], replayedOrResolved: false));
        // Missing review evidence or a replayed merge is no reason for a test run in fast either.
        Assert.Equal(strategy == TestingStrategies.Fast ? TestExecutionLevels.CompileOnly : TestExecutionLevels.WorkPackage,
            PostMergeIntegrationPolicy.ForStrategy(strategy, null, null, null, null, replayedOrResolved: true));
        Assert.Equal(strategy != TestingStrategies.Fast, PostMergeIntegrationPolicy.NeedsPathEvidence(strategy));
    }

    private Fixture CreateFixture()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "maintenance.auto", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-qm", "seed");
        Git(repo, "checkout", "-qb", "develop");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
            ["TaskRepository"] = Path.Combine(_root, "store"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var clock = new FakeTimeProvider(Now);
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(Path.Combine(_root, "store", "Fixture"), "Fixture",
            DefaultWorkspace.Id, clock);
        var runs = new PostMergeRunStore(Path.Combine(_root, "post-merge"));
        var mode = new PostMergeModeService(projects, runs);
        var gate = new RecordingGate();
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings, preDevelopBuildGate: new PreDevelopBuildGate(gate), postMergeMode: mode);
        return new Fixture(repo, Path.Combine(_root, "jobs"), log, runner, gate, runs, project.Id);
    }

    private sealed record Fixture(string Repo, string Jobs, PipelineExecutionLog Log, MergeIntoDevelopRunner Runner,
        RecordingGate Gate, PostMergeRunStore Runs, string ProjectId)
    {
        public string JobFolder(string job) => Path.Combine(Jobs, job);

        public void Deliver(string job, string path)
        {
            Git(Repo, "checkout", "-qb", $"task/{job}", "develop");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo, path))!);
            File.WriteAllText(Path.Combine(Repo, path), job + "\n");
            Git(Repo, "add", "-A");
            Git(Repo, "commit", "-qm", "feat: " + job);
            Git(Repo, "checkout", "-q", "develop");
            Directory.CreateDirectory(JobFolder(job));
            Log.Begin(JobFolder(job), PipelineCatalogue.Standard, "Fixture", job);
        }

        public void AdvanceDevelop(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo, path))!);
            File.WriteAllText(Path.Combine(Repo, path), "moved\n");
            Git(Repo, "add", "-A");
            Git(Repo, "commit", "-qm", "chore: develop moved");
        }

        public string Tip(string reference) => Git(Repo, "rev-parse", reference).Trim();

        public bool IsAncestor(string ancestor, string tip)
            => Run(Repo, "merge-base", "--is-ancestor", ancestor, tip).Code == 0;

        /// <summary>Records a green merge-window run on <paramref name="tip"/> (develop when null).</summary>
        public string CompleteWindowRun() { CompleteWindowRun(Tip("develop"), "window-before-train"); return Tip("develop"); }

        public PostMergeProjectState CompleteWindowRun(string tip, string id)
        {
            var started = Now.AddMinutes(Runs.Read(ProjectId).Runs.Count);
            var running = new PostMergeRunRecord(id, "Fixture", "window", tip, null, started, started, 1,
                ["dotnet test"], [], "running", started, null);
            Assert.NotNull(Runs.Record(ProjectId, running));
            return Runs.Record(ProjectId, running with
            {
                Verdicts = [new PostMergeCommandVerdict("dotnet test", 0, false)],
                Status = "green",
                FinishedAtUtc = started.AddMinutes(5),
            })!;
        }
    }

    private sealed class RecordingGate : IBuildTestGateRunner
    {
        public List<BuildTestGateRequest> Requests { get; } = [];

        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request, IReadOnlyList<string>? changedFiles,
            BuildProfile? profile, PostStepMode mode, TimeSpan timeout, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 10, string.Empty,
                "compile passed", true, false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            });
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var result = Run(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Err}");
        return result.Out;
    }

    private static (string Out, string Err, int Code) Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output, error, process.ExitCode);
    }
}
