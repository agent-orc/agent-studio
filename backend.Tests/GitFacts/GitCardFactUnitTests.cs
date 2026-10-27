using System.Diagnostics;
using System.Text;
using AgentStudio.Git;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Git facts S3 (AGT-3323) building blocks without a host: the batched commit
/// fact format, the per-file patch split and bounds, incremental reachability
/// on a fast-forward and a non-fast-forward move, the run trailer scan, the
/// S3 store tables, and the local configuration read that decides whether the
/// commit-graph settings must be written.
/// </summary>
public sealed class GitCardFactUnitTests : IDisposable
{
    private readonly string _root = AgentStudio.TestSupport.TestTempRoot.Create("git-card-facts");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void CommitFactParser_ReadsHeaderTrailersRenamesBinaryAndNumstat()
    {
        var output = GitCommitFactParser.RecordMarker + string.Join('\u001f',
                new string('a', 40), "aaaaaaa", new string('b', 40), new string('c', 40), "1793100580", "Fixture Author",
                "feat(AGT-1): rename", "Run-Index: 2\nVerdict: accept\n", "")
            + "\0\n:100644 100644 1111111 2222222 R100\0old.txt\0new.txt\0"
            + ":000000 100644 0000000 3333333 A\0bin.dat\0"
            + "1\t0\t\0old.txt\0new.txt\0-\t-\tbin.dat\0"
            + GitCommitFactParser.RecordMarker + string.Join('\u001f',
                new string('d', 40), "ddddddd", "", new string('e', 40), "1793100500", "Root", "root", "", "")
            + "\0\n:000000 100644 0000000 4444444 A\0README.md\0" + "3\t0\tREADME.md\0";

        var facts = GitCommitFactParser.Parse(output);

        Assert.Equal(2, facts.Count);
        var rename = facts[0];
        Assert.Equal([new string('b', 40)], rename.Parents);
        Assert.Equal("feat(AGT-1): rename", rename.Subject);
        Assert.Equal("accept", rename.Trailers["Verdict"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793100580).UtcDateTime, rename.AuthorDateUtc);
        var moved = Assert.Single(rename.Files, file => file.Path == "new.txt");
        Assert.Equal(("R100", "old.txt", 1, 0), (moved.Status, moved.OldPath, moved.Added, moved.Removed));
        Assert.True(Assert.Single(rename.Files, file => file.Path == "bin.dat").Binary);
        var info = rename.ToCommitInfo();
        Assert.Equal((2, 1, 0), (info.FilesChanged, info.Added, info.Removed));
        Assert.Empty(facts[1].Parents);
        Assert.Equal(3, facts[1].Added);
    }

    [Fact]
    public void PatchSplitter_KeysSectionsByNewPathAndMarksOversizedFilesTruncated()
    {
        var big = new StringBuilder("diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n@@ -0,0 +1,6000 @@\n");
        for (var line = 0; line < 6_000; line++) big.Append("+line ").Append(line).Append('\n');
        var patch = "diff --git a/old name.txt b/new name.txt\nsimilarity index 90%\nrename from old name.txt\nrename to new name.txt\n"
            + "diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-x\n"
            + "diff --git \"a/tab\\there.txt\" \"b/tab\\there.txt\"\nnew file mode 100644\nBinary files /dev/null and \"b/tab\\there.txt\" differ\n"
            + "diff --git a/with space.txt b/with space.txt\nold mode 100644\nnew mode 100755\n"
            + big;

        var files = GitPatchSplitter.Split(patch);

        Assert.Equal(["new name.txt", "gone.txt", "tab\there.txt", "with space.txt", "big.txt"],
            files.Select(file => file.Path).ToArray());
        Assert.All(files.Take(4), file => Assert.False(file.Truncated));
        var truncated = files[^1];
        Assert.True(truncated.Truncated);
        Assert.EndsWith(GitPatchSplitter.TruncatedNotice, truncated.Patch);
        Assert.True(truncated.Patch.Count(c => c == '\n') <= GitPatchSplitter.MaxFileLines + 1);
    }

    [Fact]
    public void ReachabilityIndex_AppendsAFastForwardAndRecomputesANonFastForwardMove()
    {
        var repo = InitRepository();
        var first = Commit(repo, "first");
        var second = Commit(repo, "second");
        var index = new GitReachabilityIndex();
        index.Track(repo);

        var set = index.Ensure(repo, "refs/heads/main", second)!;
        Assert.True(set.Contains(first) && set.Contains(second));
        Assert.Equal(1, index.FullRecomputes);
        var initial = Assert.Single(index.TakeChanges(repo));
        Assert.True(initial.Replace);

        var third = Commit(repo, "third");
        var grown = index.Ensure(repo, "refs/heads/main", third)!;
        Assert.True(grown.Contains(third) && grown.Contains(first));
        Assert.Equal((1, 1), (index.FullRecomputes, index.FastForwards));
        var append = Assert.Single(index.TakeChanges(repo));
        Assert.False(append.Replace);
        Assert.Equal([third], append.Shas.ToArray());
        Assert.Same(grown, index.Ensure(repo, "refs/heads/main", third));

        // A reset back to the first commit is not a fast-forward: recompute once.
        Git(repo, "reset", "-q", "--hard", first);
        var reset = index.Ensure(repo, "refs/heads/main", first)!;
        Assert.False(reset.Contains(second));
        Assert.Equal(2, index.FullRecomputes);
        Assert.True(Assert.Single(index.TakeChanges(repo)).Replace);

        Assert.True(index.TryUnion(repo, ["main", "origin/main", "refs/agent-studio/integration/main"], out var union));
        Assert.Equal(reset.Shas.Order(), union.Order());
        Assert.False(index.TryUnion(repo, ["main~1"], out _));
    }

    [Fact]
    public void RunTrailerScan_FindsOnlyCommitsNewSinceTheCoveredTips()
    {
        var repo = InitRepository();
        var marked = Commit(repo, "run one\n\n" + GitService.WorktreeRunCommitTrailer("agt-1"));
        Commit(repo, "plain work");
        var tips = GitRefObjectIds.ReadAll(repo).Values.Where(GitReachabilityIndex.IsSha).ToArray();

        var first = GitFactCommands.ScanRunTrailers(repo, tips, [])!;
        Assert.Equal([(marked, "agt-1")], first.ToArray());

        var second = Commit(repo, "run two\n\n" + GitService.WorktreeRunCommitTrailer("agt-2"));
        var next = GitFactCommands.ScanRunTrailers(repo, [second], tips)!;
        Assert.Equal([(second, "agt-2")], next.ToArray());
        Assert.Null(GitFactCommands.ScanRunTrailers(repo, [second], [new string('f', 40)]));
    }

    [Fact]
    public void Store_RoundTripsCommitFactsReachabilityRangesTrailersMergesViewAndDiffObjects()
    {
        var store = new GitFactStore(Path.Combine(_root, "state", GitFactStoreSchema.FileName), NullLogger.Instance, _clock);
        Assert.Equal("created", store.Open());
        var commit = new GitCommitFact(new string('a', 40), "aaaaaaa", [new string('b', 40)], new string('c', 40),
            _clock.GetUtcNow().UtcDateTime, "Author", "feat: one", new Dictionary<string, string> { ["Run-Index"] = "1" },
            [new GitCommitFileFact("M", "src/a.cs", null, 3, 1, false)]);
        var merge = new GitIntegrationMergeCommit("AGT-1", new string('d', 40), "ddddddd", _clock.GetUtcNow().UtcDateTime,
            "publisher", "merge(AGT-1): one");
        var view = new GitStoredProjectView(0, 4, "signature",
            ProjectIntegrationViewService.Empty("proj", "origin/develop", "origin/main", null!), _clock.GetUtcNow());
        store.WriteCardFacts(new GitCardFactsWrite("proj", [commit],
            [new GitReachabilityChange("refs/heads/develop", commit.Sha, true, [commit.Sha, commit.Parents[0]])],
            new Dictionary<string, IReadOnlyList<string>> { [GitCommitRangeKeys.Sha(commit.Parents[0], commit.Sha)] = [commit.Sha] },
            [("agt-1", commit.Sha)], [new GitMergeIndexChange("origin/develop", true, [merge])],
            new Dictionary<string, string> { ["merges:origin/develop"] = merge.Sha }, view), generation: 7);
        var diff = GitDiffObjectBuilder.FromCommit("proj", commit,
            "diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1 +1,3 @@\n-x\n+y\n+z\n+w\n", _clock.GetUtcNow());
        Assert.True(store.WriteDiffObject(diff));

        var stored = store.LoadCardFacts("proj")!;

        var read = Assert.Single(stored.Commits);
        Assert.Equal((commit.Sha, commit.ShortSha, commit.Tree, commit.AuthorDateUtc, commit.Author, commit.Subject),
            (read.Sha, read.ShortSha, read.Tree, read.AuthorDateUtc, read.Author, read.Subject));
        Assert.Equal(commit.Parents, read.Parents);
        Assert.Equal("1", read.Trailers["Run-Index"]);
        Assert.Equal(commit.Files, read.Files);
        var reach = Assert.Single(stored.Reachability);
        Assert.Equal(("refs/heads/develop", commit.Sha, 2), (reach.FullRef, reach.Tip, reach.Shas.Count));
        Assert.Equal([commit.Sha], stored.Ranges.Values.Single());
        Assert.Equal([commit.Sha], stored.RunCommits["agt-1"]);
        Assert.Equal(merge, Assert.Single(stored.Merges["origin/develop"]));
        Assert.Equal((7L, 4L), (stored.ProjectView!.Generation, stored.ProjectView.TaskGeneration));
        var manifest = store.ReadDiffObjects([diff.Manifest.ObjectId])[diff.Manifest.ObjectId];
        Assert.Equal(GitDiffObjectManifest.Ready, manifest.State);
        Assert.Equal("src/a.cs", Assert.Single(manifest.Files).Path);
        Assert.Contains("+w", Assert.Single(store.ReadDiffObjectPatches(diff.Manifest.ObjectId, "src/a.cs")).Patch);
        Assert.Empty(store.ReadDiffObjectPatches(diff.Manifest.ObjectId, "missing.cs"));
    }

    [Fact]
    public void DiffObjectKeys_FollowTheRouteOwnershipContract()
    {
        var commit = new GitCommitFact(new string('a', 40), "aaaaaaa", [], new string('c', 40), _clock.GetUtcNow().UtcDateTime, "",
            "", new Dictionary<string, string>(), []);
        var key = GitDiffObjectKey.Commit("proj", commit);
        Assert.Equal((GitDiffObjectKey.CommitView, GitDiffObjectKey.RootBase), (key.View, key.BaseSha));
        Assert.NotEqual(key.ObjectId, GitDiffObjectKey.Commit("other", commit).ObjectId);
        Assert.Equal(GitDiffObjectKey.Aggregate("proj", ["A", "b"]).ObjectId,
            GitDiffObjectKey.Aggregate("proj", ["a", "B"]).ObjectId);
        Assert.NotEqual(GitDiffObjectKey.Aggregate("proj", ["a", "b"]).ObjectId,
            GitDiffObjectKey.Aggregate("proj", ["b", "a"]).ObjectId);
    }

    [Theory]
    [InlineData("[core]\n\tcommitGraph = true\n", "core", "commitgraph", true)]
    [InlineData("[core]\n\tbare = false\n[fetch]\n\twriteCommitGraph = yes\n", "fetch", "writecommitgraph", true)]
    [InlineData("[core]\n\tcommitGraph = false\n", "core", "commitgraph", false)]
    [InlineData("[fetch]\n\tprune = true\n", "fetch", "writecommitgraph", false)]
    public void LocalConfig_ReadsBooleanSettingsWithoutGit(string text, string section, string key, bool expected)
        => Assert.Equal(expected, GitLocalConfig.IsTrue(text, section, key));

    private string InitRepository()
    {
        var repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Git(_root, "init", "-q", "-b", "main", repo);
        Git(repo, "config", "user.email", "facts@example.invalid");
        Git(repo, "config", "user.name", "Card Facts");
        Git(repo, "config", "commit.gpgsign", "false");
        return repo;
    }

    private static string Commit(string repo, string message)
    {
        File.AppendAllText(Path.Combine(repo, "file.txt"), message + "\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
