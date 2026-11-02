using Microsoft.Extensions.Logging.Abstractions;

// clock-independent: the review-subject CompletedAtUtc is stored metadata, never compared with the clock.

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3435: a delivery behind <c>main</c> is merged, the mandatory full suite
/// runs on the merge result, and only that tested merge is published. On
/// 2 November 2026 Token Economy escalated TE-72, TE-74, TE-76 and TE-77 with
/// <c>source-needs-rebase</c> although every delivery merged cleanly.
/// </summary>
public sealed partial class MergeIntoDevelopRunnerTests
{
    [Fact]
    public async Task RunAsync_MainTarget_MergeRound_GatesAndPublishesMappedNetChange()
    {
        var (repo, remote) = SeedRepoWithOrigin("release-merge-round");
        const string deliveryRef = "agent-studio/results/run-merge-round/result";
        RunGit(repo, "checkout -q -b task/merge-round");
        File.WriteAllText(Path.Combine(repo, "delivery.txt"), "delivery work");
        Commit(repo, "feat: delivery work");
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "earlier.txt"), "earlier main work");
        Commit(repo, "feat: earlier main work");
        RunGit(repo, "push -q origin main");
        RunGit(repo, "checkout -q task/merge-round");
        Assert.Equal(0, RunGit(repo, "merge -q --no-ff main -m \"Merge main into delivery\"").Code);
        var resultSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q origin HEAD:refs/heads/{deliveryRef}");
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "later.txt"), "later main work");
        Commit(repo, "feat: later main work");
        RunGit(repo, "push -q origin main");
        var before = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(FullSuite(BuildTestGateVerdict.Ok));
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, "merge-round");
        WriteRemoteSubject(jobFolder, repo, "AGT-MERGE-ROUND", "run-merge-round", resultSha, deliveryRef);

        var outcome = await runner.RunAsync("Fixture", "merge-round", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.MergedAfterRebase, outcome.Outcome);
        var published = Assert.IsType<string>(outcome.MergedSha);
        Assert.Equal(before, RunGit(repo, $"rev-parse {published}^1").Out.Trim());
        Assert.Contains(outcome.RebasedCommits, item => item.OriginalSha == resultSha && item.RebasedSha == published);
        Assert.Equal(published, gateRunner.Request!.ExpectedSha);
        Assert.Equal(TestExecutionLevels.Full, gateRunner.Request.RequiredTestLevel);
        Assert.Equal(published, IntegrationVerificationStore.Read(jobFolder)!.Sha);

        var pushed = await runner.PushIntegrationBranchAsync(
            "Fixture", "merge-round", jobFolder, repo, "main", CancellationToken.None, approvedSha: published);
        Assert.True(pushed.Success, pushed.Error);
        Assert.Equal(published, RemoteSha(remote, "main"));
    }

    [Fact]
    public async Task RunAsync_MainTarget_DeliveryBehindMain_MergesGatesAndPublishesTheMergeResult()
    {
        var (repo, remote) = SeedRepoWithOrigin("release-behind-main");
        const string deliveryRef = "agent-studio/results/run-behind-main/result";
        var resultSha = PushRemoteDelivery(repo, deliveryRef, "behind.txt", "delivery work");
        // Another card moved main after this delivery was cut.
        File.WriteAllText(Path.Combine(repo, "other-card.txt"), "another card's work");
        Commit(repo, "feat: another card");
        RunGit(repo, "push -q origin main");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(FullSuite(BuildTestGateVerdict.Ok));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "behind-main");
        WriteRemoteSubject(jobFolder, repo, "AGT-BEHIND-MAIN", "run-behind-main", resultSha, deliveryRef);

        var outcome = await runner.RunAsync("Fixture", "behind-main", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        var mergedSha = Lane(repo, "main");
        Assert.Equal(mergedSha, outcome.MergedSha);
        Assert.Equal(mainBefore, RunGit(repo, $"rev-parse {mergedSha}^1").Out.Trim());
        Assert.Equal(resultSha, RunGit(repo, $"rev-parse {mergedSha}^2").Out.Trim());
        // The full suite tested the exact SHA main advances to.
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(mergedSha, gateRunner.Request!.ExpectedSha);
        Assert.Equal("main", gateRunner.Request.SubjectRef);
        Assert.Equal(TestExecutionLevels.Full, gateRunner.Request.RequiredTestLevel);
        Assert.True(gateRunner.Request.RequireExactSubject);
        var evidence = File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log")));
        Assert.Contains($"expectedSha={mergedSha}", evidence);
        Assert.Null(IntegrationGateJournal.Read(jobFolder));

        // The mapping that acceptance reads: the reviewed delivery is contained
        // in the verified merge, and the step approved exactly that merge.
        var verification = IntegrationVerificationStore.Read(jobFolder);
        Assert.NotNull(verification);
        Assert.Equal(IntegrationVerificationStates.Verified, verification!.State);
        Assert.Equal(mergedSha, verification.Sha);
        Assert.Contains(resultSha, verification.DeliveryShas);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal(mergedSha, step.ApprovedIntegrationSha);
        Assert.Null(step.FailureCode);

        // Publication carries the tested merge to origin; the card is then integrated.
        var pushed = await runner.PushIntegrationBranchAsync(
            "Fixture", "behind-main", jobFolder, repo, "main", CancellationToken.None, approvedSha: mergedSha);
        Assert.True(pushed.Success, pushed.Error);
        Assert.Equal(mergedSha, RemoteSha(remote, "main"));
        Assert.Equal(PublishedDeliveryContainment.Contained, git.CheckPublishedDelivery(repo, "main", resultSha));
    }

    [Fact]
    public async Task RunAsync_MainTarget_ConflictBehindMain_RequestsOneAgentRoundWithTheConflictedFiles()
    {
        var repo = SeedRepo("release-behind-main-conflict");
        RunGit(repo, "checkout -q -b task/conflict-main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "delivery edit");
        Commit(repo, "feat: delivery edits README");
        var taskSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "another card's edit");
        Commit(repo, "feat: another card edits README");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(FullSuite(BuildTestGateVerdict.Ok));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "conflict-main");

        var outcome = await runner.RunAsync("Fixture", "conflict-main", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, outcome.Outcome);
        Assert.Contains("README.md", outcome.ConflictedFiles!);
        Assert.Equal(0, gateRunner.Invocations);
        Assert.Equal(mainBefore, git.GetBranchTip(repo, git.IntegrationLineRef(repo, "main")));
        Assert.NotEqual(0, RunGit(repo, $"merge-base --is-ancestor {taskSha} {git.IntegrationLineRef(repo, "main")}").Code);
        Assert.Null(IntegrationGateJournal.Read(jobFolder));
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal("agent-round-required", step!.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous, step.FailureCode);
        Assert.Contains("README.md", step.VerdictSummary);
        // The continuation starts one automatic round and then hands over to
        // Human Review; it does not wait in 5e-escalated for an operator.
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(outcome.Outcome, automaticAgentRoundsUsed: 0));
        Assert.Equal(
            RemoteIntegrationContinuationAction.LeaveForHumanReview,
            RemoteIntegrationContinuationPolicy.Decide(outcome.Outcome, automaticAgentRoundsUsed: 1));
    }

    [Fact]
    public async Task RunAsync_MainTarget_RedSuiteOnMergeResult_RollsBackAndPublishesNothing()
    {
        var (repo, remote) = SeedRepoWithOrigin("release-behind-main-red");
        const string deliveryRef = "agent-studio/results/run-behind-main-red/result";
        var resultSha = PushRemoteDelivery(repo, deliveryRef, "red.txt", "delivery work");
        File.WriteAllText(Path.Combine(repo, "other-card.txt"), "another card's work");
        Commit(repo, "feat: another card");
        RunGit(repo, "push -q origin main");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(FullSuite(BuildTestGateVerdict.Fail) with
        {
            ExitCode = 1,
            Reason = "full-suite test failed; output: merged tree regression",
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "behind-main-red");
        WriteRemoteSubject(jobFolder, repo, "AGT-BEHIND-RED", "run-behind-main-red", resultSha, deliveryRef);

        var outcome = await runner.RunAsync("Fixture", "behind-main-red", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.BuildGateFailed, outcome.FailureCode);
        Assert.Contains("Pre-main full suite blocked the merge result", outcome.Error);
        Assert.Equal(1, gateRunner.Invocations);
        Assert.NotEqual(mainBefore, gateRunner.Request!.ExpectedSha);
        Assert.Equal(mainBefore, Lane(repo, "main"));
        Assert.Equal(mainBefore, RemoteSha(remote, "main"));
        Assert.Null(IntegrationGateJournal.Read(jobFolder));
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Null(step.ApprovedIntegrationSha);
        Assert.Equal(AcceptedIntegrationFailureCodes.BuildGateFailed, step.FailureCode);
        Assert.Contains("merged tree regression", step.Reason);
    }

    [Fact]
    public async Task RunAsync_MainTarget_MainMovesDuringTheSuite_MergesAgainAndGatesTheNewMerge()
    {
        var (repo, remote) = SeedRepoWithOrigin("release-main-moves");
        const string deliveryRef = "agent-studio/results/run-main-moves/result";
        var resultSha = PushRemoteDelivery(repo, deliveryRef, "moves.txt", "delivery work");
        File.WriteAllText(Path.Combine(repo, "other-card.txt"), "another card's work");
        Commit(repo, "feat: another card");
        RunGit(repo, "push -q origin main");

        var mover = Path.Combine(_tempDir, "release-main-moves-other-host");
        RunGit(_tempDir, $"clone -q \"{remote}\" \"{mover}\"");
        RunGit(mover, "config user.email test@example.com");
        RunGit(mover, "config user.name test");
        string? movedMain = null;
        var gatedShas = new List<string>();
        CapturingBuildTestGateRunner? gateRunner = null;
        gateRunner = new CapturingBuildTestGateRunner(
            FullSuite(BuildTestGateVerdict.Ok),
            () =>
            {
                gatedShas.Add(gateRunner!.Request!.ExpectedSha!);
                if (movedMain is not null) return;
                // A card on another host publishes main while this suite runs.
                File.WriteAllText(Path.Combine(mover, "late-card.txt"), "published during the suite");
                Commit(mover, "feat: late card");
                RunGit(mover, "push -q origin main");
                movedMain = RunGit(mover, "rev-parse HEAD").Out.Trim();
            });
        var (git, log, settings) = BuildWithSettings(repo);
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "main-moves");
        WriteRemoteSubject(jobFolder, repo, "AGT-MAIN-MOVES", "run-main-moves", resultSha, deliveryRef);

        var outcome = await runner.RunAsync("Fixture", "main-moves", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(2, gateRunner.Invocations);
        var mergedSha = Lane(repo, "main");
        Assert.Equal(mergedSha, outcome.MergedSha);
        // The first verdict belonged to a merge that no longer contained main;
        // only the second merge, on the moved main, was tested and kept.
        Assert.NotEqual(gatedShas[0], mergedSha);
        Assert.Equal(mergedSha, gatedShas[1]);
        Assert.Equal(movedMain, RunGit(repo, $"rev-parse {mergedSha}^1").Out.Trim());
        Assert.Equal(resultSha, RunGit(repo, $"rev-parse {mergedSha}^2").Out.Trim());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log").Length);
        Assert.Equal(mergedSha, ReadMergeStep(log, jobFolder)!.ApprovedIntegrationSha);

        var pushed = await runner.PushIntegrationBranchAsync(
            "Fixture", "main-moves", jobFolder, repo, "main", CancellationToken.None, approvedSha: mergedSha);
        Assert.True(pushed.Success, pushed.Error);
        Assert.Equal(mergedSha, RemoteSha(remote, "main"));
    }

    [Fact]
    public async Task RunAsync_MainTarget_MainKeepsMoving_StopsAfterTheBoundWithANamedReason()
    {
        var (repo, remote) = SeedRepoWithOrigin("release-main-keeps-moving");
        const string deliveryRef = "agent-studio/results/run-main-keeps-moving/result";
        var resultSha = PushRemoteDelivery(repo, deliveryRef, "keeps.txt", "delivery work");
        File.WriteAllText(Path.Combine(repo, "other-card.txt"), "another card's work");
        Commit(repo, "feat: another card");
        RunGit(repo, "push -q origin main");

        var mover = Path.Combine(_tempDir, "release-main-keeps-moving-other-host");
        RunGit(_tempDir, $"clone -q \"{remote}\" \"{mover}\"");
        RunGit(mover, "config user.email test@example.com");
        RunGit(mover, "config user.name test");
        var moves = 0;
        var gateRunner = new CapturingBuildTestGateRunner(
            FullSuite(BuildTestGateVerdict.Ok),
            () =>
            {
                moves++;
                File.WriteAllText(Path.Combine(mover, $"late-card-{moves}.txt"), "published during the suite");
                Commit(mover, $"feat: late card {moves}");
                RunGit(mover, "push -q origin main");
            });
        var (git, log, settings) = BuildWithSettings(repo);
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "main-keeps-moving");
        WriteRemoteSubject(jobFolder, repo, "AGT-MAIN-KEEPS", "run-main-keeps-moving", resultSha, deliveryRef);

        var outcome = await runner.RunAsync(
            "Fixture", "main-keeps-moving", jobFolder, repo, "main", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.ReleaseLineMoving, outcome.FailureCode);
        Assert.Contains($"stopped after {ReleaseLineMergePolicy.MaxAttempts}/{ReleaseLineMergePolicy.MaxAttempts} attempts", outcome.Error);
        Assert.Equal(ReleaseLineMergePolicy.MaxAttempts, gateRunner.Invocations);
        // Nothing untested is left on the lane: it holds no merge of the delivery.
        Assert.NotEqual(0, RunGit(repo, $"merge-base --is-ancestor {resultSha} {LaneRef("main")}").Code);
        Assert.NotEqual(0, RunGit(remote, $"-c safe.bareRepository=all merge-base --is-ancestor {resultSha} refs/heads/main").Code);
        Assert.Null(IntegrationGateJournal.Read(jobFolder));
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.ReleaseLineMoving, step.FailureCode);
    }

    private static BuildTestGateResult FullSuite(BuildTestGateVerdict verdict) => new(
        verdict,
        verdict == BuildTestGateVerdict.Ok ? 0 : 1,
        20,
        string.Empty,
        verdict == BuildTestGateVerdict.Ok ? "full suite passed" : "full suite failed",
        true,
        false)
    {
        TestSelection = new TestSelectionAudit
        {
            Level = TestExecutionLevels.Full,
            FullSuiteRequired = true,
            FullSuiteRan = true,
        },
    };

    /// <summary>Publishes a fenced Remote result branch cut from the current main and returns its SHA.</summary>
    private static string PushRemoteDelivery(string repo, string deliveryRef, string file, string content)
    {
        RunGit(repo, $"checkout -q -b {deliveryRef} main");
        File.WriteAllText(Path.Combine(repo, file), content);
        Commit(repo, "feat: remote delivery");
        var resultSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, $"push -q origin {deliveryRef}:{deliveryRef}");
        RunGit(repo, "checkout -q main");
        RunGit(repo, $"branch -D {deliveryRef}");
        RunGit(repo, $"update-ref -d refs/remotes/origin/{deliveryRef}");
        return resultSha;
    }

    private static void WriteRemoteSubject(
        string jobFolder, string repo, string taskKey, string runAttemptId, string resultSha, string deliveryRef)
        => ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = taskKey,
            RunAttemptId = runAttemptId,
            Project = "Fixture",
            Repository = repo,
            ResultSha = resultSha,
            AttemptChainId = "attempt-" + runAttemptId,
            Executor = "agent-runner-01",
            LeaseId = "lease-" + runAttemptId,
            FencingToken = 1,
            ImmutableResultRef = deliveryRef,
            IntegrationBranch = "refs/heads/main",
            CompletedAtUtc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
        });
}
