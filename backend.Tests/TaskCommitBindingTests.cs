using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Git;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for the task -&gt; commit binding rules. Tasks regularly produce
/// more than one commit across iterations (continue-mode follow-up,
/// crash-recovery commit + repair, operator-driven steers); each new
/// commit lands on the task's <c>commits</c> chain via
/// <see cref="TaskMutationService.AppendJobCommitOnFolder"/>.
///
/// <para>
/// The rules these tests pin:
/// </para>
/// <list type="bullet">
/// <item>Appending a commit to a job with no prior commits seeds the
///   chain with one entry and updates the legacy singular <c>commit</c>
///   field.</item>
/// <item>Appending a second commit grows the chain to two entries in
///   chronological order; singular <c>commit</c> moves to the newest.</item>
/// <item>A commit that shares its SHA with an existing entry replaces
///   that entry in place rather than bloating the chain - re-stamping
///   the same SHA refreshes metadata (file count, message) without
///   re-ordering.</item>
/// <item>A legacy <c>task.json</c> that carries only the singular
///   <c>commit</c> field gets migrated to the array on the first
///   append, with the legacy entry preserved as the chain head.</item>
/// </list>
/// </summary>
public class TaskCommitBindingTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskCommitBindingTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-task-commit-bind-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AppendCommit_OnEmptyChain_SeedsChain_AndUpdatesLegacySingular()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("alpha", "3-progress", legacyCommit: null);

        var first = MakeCommit("aaaaaaa", "feat: first", filesChanged: 2, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));

        var info = scanner.FindJob("alpha", _watchPath);
        Assert.NotNull(info);
        Assert.Single(info!.Commits);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("aaaaaaa", info.Commit!.ShortSha);
    }

    [Fact]
    public void AppendCommit_PersistenceBoundaryRoundTripsGitDerivedFileMetadata()
    {
        var sha = SeedGitCommit("delivered.txt", "delivered");
        var (scanner, mutations) = Build(withGit: true);
        var jobDir = SeedJobFolder("git-roundtrip", "3-progress", legacyCommit: null);

        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, new TaskCommitInfo
        {
            Sha = sha,
            ShortSha = sha[..7],
            Message = "wip(runner): salvage before teardown - outcome Done",
            FilesChanged = 0,
            Files = [],
            At = DateTime.UtcNow,
        }));

        var persisted = scanner.FindJob("git-roundtrip", _watchPath);
        Assert.NotNull(persisted);
        var commit = Assert.Single(persisted!.Commits);
        Assert.Equal(1, commit.FilesChanged);
        Assert.Equal(["delivered.txt"], commit.Files);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "task.json")));
        var rawCommit = Property(json.RootElement, "commits").EnumerateArray().Single();
        Assert.Equal(1, Property(rawCommit, "filesChanged").GetInt32());
        Assert.Equal("delivered.txt", Property(rawCommit, "files").EnumerateArray().Single().GetString());
    }

    [Fact]
    public void BackfillMissingCommitMetadata_RepairsLiveCardOnceFromGit()
    {
        var sha = SeedGitCommit("backfilled.txt", "backfilled");
        var (_, mutations) = Build(withGit: true);
        var jobDir = SeedJobFolder("metadata-backfill", "5-human-review", legacyCommit: null);
        var archivedJobDir = SeedJobFolder("archived-metadata", "7-archive", legacyCommit: null);
        File.WriteAllText(Path.Combine(jobDir, "task.json"), $$"""
            {
              "id": "metadata-backfill",
              "title": "Fixture",
              "state": "5-human-review",
              "agent": "claude",
              "createdAt": "2026-05-09T08:00:00Z",
              "commits": [
                {
                  "sha": "{{sha}}",
                  "shortSha": "{{sha[..7]}}",
                  "message": "wip(runner): salvage before teardown - outcome Done",
                  "at": "2026-08-03T08:00:00Z"
                }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(archivedJobDir, "task.json"), $$"""
            {
              "id": "archived-metadata",
              "title": "Archived fixture",
              "state": "7-archive",
              "agent": "claude",
              "createdAt": "2026-05-09T08:00:00Z",
              "commits": [
                {
                  "sha": "{{sha}}",
                  "shortSha": "{{sha[..7]}}",
                  "message": "wip(runner): salvage before teardown - outcome Done",
                  "at": "2026-08-03T08:00:00Z"
                }
              ]
            }
            """);

        var first = mutations.BackfillMissingCommitMetadata();
        var second = mutations.BackfillMissingCommitMetadata();

        Assert.Equal(1, first.RepairedTasks);
        Assert.Equal(1, first.RepairedCommits);
        Assert.Equal(0, first.UnresolvedTasks);
        Assert.Equal(0, second.RepairedTasks);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "task.json")));
        var rawCommit = Property(json.RootElement, "commits").EnumerateArray().Single();
        Assert.Equal(1, Property(rawCommit, "filesChanged").GetInt32());
        Assert.Equal("backfilled.txt", Property(rawCommit, "files").EnumerateArray().Single().GetString());
        using var archivedJson = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(archivedJobDir, "task.json")));
        var archivedCommit = Property(archivedJson.RootElement, "commits").EnumerateArray().Single();
        Assert.DoesNotContain(
            archivedCommit.EnumerateObject(),
            property => string.Equals(
                property.Name,
                "filesChanged",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackfillLegacyCommitRepositories_PersistsMessagePrefixOnce()
    {
        var (_, mutations) = Build();
        var legacy = MakeCommit(
            "abc1234",
            "[runner] feat: publish remote runner",
            filesChanged: 2,
            atIso: "2026-08-04T10:00:00Z");
        var jobDir = SeedJobFolder("repository-backfill", "5-human-review", legacy);

        Assert.Equal(1, mutations.BackfillLegacyCommitRepositories());
        Assert.Equal(0, mutations.BackfillLegacyCommitRepositories());

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "task.json")));
        var rawCommit = Property(json.RootElement, "commits").EnumerateArray().Single();
        Assert.Equal("runner", Property(rawCommit, "repository").GetString());
        Assert.Equal(legacy.Message, Property(rawCommit, "message").GetString());
    }

    [Fact]
    public void AppendCommit_TwiceWithDifferentShas_GrowsChain_LegacyTracksNewest()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("beta", "3-progress", legacyCommit: null);

        var first  = MakeCommit("aaaaaaa", "feat: first",  filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        var second = MakeCommit("bbbbbbb", "fix: follow-up", filesChanged: 3, atIso: "2026-05-09T10:30:00Z");

        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, second));

        var info = scanner.FindJob("beta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.Equal("bbbbbbb", info.Commits[1].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("bbbbbbb", info.Commit!.ShortSha);
    }

    [Fact]
    public void RecordMechanicalRebase_PreservesOldShaAndAppendsAttributedReplacement()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("rebased", "3-progress", legacyCommit: null);
        var original = MakeCommit(
            "aaaaaaa",
            "feat: delivered work",
            filesChanged: 2,
            atIso: "2026-05-09T10:00:00Z") with
        {
            RunAttemptId = "round-1",
            SupersededByAttempt = "historical-attempt",
        };
        var replacementSha = Pad("bbbbbbb");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, original));

        Assert.True(mutations.RecordMechanicalRebaseOnFolder(
            jobDir,
            [new RebasedCommitReplacement(original.Sha, replacementSha)]));

        var info = scanner.FindJob("rebased", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal(replacementSha, info.Commits[0].SupersededBySha);
        Assert.Equal("historical-attempt", info.Commits[0].SupersededByAttempt);
        Assert.Equal(replacementSha, info.Commits[1].Sha);
        Assert.Equal(original.Message, info.Commits[1].Message);
        Assert.Equal(original.FilesChanged, info.Commits[1].FilesChanged);
        Assert.Equal("round-1", info.Commits[1].RunAttemptId);
        Assert.Null(info.Commits[1].SupersededBySha);
        Assert.Null(info.Commits[1].SupersededByAttempt);
        Assert.Equal(replacementSha, info.Commit!.Sha);
    }

    [Fact]
    public void AppendCommit_WithExistingSha_ReplacesInPlace_DoesNotDuplicate()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("gamma", "3-progress", legacyCommit: null);

        var first  = MakeCommit("aaaaaaa", "feat: first",  filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        var second = MakeCommit("bbbbbbb", "fix: follow",  filesChanged: 1, atIso: "2026-05-09T10:10:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, second));

        // Re-stamp the second commit with refreshed metadata. Some flows
        // re-derive the file list from `git show --name-status` after the
        // initial stamp; the chain must show the latest numbers without
        // adding a duplicate row.
        var refreshed = MakeCommit("bbbbbbb", "fix: follow (refreshed)", filesChanged: 5, atIso: "2026-05-09T10:11:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, refreshed));

        var info = scanner.FindJob("gamma", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal(5, info.Commits[1].FilesChanged);
        Assert.Equal("fix: follow (refreshed)", info.Commits[1].Message);
    }

    [Fact]
    public void AppendCommit_OverLegacySingularOnly_MigratesToChain_PreservingLegacyAsHead()
    {
        // A job with the pre-migration shape (only the singular `commit`
        // object on disk, no `commits` array). The first append after the
        // migration must preserve the legacy entry as the chain head and
        // grow from there. Anything else would drop history the moment a
        // continuation lands.
        var legacy = MakeCommit("9999999", "chore: pre-migration", filesChanged: 4, atIso: "2026-05-09T09:00:00Z");
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("delta", "3-progress", legacyCommit: legacy);

        var followup = MakeCommit("ddddddd", "feat: post-migration", filesChanged: 2, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, followup));

        var info = scanner.FindJob("delta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("9999999", info.Commits[0].ShortSha);
        Assert.Equal("ddddddd", info.Commits[1].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("ddddddd", info.Commit!.ShortSha);
    }

    [Fact]
    public void AppendJobCommit_FailsCleanly_OnMissingFolder()
    {
        var (_, mutations) = Build();
        var nonexistent = Path.Combine(_workspace, "nope");
        var commit = MakeCommit("aaaaaaa", "feat: x", filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        Assert.False(mutations.AppendJobCommitOnFolder(nonexistent, commit));
    }

    [Fact]
    public void SetCommitAttribution_EmptyOverNonEmptyPersisted_RefusesWipe()
    {
        // A run that crashes seconds into a resume can drive the attribution
        // post-step with an empty result; the replace-all write must NOT erase
        // the task's already-landed commit metadata.
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("epsilon", "3-progress", legacyCommit: null);

        var landed = MakeCommit("aaaaaaa", "feat: landed", filesChanged: 2, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, landed));

        Assert.False(mutations.SetCommitAttributionOnFolder(jobDir, new List<TaskCommitInfo>()));

        var info = scanner.FindJob("epsilon", _watchPath);
        Assert.NotNull(info);
        Assert.Single(info!.Commits);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("aaaaaaa", info.Commit!.ShortSha);
    }

    [Fact]
    public void SetCommitAttribution_NonEmpty_ReplacesChain()
    {
        // The guard only blocks the empty-over-non-empty wipe; a real
        // attribution result still rewrites the chain.
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("zeta", "3-progress", legacyCommit: null);

        var seed = MakeCommit("aaaaaaa", "feat: seed", filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, seed));

        var attributed = new List<TaskCommitInfo>
        {
            MakeCommit("aaaaaaa", "feat: seed",  filesChanged: 1, atIso: "2026-05-09T10:00:00Z"),
            MakeCommit("bbbbbbb", "fix: second", filesChanged: 2, atIso: "2026-05-09T10:30:00Z"),
        };
        Assert.True(mutations.SetCommitAttributionOnFolder(jobDir, attributed));

        var info = scanner.FindJob("zeta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("bbbbbbb", info.Commit!.ShortSha);
    }

    [Fact]
    public void SetCommitAttribution_EmptyOverEmptyPersisted_FailsOpen()
    {
        // Nothing to protect: an empty write over an empty chain is a no-op
        // that must not be mistaken for a refused wipe.
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("eta", "3-progress", legacyCommit: null);

        Assert.True(mutations.SetCommitAttributionOnFolder(jobDir, new List<TaskCommitInfo>()));

        var info = scanner.FindJob("eta", _watchPath);
        Assert.NotNull(info);
        Assert.Empty(info!.Commits);
    }

    [Fact]
    public void SetRemoteCommitAttribution_TwoGenerations_PersistsTheirUnionWithProducerIdentity()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("theta", "3-progress", legacyCommit: null);
        var first = MakeCommit(
            "aaaaaaaa", "feat(AGT-2462): first generation", 1,
            "2026-07-30T10:00:00Z") with
        {
            Repository = "https://github.com/example/agent-studio.git",
            Branch = "agent-studio/results/run_first/result-a",
        };
        var second = MakeCommit(
            "bbbbbbbb", "fix(AGT-2462): second generation", 2,
            "2026-07-30T11:00:00Z") with
        {
            Repository = "https://github.com/example/runner.git",
            Branch = "agent-studio/results/run_second/result-b",
        };

        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-first", "runner-a", first.Sha, [first]));
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-second", "runner-b", second.Sha, [second]));

        // The ordinary transition attribution pass may subsequently refresh
        // commit metadata. It must not strip the remote generation proof.
        Assert.True(mutations.SetCommitAttributionOnFolder(
            jobDir,
            [first with { Branch = null }, second with { Branch = null }]));

        var info = scanner.FindJob("theta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal([first.Sha, second.Sha], info!.Commits.Select(commit => commit.Sha));
        Assert.Equal("run-first", info.Commits[0].RunAttemptId);
        Assert.Equal("runner-a", info.Commits[0].RunnerId);
        Assert.Equal(first.Sha, info.Commits[0].ResultSha);
        Assert.Equal(first.Repository, info.Commits[0].Repository);
        Assert.Equal(first.Branch, info.Commits[0].Branch);
        Assert.Equal("run-second", info.Commits[1].RunAttemptId);
        Assert.Equal("runner-b", info.Commits[1].RunnerId);
        Assert.Equal(second.Sha, info.Commits[1].ResultSha);
        Assert.Equal(second.Repository, info.Commits[1].Repository);
        Assert.Equal(second.Branch, info.Commits[1].Branch);
        Assert.Equal(second.Sha, info.Commit!.Sha);
    }

    [Fact]
    public void RequeueSupersession_PreservesHistory_AndResolvesToNextRunAttempt()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("supersession", "5-human-review", legacyCommit: null);
        var fence = MakeCommit(
            "aaaaaaaa",
            "wip(runner): salvage before teardown - outcome Done",
            2,
            "2026-08-09T10:00:00Z") with { RunAttemptId = "round-1" };
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, fence));

        var marked = mutations.SupersedeCurrentDeliveryOnFolder(
            jobDir,
            TaskCommitSupersession.PendingAttempt);

        Assert.True(marked.Succeeded);
        Assert.Equal(1, marked.MarkedCommits);
        var pending = scanner.FindJob("supersession", _watchPath);
        Assert.NotNull(pending);
        Assert.Equal(TaskCommitSupersession.PendingAttempt, pending!.Commits[0].SupersededByAttempt);

        var replacement = MakeCommit(
            "bbbbbbbb",
            "feat(AGT-2533): complete replacement delivery",
            2,
            "2026-08-09T11:00:00Z");
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir,
            "round-2",
            "runner-b",
            replacement.Sha,
            [replacement]));

        var persisted = scanner.FindJob("supersession", _watchPath);
        Assert.NotNull(persisted);
        Assert.Equal([fence.Sha, replacement.Sha], persisted!.Commits.Select(commit => commit.Sha));
        Assert.Equal("round-2", persisted.Commits[0].SupersededByAttempt);
        Assert.Null(persisted.Commits[1].SupersededByAttempt);
        Assert.Equal("round-2", persisted.Commits[1].RunAttemptId);
    }

    [Fact]
    public void AppendCommit_RefreshDoesNotEraseSupersessionMarker()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("supersession-refresh", "5-human-review", legacyCommit: null);
        var original = MakeCommit(
            "aaaaaaaa",
            "wip(runner): salvage before teardown - outcome Done",
            1,
            "2026-08-09T10:00:00Z") with { SupersededByAttempt = "round-2" };
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, original));

        Assert.True(mutations.AppendJobCommitOnFolder(
            jobDir,
            original with { Message = "refreshed metadata", SupersededByAttempt = null }));

        var persisted = scanner.FindJob("supersession-refresh", _watchPath);
        Assert.NotNull(persisted);
        Assert.Equal("round-2", Assert.Single(persisted!.Commits).SupersededByAttempt);
    }

    [Fact]
    public void SetRemoteCommitAttribution_LocalThenRemote_PreservesBothGenerations()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("theta-mixed", "3-progress", legacyCommit: null);
        var local = MakeCommit(
            "aaaaaaaa", "feat(AGT-2462): local generation", 1,
            "2026-07-30T10:00:00Z") with { Branch = "task/AGT-2462" };
        var remote = MakeCommit(
            "bbbbbbbb", "fix(AGT-2462): remote continuation", 2,
            "2026-07-30T11:00:00Z") with { Branch = "runner/runner-b/AGT-2462" };

        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, local));

        var guardedRemote = RemoteCommitAttributionGuard.Attribute(
            "AGT-2462",
            remote.Branch!,
            [new GitCommitInfo(
                remote.Sha,
                remote.ShortSha,
                remote.At,
                "Runner B",
                remote.Message,
                remote.FilesChanged,
                1,
                0)]);
        Assert.True(guardedRemote.Accepted);
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir,
            "run-remote",
            "runner-b",
            remote.Sha,
            guardedRemote.Commits));

        var info = scanner.FindJob("theta-mixed", _watchPath);
        Assert.NotNull(info);
        Assert.Equal([local.Sha, remote.Sha], info!.Commits.Select(commit => commit.Sha));
        Assert.Null(info.Commits[0].RunAttemptId);
        Assert.Equal(local.Branch, info.Commits[0].Branch);
        Assert.Equal("run-remote", info.Commits[1].RunAttemptId);
        Assert.Equal("runner-b", info.Commits[1].RunnerId);
        Assert.Equal(remote.Sha, info.Commits[1].ResultSha);
    }

    [Fact]
    public void SetRemoteCommitAttribution_ReplayedGeneration_ReplacesOnlyThatGeneration()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("theta-replay", "3-progress", legacyCommit: null);
        var first = MakeCommit(
            "aaaaaaaa", "feat(AGT-2462): first generation", 1,
            "2026-07-30T10:00:00Z");
        var staleSecond = MakeCommit(
            "bbbbbbbb", "fix(AGT-2462): stale second result", 1,
            "2026-07-30T11:00:00Z");
        var replayedSecond = MakeCommit(
            "cccccccc", "fix(AGT-2462): corrected second result", 1,
            "2026-07-30T11:30:00Z");

        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-first", "runner-a", first.Sha, [first]));
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-second", "runner-b", staleSecond.Sha, [staleSecond]));
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-second", "runner-b", replayedSecond.Sha, [replayedSecond]));

        var info = scanner.FindJob("theta-replay", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(
            [first.Sha, replayedSecond.Sha],
            info!.Commits.Select(commit => commit.Sha));
        Assert.Equal("run-first", info.Commits[0].RunAttemptId);
        Assert.Equal("run-second", info.Commits[1].RunAttemptId);
        Assert.Equal(replayedSecond.Sha, info.Commits[1].ResultSha);
    }

    [Fact]
    public void SetRemoteCommitAttribution_InheritedCommit_KeepsItsOriginalGeneration()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("iota", "3-progress", legacyCommit: null);
        var inherited = MakeCommit(
            "aaaaaaaa", "feat(AGT-2462): inherited work", 1,
            "2026-07-30T10:00:00Z") with { Branch = "runner/runner-a/AGT-2462" };
        var continuation = MakeCommit(
            "bbbbbbbb", "fix(AGT-2462): continuation", 1,
            "2026-07-30T11:00:00Z") with { Branch = "runner/runner-b/AGT-2462" };

        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-first", "runner-a", inherited.Sha, [inherited]));
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-second", "runner-b", continuation.Sha, [inherited, continuation]));

        var info = scanner.FindJob("iota", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("run-first", info.Commits[0].RunAttemptId);
        Assert.Equal("runner-a", info.Commits[0].RunnerId);
        Assert.Equal("run-second", info.Commits[1].RunAttemptId);
        Assert.Equal("runner-b", info.Commits[1].RunnerId);
    }

    [Fact]
    public void SetRemoteCommitAttribution_EmptyRejectedGeneration_DoesNotErasePriorGenerations()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("kappa", "3-progress", legacyCommit: null);
        var first = MakeCommit(
            "aaaaaaaa", "feat(AGT-2462): safe generation", 1,
            "2026-07-30T10:00:00Z") with { Branch = "runner/runner-a/AGT-2462" };

        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-first", "runner-a", first.Sha, [first]));

        var rejected = RemoteCommitAttributionGuard.Attribute(
            "AGT-2462",
            "runner/runner-b/AGT-2462",
            [new GitCommitInfo(
                new string('f', 40),
                "ffffffff",
                DateTime.Parse("2026-07-30T11:00:00Z"),
                "Runner B",
                "fix(AGT-9999): foreign task work",
                1,
                1,
                0)]);
        Assert.False(rejected.Accepted);
        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir, "run-foreign", "runner-b", new string('f', 40), rejected.Commits));

        var info = scanner.FindJob("kappa", _watchPath);
        Assert.NotNull(info);
        Assert.Single(info!.Commits);
        Assert.Equal(first.Sha, info.Commits[0].Sha);
        Assert.Equal("run-first", info.Commits[0].RunAttemptId);
    }

    [Fact]
    public void SetRemoteCommitAttribution_LegacyBackfill_CanReplaceUnscopedForeignEvidence()
    {
        var legacy = MakeCommit(
            "ffffffff", "fix(AGT-9999): legacy foreign work", 1,
            "2026-07-29T10:00:00Z");
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("lambda", "3-progress", legacy);

        Assert.True(mutations.SetRemoteCommitAttributionOnFolder(
            jobDir,
            "legacy-backfill:AGT-2462",
            "runner-a",
            new string('f', 40),
            [],
            replaceUnscopedLegacyAttribution: true));

        var info = scanner.FindJob("lambda", _watchPath);
        Assert.NotNull(info);
        Assert.Empty(info!.Commits);
        Assert.Null(info.Commit);
    }

    private string SeedJobFolder(string id, string lane, TaskCommitInfo? legacyCommit)
    {
        var laneDir = Path.Combine(_watchPath, lane);
        Directory.CreateDirectory(laneDir);
        var jobDir = Path.Combine(laneDir, id);
        Directory.CreateDirectory(jobDir);
        var jobJson = legacyCommit == null
            ? $$"""
                {
                  "id": "{{id}}",
                  "title": "Fixture",
                  "state": "{{lane}}",
                  "agent": "claude",
                  "createdAt": "2026-05-09T08:00:00Z"
                }
                """
            : $$"""
                {
                  "id": "{{id}}",
                  "title": "Fixture",
                  "state": "{{lane}}",
                  "agent": "claude",
                  "createdAt": "2026-05-09T08:00:00Z",
                  "commit": {
                    "sha": "{{Pad(legacyCommit.Sha)}}",
                    "shortSha": "{{legacyCommit.ShortSha}}",
                    "message": "{{legacyCommit.Message}}",
                    "filesChanged": {{legacyCommit.FilesChanged}},
                    "files": [],
                    "at": "{{legacyCommit.At:o}}"
                  }
                }
                """;
        File.WriteAllText(Path.Combine(jobDir, "task.json"), jobJson);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");
        return jobDir;
    }

    private static TaskCommitInfo MakeCommit(string shortSha, string message, int filesChanged, string atIso)
    {
        var fullSha = Pad(shortSha);
        return new TaskCommitInfo
        {
            Sha = fullSha,
            ShortSha = shortSha,
            Message = message,
            FilesChanged = filesChanged,
            Files = new List<string>(),
            At = DateTime.Parse(atIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        };
    }

    private static string Pad(string s) => s.Length >= 40 ? s : s + new string('0', 40 - s.Length);

    private (TaskScannerService scanner, TaskMutationService mutations) Build(bool withGit = false)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = withGit ? new GitService(NullLogger<GitService>.Instance, scanner, config) : null;
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance, git: git);
        return (scanner, mutations);
    }

    private string SeedGitCommit(string path, string contents)
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_watchPath, path), contents);
        RunGit("add", "--", path);
        RunGit("commit", "-q", "-m", "fixture commit");
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _watchPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.EnumerateObject()
            .Single(property => string.Equals(
                property.Name,
                name,
                StringComparison.OrdinalIgnoreCase))
            .Value;

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
