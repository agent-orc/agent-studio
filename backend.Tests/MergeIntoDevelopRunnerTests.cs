using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ASS-1721: the deferred, operator-triggered "Merge into Develop" post-step.
/// Drives <see cref="MergeIntoDevelopRunner"/> against a throwaway temp repo and
/// asserts it performs the real <c>task/&lt;id&gt; -&gt; develop</c> merge and
/// flips the deferred step in <c>pipeline-execution.json</c> from pending to its
/// outcome (passed / failed / skipped). A conflict is recorded as a visible
/// failure, not swallowed.
/// </summary>
public sealed class MergeIntoDevelopRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public MergeIntoDevelopRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "merge-into-develop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Run_MergesTaskBranch_AndRecordsStepPassed()
    {
        var repo = SeedRepo("runner-merge");
        // develop + task/20 with a commit the merge should fold in.
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/20");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "20");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "20", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code); // merge commit

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
    }

    [Fact]
    public async Task RunAsync_BehindCurrentTarget_DirectMergePreservesShaAndRunsGate()
    {
        var repo = SeedRepo("runner-rebase");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/20-rebase");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "develop.txt"), "integration advanced");
        Commit(repo, "chore: advance integration target");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "rebased merge gate passed",
            true,
            false));
        var jobFolder = BeginRun(log, repo, jobId: "20-rebase");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture",
            "20-rebase",
            jobFolder,
            repo,
            "develop",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Empty(outcome.RebasedCommits);
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(outcome.MergedSha, gateRunner.Request!.ExpectedSha);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
        Assert.Contains("rebased merge gate passed", step.VerdictSummary);
    }

    [Fact]
    public void Run_CurrentTargetDoesNotLetRecordedSubjectRetargetDelivery()
    {
        var repo = SeedRepo("runner-recorded-main");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q main");
        RunGit(repo, "checkout -q -b runner/agent-runner-01/AGT-2400");
        File.WriteAllText(Path.Combine(repo, "main-only.txt"), "task work");
        Commit(repo, "feat: main-line task work");
        var resultSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q main");
        RunGit(repo, "remote add origin .");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "AGT-2400");
        ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-2400",
            RunAttemptId = "run-main",
            Project = "Fixture",
            Repository = repo,
            ResultSha = resultSha,
            AttemptChainId = "attempt-main",
            Executor = "agent-runner-01",
            LeaseId = "lease-main",
            FencingToken = 1,
            ResultRef = "runner/agent-runner-01/AGT-2400",
            IntegrationBranch = "refs/heads/main",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "AGT-2400", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.NotEqual(resultSha, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {resultSha} develop").Code);
    }

    [Fact]
    public void Run_StaleRemoteSubject_DoesNotRetargetAcceptedDelivery()
    {
        var repo = SeedRepo("runner-stale-subject");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b runner/agent-runner-01/AGT-STALE");
        File.WriteAllText(Path.Combine(repo, "stale.txt"), "superseded work");
        Commit(repo, "feat: superseded remote work");
        var staleSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var authority = new AttemptAuthorityService(
            new ConfigurationBuilder().Build(),
            NullLogger<AttemptAuthorityService>.Instance);
        var staleRun = authority.AcquireRun(
            "AGT-STALE", "PROJ-FIXTURE", null,
            "agent-runner-01", "host-a", 60, "claim-stale").RunAttempt!;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                staleRun.AttemptId,
                staleRun.LastFence,
                staleRun.AuthorityEpoch,
                "settle-stale"),
            Outcome = "done",
            ResultSha = staleSha,
        });
        Assert.True(settled.Accepted);
        var currentRun = authority.AcquireRun(
            "AGT-STALE", "PROJ-FIXTURE", staleRun.AttemptId,
            "local", "host-local", 60, "claim-current").RunAttempt!;

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "AGT-STALE");
        File.WriteAllText(
            Path.Combine(jobFolder, "task.json"),
            """{"id":"AGT-STALE","key":"AGT-STALE"}""");
        ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-STALE",
            RunAttemptId = staleRun.AttemptId,
            Project = "Fixture",
            Repository = repo,
            ResultSha = staleSha,
            AttemptChainId = staleRun.Lease!.LeaseId,
            Executor = "agent-runner-01",
            LeaseId = staleRun.Lease.LeaseId,
            FencingToken = staleRun.LastFence,
            ResultRef = "runner/agent-runner-01/AGT-STALE",
            IntegrationBranch = "develop",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            attemptAuthority: authority);

        var outcome = runner.Run("Fixture", "AGT-STALE", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains(staleRun.AttemptId, outcome.Error);
        Assert.Contains(currentRun.AttemptId, outcome.Error);
        Assert.NotEqual(0, RunGit(repo, $"merge-base --is-ancestor {staleSha} develop").Code);
    }

    [Fact]
    public async Task RunAsync_MainTarget_RunsFullSuiteOnExactSourceBeforeFastForward()
    {
        var repo = SeedRepo("runner-main-full-suite");
        RunGit(repo, "checkout -q -b task/50");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "release work");
        Commit(repo, "feat: release work");
        var taskSha = RunGit(repo, "rev-parse task/50").Out.Trim();
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            TestCmds = [TagMarker("pre-main-suite-ran")],
        });
        var gateRunner = HermeticGateRunner();
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner),
            preMainTimeout: TimeSpan.FromSeconds(30));
        var jobFolder = BeginRun(log, repo, jobId: "50");

        var outcome = await runner.RunAsync(
            "Fixture",
            "50",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(taskSha, outcome.MergedSha);
        Assert.NotEqual(mainBefore, taskSha);
        Assert.Equal(taskSha, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.True(HasTag(repo, "pre-main-suite-ran"), "the declared full-suite test command must run before main advances");

        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains($"expectedSha={taskSha}", evidence);
        Assert.Contains($"testedSha={taskSha}", evidence);
        Assert.Contains("\"Level\": \"full\"", evidence);
        Assert.Contains("\"FullSuiteRequired\": true", evidence);
        Assert.Contains("\"FullSuiteRan\": true", evidence);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Contains("mandatory full suite passed", step.Reason);
        Assert.Contains("full-suite=required-and-run", step.VerdictSummary);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_MainTarget_WithDevelop_IntegratesDevelopThenFastForwardsMainAlongOneLine()
    {
        var repo = SeedRepo("runner-main-through-develop");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "develop.txt"), "work line advanced");
        Commit(repo, "chore: advance develop");
        RunGit(repo, "checkout -q -b task/50-lineage");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "release work");
        Commit(repo, "feat: release work");
        var taskSha = RunGit(repo, "rev-parse task/50-lineage").Out.Trim();
        RunGit(repo, "checkout -q main");

        var candidateWasDescendantOfMain = false;
        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(
            new BuildTestGateResult(
                BuildTestGateVerdict.Ok,
                0,
                20,
                string.Empty,
                "full suite passed",
                true,
                false)
            {
                TestSelection = new TestSelectionAudit
                {
                    Level = TestExecutionLevels.Full,
                    FullSuiteRequired = true,
                    FullSuiteRan = true,
                },
            },
            () =>
            {
                var candidate = RunGit(repo, "rev-parse develop").Out.Trim();
                candidateWasDescendantOfMain =
                    RunGit(repo, $"merge-base --is-ancestor main {candidate}").Code == 0;
            });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "50-lineage");

        var outcome = await runner.RunAsync(
            "Fixture",
            "50-lineage",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        var mainTip = RunGit(repo, "rev-parse main").Out.Trim();
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(developTip, outcome.MergedSha);
        Assert.Equal(developTip, mainTip);
        Assert.NotEqual(taskSha, developTip);
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {taskSha} develop").Code);
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {mainBefore} {developTip}").Code);
        Assert.True(candidateWasDescendantOfMain);
        Assert.Equal(developTip, gateRunner.Request!.ExpectedSha);
        Assert.Equal("develop", gateRunner.Request.SubjectRef);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_MainTarget_WithDivergedDevelop_BlocksBeforeMergingTheDelivery()
    {
        var repo = SeedRepo("runner-main-diverged-from-develop");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "develop.txt"), "develop-only work");
        Commit(repo, "chore: advance develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();
        RunGit(repo, "checkout -q -b task/diverged-lineage");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "delivery work");
        Commit(repo, "feat: delivery work");
        var taskSha = RunGit(repo, "rev-parse task/diverged-lineage").Out.Trim();
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main-only work");
        Commit(repo, "chore: advance main independently");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "full suite passed",
            true,
            false));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "diverged-lineage");

        var outcome = await runner.RunAsync(
            "Fixture",
            "diverged-lineage",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("main is not an ancestor of develop", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.NotEqual(0, RunGit(repo, $"merge-base --is-ancestor {taskSha} develop").Code);
        Assert.Equal(0, gateRunner.Invocations);
    }

    [Fact]
    public async Task RunAsync_MainTarget_DocsOnlyDelivery_SkipsFullSuiteAndRebaseRequirement()
    {
        // AGT-2417 docs rule: a delivery whose whole diff is documentation
        // integrates through the light gate - no full suite, no
        // rebased-onto-main requirement - via a conflict-checked merge.
        var repo = SeedRepo("runner-main-docs-only");
        RunGit(repo, "checkout -q -b task/docs");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "note.md"), "docs-only delivery");
        Commit(repo, "docs: research note");
        var taskSha = RunGit(repo, "rev-parse task/docs").Out.Trim();
        // main advances AFTER the branch was cut, so the delivery is NOT
        // rebased onto main - the strict path would refuse it.
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "mainline.txt"), "independent mainline work");
        Commit(repo, "feat: mainline moved on");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            TestCmds = [TagMarker("pre-main-suite-ran")],
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(HermeticGateRunner()),
            preMainTimeout: TimeSpan.FromSeconds(30));
        var jobFolder = BeginRun(log, repo, jobId: "docs");

        var outcome = await runner.RunAsync(
            "Fixture",
            "docs",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Empty(outcome.RebasedCommits);
        Assert.False(HasTag(repo, "pre-main-suite-ran"),
            "a docs-only delivery must not run the full suite");
        // The docs delivery landed on main as a merge commit.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify main^2").Code);
        Assert.Equal(taskSha, RunGit(repo, "rev-parse main^2").Out.Trim());
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {taskSha} main").Code);

        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains("verdict=Skipped", evidence);
        Assert.Contains("Docs-only delivery", evidence);
        Assert.Contains("budget=none", evidence);
        Assert.Contains("--- dependency-cache.json ---", evidence);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
    }

    [Fact]
    public async Task RunAsync_MainTarget_DocsOnlyClassification_FailsClosedForCodePaths()
    {
        // One code file in the diff keeps the delivery on the strict release
        // path: not rebased onto main -> refused, nothing merged.
        var repo = SeedRepo("runner-main-docs-and-code");
        RunGit(repo, "checkout -q -b task/mixed");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "note.md"), "docs part");
        File.WriteAllText(Path.Combine(repo, "logic.cs"), "// code part");
        Commit(repo, "feat: docs + code");
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "mainline.txt"), "independent mainline work");
        Commit(repo, "feat: mainline moved on");
        var mainSha = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            TestCmds = [TagMarker("pre-main-suite-ran")],
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(HermeticGateRunner()),
            preMainTimeout: TimeSpan.FromSeconds(30));
        var jobFolder = BeginRun(log, repo, jobId: "mixed");

        var outcome = await runner.RunAsync(
            "Fixture",
            "mixed",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("must be rebased onto", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(mainSha, RunGit(repo, "rev-parse main").Out.Trim());
    }

    [Fact]
    public async Task RunAsync_MainTarget_FetchesImmutableRemoteDeliveryWithoutTaskBranch()
    {
        var (repo, _) = SeedRepoWithOrigin("runner-main-remote-ref");
        const string deliveryRef = "agent-studio/results/run-remote-main/result";
        RunGit(repo, $"checkout -q -b {deliveryRef} main");
        File.WriteAllText(Path.Combine(repo, "remote-main.txt"), "remote release work");
        Commit(repo, "feat: remote release work");
        var resultSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q origin {deliveryRef}:{deliveryRef}");
        RunGit(repo, "checkout -q main");
        RunGit(repo, $"branch -D {deliveryRef}");
        RunGit(repo, $"update-ref -d refs/remotes/origin/{deliveryRef}");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "full suite passed",
            true,
            false)
        {
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.Full,
                FullSuiteRequired = true,
                FullSuiteRan = true,
            },
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "remote-main");
        ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-REMOTE-MAIN",
            RunAttemptId = "run-remote-main",
            Project = "Fixture",
            Repository = repo,
            ResultSha = resultSha,
            AttemptChainId = "attempt-remote-main",
            Executor = "agent-runner-01",
            LeaseId = "lease-remote-main",
            FencingToken = 1,
            ImmutableResultRef = deliveryRef,
            IntegrationBranch = "refs/heads/main",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var outcome = await runner.RunAsync(
            "Fixture",
            "remote-main",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(resultSha, outcome.MergedSha);
        Assert.Equal(resultSha, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.False(git.BranchExists(repo, WorktreeTaskLifecycle.BranchFor("remote-main")));
        Assert.Equal(resultSha, gateRunner.Request!.ExpectedSha);
    }

    [Fact]
    public async Task RunAsync_MainTarget_AlreadyOnOriginWithStaleLocalMain_SkipsGate()
    {
        var (repo, remote) = SeedRepoWithOrigin("runner-main-stale-local");
        const string deliveryRef = "agent-studio/results/run-stale-main/result";
        RunGit(repo, $"checkout -q -b {deliveryRef} main");
        File.WriteAllText(Path.Combine(repo, "already-released.txt"), "released out of band");
        Commit(repo, "feat: already released work");
        var resultSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q origin {deliveryRef}:{deliveryRef}");
        RunGit(repo, "checkout -q main");
        RunGit(repo, $"branch -D {deliveryRef}");
        var staleMain = RunGit(repo, "rev-parse main").Out.Trim();

        var integrator = Path.Combine(_tempDir, "runner-main-stale-integrator");
        RunGit(_tempDir, $"clone -q \"{remote}\" \"{integrator}\"");
        RunGit(integrator, "checkout -q main");
        RunGit(integrator, $"merge -q --ff-only origin/{deliveryRef}");
        RunGit(integrator, "push -q origin main");
        Assert.Equal(staleMain, RunGit(repo, "rev-parse origin/main").Out.Trim());

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "full suite passed",
            true,
            false));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "stale-main");
        ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-STALE-MAIN",
            RunAttemptId = "run-stale-main",
            Project = "Fixture",
            Repository = repo,
            ResultSha = resultSha,
            AttemptChainId = "attempt-stale-main",
            Executor = "agent-runner-01",
            LeaseId = "lease-stale-main",
            FencingToken = 1,
            ImmutableResultRef = deliveryRef,
            IntegrationBranch = "refs/heads/main",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var outcome = await runner.RunAsync(
            "Fixture",
            "stale-main",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(0, gateRunner.Invocations);
        Assert.Equal(resultSha, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(resultSha, RunGit(repo, "rev-parse origin/main").Out.Trim());
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal("already-merged", step!.Verdict);
    }

    [Fact]
    public async Task RunAsync_MainTarget_RedFullSuiteLeavesMainUnchanged()
    {
        var repo = SeedRepo("runner-main-red");
        RunGit(repo, "checkout -q -b task/51");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "blocked release work");
        Commit(repo, "feat: blocked release work");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            "release regression",
            "full-suite test failed; output: stderr: exact release regression",
            true,
            false)
        {
            ViolatedBudget = new BuildTestGateBudgetEvidence(
                "gate-run", 3_600_000, 3_600_041, "verification"),
            DependencyCache =
            [
                new BuildTestGateDependencyCacheEvidence(
                    ".", "hit", "lock-unchanged", "abc123", ["package-lock.json"], false),
            ],
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.Full,
                FullSuiteRequired = true,
                FullSuiteRan = true,
            },
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "51");

        var outcome = await runner.RunAsync(
            "Fixture",
            "51",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("Pre-main full suite blocked", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(TestExecutionLevels.Full, gateRunner.Request!.RequiredTestLevel);
        Assert.True(gateRunner.Request.RequireExactSubject);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Contains("output: stderr: exact release regression", step.Reason);
        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains("budget=gate-run limitMs=3600000 consumedMs=3600041 phase=verification", evidence);
        Assert.Contains("\"State\": \"hit\"", evidence);
        Assert.Contains("\"Reason\": \"lock-unchanged\"", evidence);
        Assert.Contains("\"InstallRan\": false", evidence);
    }

    [Fact]
    public async Task RunAsync_MainTarget_SourceMovesDuringSuiteLeavesMainUnchanged()
    {
        var repo = SeedRepo("runner-main-source-moved");
        RunGit(repo, "checkout -q -b task/52");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "tested release work");
        Commit(repo, "feat: tested release work");
        var testedSha = RunGit(repo, "rev-parse task/52").Out.Trim();
        File.WriteAllText(Path.Combine(repo, "late.txt"), "late untested work");
        Commit(repo, "feat: late untested work");
        RunGit(repo, "branch task/52-next");
        RunGit(repo, "checkout -q main");
        RunGit(repo, $"branch -f task/52 {testedSha}");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(
            new BuildTestGateResult(
                BuildTestGateVerdict.Ok,
                0,
                20,
                "",
                "full suite passed",
                true,
                false)
            {
                TestSelection = new TestSelectionAudit
                {
                    Level = TestExecutionLevels.Full,
                    FullSuiteRequired = true,
                    FullSuiteRan = true,
                },
            },
            () => RunGit(repo, "branch -f task/52 task/52-next"));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "52");

        var outcome = await runner.RunAsync(
            "Fixture",
            "52",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("moved after the pre-main test run", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(testedSha, gateRunner.Request!.ExpectedSha);
    }

    [Fact]
    public void Run_NoTaskBranch_RecordsStepSkipped()
    {
        var repo = SeedRepo("runner-skip");
        RunGit(repo, "checkout -q -b develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "21");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "21", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.NoTaskBranch, outcome.Outcome);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Skipped, step!.Status);
    }

    [Fact]
    public void Run_NoTaskBranchWithAttributedCommitsOnDevelop_IsIntegrated()
    {
        var repo = SeedRepo("runner-direct-delivery");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "direct.txt"), "direct delivery");
        Commit(repo, "feat: direct delivery");
        var deliveredSha = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "21-direct");
        File.WriteAllText(
            Path.Combine(jobFolder, "task.json"),
            $$"""{"commits":[{"sha":"{{deliveredSha}}","shortSha":"{{deliveredSha[..7]}}","message":"[runner-direct-delivery] feat: direct delivery","filesChanged":1,"files":["direct.txt"]}]}""");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "21-direct", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch, outcome.Outcome);
        Assert.Equal([deliveredSha], outcome.EvidenceShas);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("already-on-integration-branch", step.Verdict);
        Assert.Contains(deliveredSha[..7], step.VerdictSummary);
    }

    [Fact]
    public void Run_Conflict_RecordsStepFailed_WithConflictedFilesVisible()
    {
        var repo = SeedRepo("runner-conflict");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/22");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "task version");
        Commit(repo, "feat: task edits shared");
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "22");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "22", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, outcome.Outcome);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("agent-round-required", step.Verdict);
        // The conflicted file is surfaced in the verdict summary tooltip.
        Assert.Contains("shared.txt", step.VerdictSummary);
    }

    // ---- AGT-1999: integration-branch push to origin -----------------------

    [Fact]
    public async Task PushIntegrationBranch_PushesDevelopToOrigin_AndRecordsStepPassed()
    {
        var (repo, remote) = SeedRepoWithOrigin("push-develop");
        // develop is ahead of origin (origin has only main).
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var localHead = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();
        Assert.NotEqual(localHead, RemoteSha(remote, "develop")); // not on origin yet

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "30");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "30", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("pushed", result.Status);
        Assert.Equal(localHead, RemoteSha(remote, "develop"));

        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("pushed", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_AlreadyOnOrigin_RecordsAlreadyRemote()
    {
        var (repo, remote) = SeedRepoWithOrigin("push-noop");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        // Already push develop so a second push is a no-op ancestor short-circuit.
        RunGit(repo, "push -q origin develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "31");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "31", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("already-remote", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("already-remote", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_NoOriginRemote_RecordsSkipped()
    {
        // A local-only checkout (no origin) is a benign skip, not a failure.
        var repo = SeedRepo("push-no-remote");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "32");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "32", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("no-remote", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Skipped, step!.Status);
        Assert.Equal("no-remote", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_TransientFailure_RetriesThenRecordsEnvironmental()
    {
        // Point origin at a path that is not a repository so every push fails with
        // a generic (non-fast-forward-free) error -> classified environmental ->
        // retried per the AGT-1944 taxonomy (zero backoff here) -> recorded as a
        // visible Failed step flagged environmental, never silently dropped.
        var repo = SeedRepo("push-transient");
        RunGit(repo, $"remote add origin \"{Path.Combine(_tempDir, "does-not-exist.git")}\"");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "33");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, environmentalBackoff: _ => TimeSpan.Zero);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "33", jobFolder, repo, "develop");

        Assert.False(result.Success);
        Assert.Equal("failed", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("environmental", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_PublishesTheApprovedMergeResult_NotALaterTip()
    {
        // The deferred push runs minutes after the gate released a merge result.
        // If it published the branch TIP, a second merge that landed in that
        // window - one no gate has approved yet - would ride to origin under this
        // card's acceptance. The approved SHA is what gets published.
        var (repo, remote) = SeedRepoWithOrigin("push-approved-sha");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "gated.txt"), "gated work");
        Commit(repo, "feat: gated work");
        var approved = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();
        File.WriteAllText(Path.Combine(repo, "ungated.txt"), "not gated yet");
        Commit(repo, "feat: work that no gate has approved");
        var tip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();
        Assert.NotEqual(approved, tip);

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "34");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync(
            "Fixture", "34", jobFolder, repo, "develop", CancellationToken.None, approved);

        Assert.True(result.Success, result.Error);
        Assert.Equal("pushed", result.Status);
        Assert.Equal(approved, result.Sha);
        Assert.Equal(approved, RemoteSha(remote, "develop"));
        Assert.NotEqual(tip, RemoteSha(remote, "develop"));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task PushIntegrationBranch_MainTarget_DoesNotPublishMainWhenDevelopPushFails()
    {
        var (repo, remote) = SeedRepoWithOrigin("push-main-lineage");
        var originalMain = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "branch develop main");
        RunGit(repo, "push -q origin main:develop");

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "candidate.txt"), "local candidate");
        Commit(repo, "feat: local candidate");
        var candidate = RunGit(repo, "rev-parse develop").Out.Trim();
        RunGit(repo, "checkout -q main");
        RunGit(repo, "merge -q --ff-only develop");

        var competing = Path.Combine(_tempDir, "push-main-lineage-competing");
        RunGit(_tempDir, $"clone -q \"{remote}\" \"{competing}\"");
        RunGit(competing, "config user.email test@example.com");
        RunGit(competing, "config user.name test");
        RunGit(competing, "checkout -q -b develop origin/develop");
        File.WriteAllText(Path.Combine(competing, "competing.txt"), "remote develop moved");
        Commit(competing, "feat: competing develop change");
        RunGit(competing, "push -q origin develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "push-main-lineage");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            environmentalBackoff: _ => TimeSpan.Zero);

        var result = await runner.PushIntegrationBranchAsync(
            "Fixture",
            "push-main-lineage",
            jobFolder,
            repo,
            "main",
            approvedSha: candidate);

        Assert.False(result.Success);
        Assert.Equal("remote-rejected", result.Status);
        Assert.Equal(originalMain, RemoteSha(remote, "main"));
        Assert.NotEqual(candidate, RemoteSha(remote, "develop"));

        // AGT-2688: a genuinely diverged remote must report a distinct,
        // actionable "integration-push-blocked" terminal state - not the
        // generic "environmental" bucket a plain infra blip gets, and never
        // silent-pending that would let the accepted-integration backstop spin.
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("push-blocked", step.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPushBlocked, step.FailureCode);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task PushIntegrationBranch_MainLineageBlocked_StillPublishesDevelopToOrigin()
    {
        // AGT-2688 root cause: main not being an ancestor of develop (a purely
        // local divergence with no bearing on develop's own remote reachability)
        // must never starve origin/develop of a delivery that is perfectly safe
        // to fast-forward. Before the fix, PushIntegrationBranchAsync checked
        // the main/develop lineage BEFORE attempting the develop push and
        // returned "lineage-blocked" without ever touching origin - exactly the
        // overnight 705-claims/0-accepts failure mode.
        var (repo, remote) = SeedRepoWithOrigin("push-lineage-blocked-develop-ok");

        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var developSha = RunGit(repo, "rev-parse develop").Out.Trim();

        // Advance local main with a commit develop never carries, so main is
        // NOT an ancestor of develop - the lineage guard blocks the main leg.
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "main-only.txt"), "main-only change");
        Commit(repo, "feat: main-only change");
        RunGit(repo, "checkout -q develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "push-lineage-blocked-develop-ok");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            environmentalBackoff: _ => TimeSpan.Zero);

        var result = await runner.PushIntegrationBranchAsync(
            "Fixture",
            "push-lineage-blocked-develop-ok",
            jobFolder,
            repo,
            "main",
            approvedSha: developSha);

        Assert.False(result.Success);
        Assert.Equal("lineage-blocked", result.Status);

        // The load-bearing assertion: develop reached origin despite main
        // being blocked. Acceptance reads origin/develop, so the delivery is
        // no longer starved.
        Assert.Equal(developSha, RemoteSha(remote, "develop"));
        Assert.NotEqual(developSha, RemoteSha(remote, "main"));

        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("lineage-blocked", step.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPushBlocked, step.FailureCode);
    }

    [Fact]
    public async Task PushIntegrationBranch_ApprovedShaOutsideTheBranch_FailsClosed()
    {
        // Publishing an object the integration branch does not contain would
        // advance origin to something local develop never carried (for example
        // after a gate rollback). Refuse instead, visibly.
        var (repo, remote) = SeedRepoWithOrigin("push-approved-foreign");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        RunGit(repo, "checkout -q -b side");
        File.WriteAllText(Path.Combine(repo, "side.txt"), "side work");
        Commit(repo, "feat: side work");
        var foreign = RunGit(repo, "rev-parse refs/heads/side").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "35");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync(
            "Fixture", "35", jobFolder, repo, "develop", CancellationToken.None, foreign);

        Assert.False(result.Success);
        Assert.Equal("sha-not-on-branch", result.Status);
        Assert.Equal(string.Empty, RemoteSha(remote, "develop"));
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
    }

    [Fact]
    public void Run_MergedAndPushEnabled_EnqueuesDevelopPush_OffTheRequestPath()
    {
        // The merge runs synchronously on the accept trigger; the origin push is
        // handed off to the queue (the same offload shape as the completed-job
        // workspace push) so the transition never awaits the network. This is the
        // request-path guard: Run returns after an instant channel write.
        var repo = SeedRepo("run-enqueue");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/40");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "40");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, pushQueue: queue, projectSettings: settings);

        var outcome = runner.Run("Fixture", "40", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.True(queue.Reader.TryRead(out var queued), "a develop push must be enqueued after a successful merge");
        Assert.Equal("40", queued!.JobId);
        Assert.Equal("develop", queued.IntegrationBranch);
        // The object the push may publish is pinned at release time, not read
        // from the branch when the queued item finally runs.
        Assert.Equal(outcome.MergedSha, queued.ApprovedSha);
    }

    [Fact]
    public void Run_AlreadyMerged_EnqueuesTheTipAtReleaseTime()
    {
        // AlreadyMerged produces no commit of its own, so the released object is
        // the integration tip as it stands at acceptance - still pinned here, not
        // re-read later by the worker.
        var repo = SeedRepo("run-already-merged");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/44");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"chore: pre-merged\" task/44");
        var tip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "44");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, pushQueue: queue, projectSettings: settings);

        var outcome = runner.Run("Fixture", "44", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal(tip, queued!.ApprovedSha);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_DevelopTarget_AlreadyMergedWithExactGreenGateReceipt_ReusesReceipt()
    {
        var repo = SeedRepo("run-already-merged-gated");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/45");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"chore: pre-merged\" task/45");
        var tip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            string.Empty,
            "a matching durable receipt must prevent this rerun",
            true,
            false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "45");
        var evidenceDir = Path.Combine(jobFolder, "post-steps");
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(
            Path.Combine(evidenceDir, "pre-develop-build-gate-1.log"),
            $"verdict=Ok exit=0 durationMs=20\n" +
            $"expectedSha={tip} testedSha={tip}\n" +
            "reason=original exact gate passed\n");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture", "45", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(tip, outcome.MergedSha);
        Assert.Equal(0, gateRunner.Invocations);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal(tip, queued!.ApprovedSha);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("already-merged", step.Verdict);
        Assert.Contains("Exact build-gate verdict Ok", step.Reason);
        Assert.Equal("original exact gate passed", step.VerdictSummary);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_DevelopTarget_AlreadyMergedWithoutReceipt_RedGateNeverPassesOrEnqueues()
    {
        var repo = SeedRepo("run-already-merged-red-gate");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/46");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"chore: merge before crash\" task/46");
        var tip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            "compile error",
            "recovery gate failed",
            true,
            false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "46");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture", "46", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(tip, gateRunner.Request!.ExpectedSha);
        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, outcome.Outcome);
        Assert.Equal(tip, RunGit(repo, "rev-parse refs/heads/develop").Out.Trim());
        Assert.False(queue.Reader.TryRead(out _));

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("gate-failed", step.Verdict);
        Assert.Contains("no push was released", step.Reason);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_DevelopTarget_AlreadyMergedWithDifferentShaReceipt_RerunsExactGate()
    {
        var repo = SeedRepo("run-already-merged-wrong-receipt");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/47");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        var deliveryTip = RunGit(repo, "rev-parse refs/heads/task/47").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"chore: merge before crash\" task/47");
        var integrationTip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok,
            0,
            20,
            string.Empty,
            "exact recovery gate passed",
            true,
            false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "47");
        var evidenceDir = Path.Combine(jobFolder, "post-steps");
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(
            Path.Combine(evidenceDir, "pre-develop-build-gate-1.log"),
            $"verdict=Ok exit=0 durationMs=20\n" +
            $"expectedSha={deliveryTip} testedSha={deliveryTip}\n" +
            "reason=gate covered only the delivery, not the merge result\n");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture", "47", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(integrationTip, gateRunner.Request!.ExpectedSha);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal(integrationTip, queued!.ApprovedSha);
        Assert.Equal(
            2,
            Directory.GetFiles(evidenceDir, "pre-develop-build-gate-*.log").Length);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunAsync_DevelopTarget_AlreadyMergedWithApplicableButUnwiredGate_FailsClosed()
    {
        var repo = SeedRepo("run-already-merged-unwired-gate");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/48");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"chore: merge before crash\" task/48");
        var tip = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "48");
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: null);

        var outcome = await runner.RunAsync(
            "Fixture", "48", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, outcome.Outcome);
        Assert.Equal(tip, RunGit(repo, "rev-parse refs/heads/develop").Out.Trim());
        Assert.False(queue.Reader.TryRead(out _));

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("gate-failed", step.Verdict);
        Assert.Contains("not available", step.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_MergedButPushDisabled_DoesNotEnqueue()
    {
        var repo = SeedRepo("run-disabled");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/41");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetPipelineStep("Fixture", PipelineCatalogue.MergeIntoDevelopPushStepId, new PipelineStepSetting { Enabled = false });
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "41");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, pushQueue: queue, projectSettings: settings);

        var outcome = runner.Run("Fixture", "41", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.False(queue.Reader.TryRead(out _), "no push must be enqueued when the push step is disabled");
    }

    [Fact]
    public async Task IntegrationPushWorker_DrainsQueue_AndPushesDevelop()
    {
        var (repo, remote) = SeedRepoWithOrigin("worker-push");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var localHead = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "42");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var queue = new IntegrationPushQueue();
        var worker = new IntegrationPushWorker(queue, runner, NullLogger<IntegrationPushWorker>.Instance);

        queue.Enqueue(new IntegrationPushRequest("Fixture", "42", jobFolder, repo, "develop"));
        Assert.True(queue.Reader.TryRead(out var request));
        await worker.ProcessAsync(request!, CancellationToken.None);

        Assert.Equal(localHead, RemoteSha(remote, "develop"));
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
    }

    // ---- Pre-develop build gate: the merge RESULT must compile ---------------

    [Fact]
    public async Task RunAsync_DevelopTarget_RedBuildGate_RollsBackAndBlocksThePush()
    {
        var repo = SeedRepo("develop-gate-red");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/60");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 20, "CS0103: the merge does not compile",
            "backend build exit 1", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "60");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));

        var outcome = await runner.RunAsync(
            "Fixture", "60", jobFolder, repo, "develop", CancellationToken.None);

        // The gate saw the exact merge commit, not the delivery and not a branch name.
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(TestExecutionLevels.BuildOnly, gateRunner.Request!.RequiredTestLevel);
        Assert.True(gateRunner.Request.RequireExactSubject);
        Assert.NotEqual(developBefore, gateRunner.Request.ExpectedSha);

        // Red gate: develop is back on its exact pre-merge tip, nothing pushed.
        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, outcome.Outcome);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(string.Empty, RunGit(repo, "status --porcelain").Out.Trim());
        Assert.False(queue.Reader.TryRead(out _), "a gate-blocked merge must never enqueue a push");

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("gate-failed", step.Verdict);
        Assert.Contains("backend build exit 1", step.Reason);
        Assert.Contains("rolled back", step.Reason);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_GreenBuildGate_KeepsTheMergeAndEnqueuesThePush()
    {
        var repo = SeedRepo("develop-gate-green");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/61");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 20, "", "verify gate passed (build-profile)", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "61");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));

        var outcome = await runner.RunAsync(
            "Fixture", "61", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        var developAfter = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.NotEqual(developBefore, developAfter);
        Assert.Equal(developAfter, outcome.MergedSha);
        Assert.Equal(developAfter, gateRunner.Request!.ExpectedSha);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code); // merge commit stands
        Assert.True(queue.Reader.TryRead(out var queued), "a green gate merges and pushes as before");
        Assert.Equal("develop", queued!.IntegrationBranch);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
        Assert.Contains("build gate passed", step.Reason);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_FrontendDiffRunsWorkPackageAgainstExactChangedFiles()
    {
        var repo = SeedRepo("develop-gate-frontend-work-package");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/61-front");
        var component = Path.Combine(
            repo, "frontend", "src", "app", "features", "project-detail", "components",
            "project-git-panel", "project-git-panel.component.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(component)!);
        File.WriteAllText(component, "export const changed = true;");
        Commit(repo, "feat: change frontend component");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 20, "", "verify gate passed (build-profile)", true, true));
        var jobFolder = BeginRun(log, repo, jobId: "61-front");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));

        var outcome = await runner.RunAsync(
            "Fixture", "61-front", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(TestExecutionLevels.WorkPackage, gateRunner.Request!.RequiredTestLevel);
        Assert.Equal(
            ["frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.ts"],
            gateRunner.ChangedFiles);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_BuildGateRunsBuildCommandsWithoutTheSuite()
    {
        // Build-only stage against the REAL gate runner: the declared build
        // command runs on the merge result, the declared test command does not.
        var repo = SeedRepo("develop-gate-build-only");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/62");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            BuildCmds = [TagMarker("gate-build-ran")],
            TestCmds = [TagMarker("gate-test-ran")],
        });
        var jobFolder = BeginRun(log, repo, jobId: "62");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(
                HermeticGateRunner()),
            preDevelopTimeout: TimeSpan.FromSeconds(60));

        var outcome = await runner.RunAsync(
            "Fixture", "62", jobFolder, repo, "develop", CancellationToken.None);

        Assert.True(
            outcome.Outcome == MergeIntoIntegrationOutcome.Merged,
            outcome.Error ?? outcome.Outcome.ToString());
        Assert.True(HasTag(repo, "gate-build-ran"), "the declared build command must run on the merge result");
        Assert.False(HasTag(repo, "gate-test-ran"), "the build-only stage must not run the test suite again");

        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-develop-build-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains($"expectedSha={outcome.MergedSha}", evidence);
        Assert.Contains($"testedSha={outcome.MergedSha}", evidence);
        Assert.Contains("\"Level\": \"build-only\"", evidence);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_WithoutBuildProfile_MergesUngated()
    {
        var repo = SeedRepo("develop-gate-absent");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/63");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 20, "", "must never be consulted", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "63");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture", "63", jobFolder, repo, "develop", CancellationToken.None);

        // Convention instead of a settings switch: no build profile, no gate.
        Assert.Equal(0, gateRunner.Invocations);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code);
        Assert.True(queue.Reader.TryRead(out _));

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
        Assert.DoesNotContain("build gate", step.Reason ?? string.Empty);
    }

    [Fact]
    public void Run_LocalDelivery_DivergedIntegrationBranch_ReportsHealingErrorInsteadOfMergingStale()
    {
        // Local develop and origin/develop both moved on from main: a real
        // divergence. The local task-branch path used to merge onto the stale
        // local tip and report success; it must now say so and merge nothing.
        var (repo, _) = SeedRepoWithOrigin("develop-diverged");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "local.txt"), "local develop work");
        Commit(repo, "chore: local develop work");
        RunGit(repo, "push -q -u origin develop");

        // Rewrite origin/develop onto an unrelated commit -> histories diverge.
        RunGit(repo, "checkout -q -b origin-side main");
        File.WriteAllText(Path.Combine(repo, "remote.txt"), "remote develop work");
        Commit(repo, "chore: remote develop work");
        RunGit(repo, "push -q -f origin origin-side:develop");

        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/64");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "64");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var outcome = runner.Run("Fixture", "64", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains(
            "Integration branch 'develop' diverged from origin - heal or recreate it via project settings before accepting deliveries.",
            outcome.Error);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("error", step.Verdict);
    }

    /// <summary>
    /// A verify command that leaves a durable, checkable trace and exits zero on
    /// every platform: it tags the commit the gate actually checked out. Git tags
    /// are written to the shared repository, so the test can read them from the
    /// main checkout after the gate's isolated worktree is gone. Explicit profile
    /// commands run through the same <c>bash -lc</c> contract as profile validation.
    /// </summary>
    private static string TagMarker(string tag) => $"git tag {tag}";

    private static BuildTestGateRunner HermeticGateRunner()
        => new(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest);

    private static bool HasTag(string repo, string tag)
        => RunGit(repo, "tag --list " + tag).Out.Trim().Length > 0;

    private static PipelineStepExecution? ReadPushStep(PipelineExecutionLog log, string jobFolder)
    {
        var record = log.Read(jobFolder);
        return record?.Steps.FirstOrDefault(s => s.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
    }

    private (GitService Git, PipelineExecutionLog Log, ProjectSettingsService Settings) BuildWithSettings(string repo)
    {
        var (git, log) = Build(repo);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                // Keep project-settings.json inside the throwaway temp dir.
                ["TaskRepository"] = Path.Combine(_tempDir, "settings-store"),
            })
            .Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return (git, log, settings);
    }

    private (string Repo, string Remote) SeedRepoWithOrigin(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        var remote = Path.Combine(_tempDir, name + "-origin.git");
        Directory.CreateDirectory(repo);
        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{remote}\"");
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, $"remote add origin \"{remote}\"");
        RunGit(repo, "push -q -u origin main");
        return (repo, remote);
    }

    private static string RemoteSha(string remote, string branch)
    {
        // The remote is a bare repo; relax safe.bareRepository per-invocation so
        // the ref read works on hosts where it is set to "explicit".
        var (o, _, code) = RunGit(remote, $"-c safe.bareRepository=all rev-parse refs/heads/{branch}");
        return code == 0 ? o.Trim() : string.Empty;
    }

    private string BeginRun(PipelineExecutionLog log, string repo, string jobId)
    {
        // The job folder lives OUTSIDE the repo working tree (as in production,
        // where the tasks workspace is separate from the code checkout); writing
        // pipeline-execution.json inside the repo would otherwise make the tree
        // look dirty and the merge precondition would refuse.
        var jobFolder = Path.Combine(_tempDir, "jobs", jobId);
        Directory.CreateDirectory(jobFolder);
        // Pre-populate the run so the deferred merge step sits in it as pending,
        // exactly as it would after a real run recorded the pipeline.
        log.Begin(jobFolder, PipelineCatalogue.Standard, "Fixture", jobId);
        return jobFolder;
    }

    private static PipelineStepExecution? ReadMergeStep(PipelineExecutionLog log, string jobFolder)
    {
        var record = log.Read(jobFolder);
        return record?.Steps.FirstOrDefault(s => s.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
    }

    private (GitService Git, PipelineExecutionLog Log) Build(string repo)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        return (git, log);
    }

    private sealed class CapturingBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly BuildTestGateResult _result;
        private readonly Action? _duringRun;

        public CapturingBuildTestGateRunner(
            BuildTestGateResult result,
            Action? duringRun = null)
        {
            _result = result;
            _duringRun = duringRun;
        }

        public int Invocations { get; private set; }
        public BuildTestGateRequest? Request { get; private set; }
        public IReadOnlyList<string>? ChangedFiles { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            Request = request;
            ChangedFiles = changedFiles;
            _duringRun?.Invoke();
            return Task.FromResult(_result with
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            });
        }
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
