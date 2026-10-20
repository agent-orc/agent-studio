using AgentStudio.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3215: an external push to origin/develop while the integration lane
/// holds unpublished merges. Deliveries origin already carries, as such or as a
/// rebased successor of the same card, are dropped behind a backup ref and
/// integration continues; genuinely unpublished work stays and stalls visibly.
/// </summary>
public sealed partial class MergeIntoDevelopRunnerTests
{
    [Fact]
    public void LaneDivergence_ExternalPushContainsTheLaneDelivery_BacksUpLaneAndContinues()
    {
        var (repo, remote) = SeedRepoWithOrigin("lane-contained");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, "checkout -q -b task/contained");
        File.WriteAllText(Path.Combine(repo, "contained.txt"), "contained delivery");
        Commit(repo, "feat: contained delivery");
        RunGit(repo, "checkout -q -b task/next develop");
        File.WriteAllText(Path.Combine(repo, "next.txt"), "next delivery");
        Commit(repo, "feat: next delivery");
        RunGit(repo, "checkout -q develop");

        var entries = new List<(LogLevel Level, string Message)>();
        var (git, log, settings) = BuildWithSettings(repo, new CollectingLogger<GitService>(entries));
        var alarm = new RecordingLaneAlarm();
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: new IntegrationPushQueue(), projectSettings: settings,
            timeProvider: new FakeTimeProvider(), laneStallAlarm: alarm);
        var first = runner.Run("Fixture", "contained", BeginRun(log, repo, "contained"), repo, "develop");
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, first.Outcome);
        var droppedDelivery = RunGit(repo, $"rev-parse {first.MergedSha}^2").Out.Trim();

        // The operator candidate carries the delivery and moves origin past the lane.
        RunGit(repo, "checkout -q -b operator-candidate develop");
        RunGit(repo, "merge -q --no-ff -m \"Merge contained delivery into operator candidate\" task/contained");
        File.WriteAllText(Path.Combine(repo, "operator.txt"), "operator change");
        Commit(repo, "chore: operator candidate");
        RunGit(repo, "push -q origin operator-candidate:develop");
        RunGit(repo, "checkout -q develop");
        var published = RemoteSha(remote, "develop");

        var next = runner.Run("Fixture", "next", BeginRun(log, repo, "next"), repo, "develop");

        Assert.True(next.Outcome == MergeIntoIntegrationOutcome.Merged, next.Error);
        Assert.Equal(published, RunGit(repo, $"rev-parse {next.MergedSha}^1").Out.Trim());
        Assert.Equal(
            first.MergedSha,
            RunGit(repo, $"rev-parse {GitService.IntegrationLaneBackupRef("develop", first.MergedSha!)}").Out.Trim());
        var receipt = Assert.Single(entries, entry => entry.Message.Contains("restarted at", StringComparison.Ordinal));
        Assert.Contains(droppedDelivery[..9], receipt.Message);
        Assert.Contains("contained in origin", receipt.Message);
        Assert.Empty(alarm.Stalls);
        Assert.Equal(2, alarm.Recovered);
    }

    [Fact]
    public void LaneDivergence_ExternalPushWithRebasedSuccessor_DropsTheSupersededDelivery()
    {
        var (repo, remote) = SeedRepoWithOrigin("lane-superseded");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, "checkout -q -b task/older");
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "first delivery of the card");
        Commit(repo, "feat: card delivery");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: new IntegrationPushQueue(), projectSettings: settings);
        var older = runner.Run("Fixture", "older", BeginRun(log, repo, "older"), repo, "develop");
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, older.Outcome);
        var olderDelivery = RunGit(repo, $"rev-parse {older.MergedSha}^2").Out.Trim();

        // Origin moves first; the card's delivery is rebased onto it with a
        // conflict-adjusted content, so neither ancestry, tree, nor patch id matches.
        RunGit(repo, "checkout -q -b operator-candidate develop");
        File.WriteAllText(Path.Combine(repo, "operator.txt"), "operator change");
        Commit(repo, "chore: operator change");
        RunGit(repo, "checkout -q -b task/successor operator-candidate");
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "rebased delivery of the card");
        Commit(repo, "feat: card delivery");
        var successor = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q operator-candidate");
        RunGit(repo, "merge -q --no-ff -m \"Merge card delivery into operator candidate\" task/successor");
        RunGit(repo, "push -q origin operator-candidate:develop");
        RunGit(repo, "checkout -q develop");
        var published = RemoteSha(remote, "develop");

        var worktree = new IntegrationWorktreeProvider(git).Resolve(repo, "develop").Path!;
        var lane = GitService.IntegrationLaneRef("develop");

        // Without card truth the older delivery is indistinguishable from unpublished work.
        var blind = git.SynchronizeIntegrationBranch(worktree, "develop");
        Assert.Equal(IntegrationBranchSyncOutcome.Diverged, blind.Outcome);
        Assert.Equal(older.MergedSha, RunGit(repo, $"rev-parse {lane}").Out.Trim());

        var lineage = new FixedLaneLineage(older.MergedSha!, olderDelivery, new LaneDeliveryCard("AGT-3065", successor));
        var healed = git.SynchronizeIntegrationBranch(worktree, "develop", lineage: lineage);

        Assert.Equal(IntegrationBranchSyncOutcome.FastForwarded, healed.Outcome);
        Assert.Equal(published, RunGit(repo, $"rev-parse {lane}").Out.Trim());
        Assert.Equal(
            older.MergedSha,
            RunGit(repo, $"rev-parse {GitService.IntegrationLaneBackupRef("develop", older.MergedSha!)}").Out.Trim());
        var dropped = Assert.Single(healed.LaneCommits!);
        Assert.Equal(LaneCommitStatus.Superseded, dropped.Status);
        Assert.Equal("AGT-3065", dropped.CardKey);
        Assert.Equal(successor, dropped.SuccessorSha);
    }

    [Fact]
    public void LaneDivergence_UnrelatedExternalPushOverRealUnpublishedMerge_KeepsLaneAndRaisesStall()
    {
        var (repo, remote) = SeedRepoWithOrigin("lane-unpublished");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, "checkout -q -b task/pending");
        File.WriteAllText(Path.Combine(repo, "pending.txt"), "pending delivery");
        Commit(repo, "feat: pending delivery");
        foreach (var blocked in new[] { "blocked-a", "blocked-b" })
        {
            RunGit(repo, $"checkout -q -b task/{blocked} develop");
            File.WriteAllText(Path.Combine(repo, blocked + ".txt"), blocked);
            Commit(repo, $"feat: {blocked}");
        }
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var alarm = new RecordingLaneAlarm();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 20, 55, 0, TimeSpan.Zero));
        var runner = new MergeIntoDevelopRunner(git, log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: new IntegrationPushQueue(), projectSettings: settings,
            timeProvider: time, laneStallAlarm: alarm);
        var pending = runner.Run("Fixture", "pending", BeginRun(log, repo, "pending"), repo, "develop");
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, pending.Outcome);
        var pendingDelivery = RunGit(repo, "rev-parse task/pending").Out.Trim();

        RunGit(repo, "checkout -q -b origin-side develop");
        File.WriteAllText(Path.Combine(repo, "unrelated.txt"), "unrelated origin work");
        Commit(repo, "chore: unrelated origin work");
        RunGit(repo, "push -q origin origin-side:develop");
        RunGit(repo, "checkout -q develop");
        var published = RemoteSha(remote, "develop");

        var a = runner.Run("Fixture", "blocked-a", BeginRun(log, repo, "blocked-a"), repo, "develop");
        var b = runner.Run("Fixture", "blocked-b", BeginRun(log, repo, "blocked-b"), repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Error, a.Outcome);
        Assert.Equal(MergeIntoIntegrationOutcome.Error, b.Outcome);
        Assert.Contains("diverged from origin", a.Error);
        Assert.Equal(pending.MergedSha, RunGit(repo, $"rev-parse {GitService.IntegrationLaneRef("develop")}").Out.Trim());
        Assert.NotEqual(
            0,
            RunGit(repo, $"rev-parse --verify --quiet {GitService.IntegrationLaneBackupRef("develop", pending.MergedSha!)}").Code);
        Assert.Equal(published, RemoteSha(remote, "develop"));

        Assert.Equal(["blocked-a", "blocked-b"], alarm.Stalls.Select(stall => stall.JobId));
        var stall = alarm.Stalls[^1];
        Assert.Equal("develop", stall.Branch);
        Assert.Equal(time.GetUtcNow().UtcDateTime, stall.DetectedAtUtc);
        var unpublished = Assert.Single(stall.Commits);
        Assert.Equal(LaneCommitStatus.Unpublished, unpublished.Status);
        Assert.Equal(pending.MergedSha, unpublished.CommitSha);
        Assert.Equal(pendingDelivery, unpublished.DeliverySha);
    }

    private sealed class FixedLaneLineage(string laneCommit, string delivery, LaneDeliveryCard card)
        : IIntegrationLaneDeliveryLineage
    {
        public LaneDeliveryCard? Find(string laneCommitSha, string deliverySha)
            => laneCommitSha == laneCommit && deliverySha == delivery ? card : null;
    }

    private sealed class RecordingLaneAlarm : IIntegrationLaneStallAlarm
    {
        public List<IntegrationLaneStall> Stalls { get; } = [];
        public int Recovered { get; private set; }

        public void IntegrationLaneStalled(IntegrationLaneStall stall) => Stalls.Add(stall);

        public void IntegrationLaneRecovered(string project, string branch, DateTime recoveredAtUtc) => Recovered++;
    }
}
