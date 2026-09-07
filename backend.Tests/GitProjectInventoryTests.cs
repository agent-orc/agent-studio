using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Shape tests for the Project Hub Git View inventory
/// (<see cref="GitService.GetProjectInventory"/>) plus its pure parse helpers.
/// The inventory is what the Git View tree reads to distinguish main /
/// develop / feature / task branches and to show where each checkout /
/// worktree lives on disk, so these tests pin:
///  - the empty/error contract for a non-repo / unknown project (the frontend
///    branches on <c>IsRepo == false</c> for its empty state);
///  - a real repo's branch classification, worktree list (primary + task
///    worktree with a concrete path), branch->worktree linkage, and that
///    recent history is populated;
///  - the porcelain / track / branch-name parse helpers in isolation.
/// The git operations are real (same SeedRepo/RunGit harness style as the
/// other GitService tests), not mocked.
/// </summary>
public class GitProjectInventoryTests : IDisposable
{
    private readonly string _tempDir;

    public GitProjectInventoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-inventory-" + Guid.NewGuid().ToString("N"));
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
    public void Inventory_UnknownProject_ReturnsNotRepoWithError()
    {
        var (repoRoot, watchPath) = SetupRepo();
        var git = BuildGitService(repoRoot, watchPath);

        var inv = git.GetProjectInventory("No Such Project");

        Assert.False(inv.IsRepo);
        Assert.NotNull(inv.Error);
        Assert.Empty(inv.Branches);
        Assert.Empty(inv.Worktrees);
        Assert.Empty(inv.RecentCommits);
    }

    [Fact]
    public async Task Inventory_RealRepo_ClassifiesBranches_ListsWorktrees_AndRecentHistory()
    {
        var (repoRoot, watchPath) = SetupRepo();

        // main + a seed commit so HEAD exists.
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        // Integration + feature + task branches. task/42 gets its own commit so
        // its tip differs from main, and it is checked out into a real worktree.
        RunGit(repoRoot, "branch", "develop");
        RunGit(repoRoot, "checkout", "-q", "-b", "feature/login");
        WriteFile(repoRoot, "login.ts", "export const login = 1;");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "feat: add login form");

        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "checkout", "-q", "-b", "task/42");
        WriteFile(repoRoot, "work.txt", "task work");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "task: do the work");

        // Back on main so task/42 is free to be checked out in the worktree.
        RunGit(repoRoot, "checkout", "-q", "main");
        // Operational safety refs can be numerous in a long-lived repository.
        // They must not enter the user-facing branch inventory scan.
        RunGit(repoRoot, "update-ref", "refs/backups/task-42", "task/42");
        var worktreePath = Path.Combine(_tempDir, "worktrees", "task-42");
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        RunGit(repoRoot, "worktree", "add", worktreePath, "task/42");

        var git = BuildGitService(repoRoot, watchPath);
        var inv = await git.RefreshProjectInventoryAsync("Demo");

        Assert.True(inv.IsRepo);
        Assert.Null(inv.Error);
        Assert.NotNull(inv.ComputedAt);
        Assert.Equal("main", inv.CurrentBranch);
        Assert.NotNull(inv.RepositoryPath);
        Assert.True(Directory.Exists(inv.RepositoryPath!));

        // Branch classification.
        var byName = inv.Branches.ToDictionary(b => b.Name, b => b);
        Assert.Equal("main", byName["main"].Category);
        Assert.Equal("develop", byName["develop"].Category);
        Assert.Equal("feature", byName["feature/login"].Category);
        Assert.Equal("task", byName["task/42"].Category);
        Assert.DoesNotContain(inv.Branches, b => b.Name.Contains("backups", StringComparison.Ordinal));
        Assert.True(byName["main"].IsCurrent);
        Assert.False(byName["task/42"].IsCurrent);
        Assert.All(inv.Branches, b => Assert.False(string.IsNullOrWhiteSpace(b.TipShortSha)));

        // Worktrees: the primary checkout plus the task/42 worktree, each with a
        // concrete on-disk path.
        Assert.True(inv.Worktrees.Count >= 2);
        var primary = Assert.Single(inv.Worktrees, w => w.IsPrimary);
        Assert.False(string.IsNullOrWhiteSpace(primary.Path));
        var taskWt = Assert.Single(inv.Worktrees, w => w.Branch == "task/42");
        Assert.False(taskWt.IsPrimary);
        Assert.False(string.IsNullOrWhiteSpace(taskWt.Path));

        // The task/42 branch entry links back to that worktree folder.
        Assert.Equal(taskWt.Path, byName["task/42"].WorktreePath, ignoreCase: true);

        // Recent history is populated for the current HEAD (main -> "seed").
        Assert.NotEmpty(inv.RecentCommits);
        Assert.Contains(inv.RecentCommits, c => c.Subject == "seed");
    }

    [Fact]
    public async Task Inventory_BoundsRemoteRefs_ToProtectedLines()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        var remote = Path.Combine(_tempDir, "origin.git");
        RunGit(_tempDir, "init", "-q", "--bare", remote);
        RunGit(repoRoot, "remote", "add", "origin", remote);
        RunGit(repoRoot, "push", "-q", "-u", "origin", "main");
        RunGit(repoRoot, "branch", "local-only");
        RunGit(repoRoot, "branch", "remote-only");
        RunGit(repoRoot, "push", "-q", "origin", "remote-only");
        RunGit(repoRoot, "branch", "-D", "remote-only");
        RunGit(repoRoot, "branch", "develop");
        RunGit(repoRoot, "push", "-q", "origin", "develop");

        var git = BuildGitService(repoRoot, watchPath);
        var inventory = await git.RefreshProjectInventoryAsync("Demo");
        var branches = inventory.Branches.ToDictionary(branch => branch.Name, branch => branch);

        Assert.True(branches["main"].IsLocal);
        Assert.True(branches["main"].HasRemote);
        Assert.True(branches["local-only"].IsLocal);
        Assert.False(branches["local-only"].HasRemote);
        Assert.DoesNotContain("remote-only", branches.Keys);
        Assert.True(branches["develop"].HasRemote);
        Assert.Equal("origin/main", branches["main"].Upstream);
    }

    [Fact]
    public async Task History_IsBoundedAndPaged_WithParentsAndRefDecorations()
    {
        var (repoRoot, watchPath) = SetupRepo();
        for (var index = 0; index < 12; index++)
        {
            WriteFile(repoRoot, "history.txt", $"revision {index}");
            RunGit(repoRoot, "add", "-A");
            RunGit(repoRoot, "commit", "-q", "-m", $"history {index}");
        }

        var git = BuildGitService(repoRoot, watchPath);
        await git.RefreshProjectInventoryAsync("Demo");
        var first = git.GetProjectHistory("Demo", offset: 0, pageSize: 10);
        var second = git.GetProjectHistory("Demo", offset: first.NextOffset!.Value, pageSize: 10);

        Assert.Equal(10, first.Commits.Count);
        Assert.True(first.HasMore);
        Assert.Equal(10, first.NextOffset);
        Assert.Equal(2, second.Commits.Count);
        Assert.False(second.HasMore);
        Assert.Null(second.NextOffset);
        Assert.Single(first.Commits[0].ParentShas);
        Assert.Contains(first.Commits[0].Refs, reference => reference.Name == "HEAD");
        Assert.Contains(first.Commits[0].Refs, reference => reference.Name == "main");
        Assert.Empty(first.Commits.Select(commit => commit.Sha)
            .Intersect(second.Commits.Select(commit => commit.Sha)));
    }

    [Fact]
    public async Task Inventory_RequestPath_IsCacheOnly_AndSignatureChangeQueuesOneRefresh()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var git = BuildGitService(repoRoot, watchPath);
        var first = await git.RefreshProjectInventoryAsync("Demo");

        using (GitProcessTelemetry.BeginRequest(
                   "inventory-cache-test", NullLogger.Instance, includeNested: true))
        {
            var hit = git.GetProjectInventory("Demo");
            Assert.Equal(first.ComputedAt, hit.ComputedAt);
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }

        RunGit(repoRoot, "branch", "feature/signature-change");
        var reads = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => git.GetProjectInventory("Demo"))
            .ToList();
        var stale = reads[0];
        var coalesced = reads[^1];
        Assert.Equal(first.ComputedAt, stale.ComputedAt);
        Assert.Equal(first.ComputedAt, coalesced.ComputedAt);
        Assert.Equal(1, git.InventoryRefreshQueueWrites);

        var refreshed = await git.RefreshProjectInventoryAsync("Demo");
        Assert.Contains(refreshed.Branches, branch => branch.Name == "feature/signature-change");
        Assert.True(refreshed.ComputedAt > first.ComputedAt);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Inventory_IgnoresTwoThousandRemoteResultRefs()
    {
        var (repoRoot, watchPath) = SetupRepo();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        for (var index = 0; index < 2_000; index++)
            RunGit(repoRoot, "update-ref", $"refs/remotes/origin/agent-studio/results/run-{index}/fence-1/result", "HEAD");

        var git = BuildGitService(repoRoot, watchPath);
        var inventory = await git.RefreshProjectInventoryAsync("Demo");

        Assert.DoesNotContain(inventory.Branches,
            branch => branch.Name.StartsWith("agent-studio/results/", StringComparison.Ordinal));
        Assert.Single(inventory.Branches, branch => branch.Name == "main");
        Assert.All(inventory.History!.Commits,
            commit => Assert.DoesNotContain(commit.Refs,
                reference => reference.Name.Contains("agent-studio/results/", StringComparison.Ordinal)));

        var stopwatch = Stopwatch.StartNew();
        stopwatch.Restart();
        _ = git.GetProjectInventory("Demo");
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50),
            $"Cached inventory took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void ParseWorktreePorcelain_MarksPrimary_ParsesBranchAndDetached()
    {
        const string sample =
            "worktree /repo/main\n" +
            "HEAD 1111111111111111111111111111111111111111\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repo/wt-task\n" +
            "HEAD 2222222222222222222222222222222222222222\n" +
            "branch refs/heads/task/42\n" +
            "\n" +
            "worktree /repo/detached\n" +
            "HEAD 3333333333333333333333333333333333333333\n" +
            "detached\n";

        var entries = GitService.ParseWorktreePorcelain(sample);

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsPrimary);
        Assert.Equal("main", entries[0].Branch);
        Assert.Equal("1111111", entries[0].HeadShortSha);
        Assert.False(entries[1].IsPrimary);
        Assert.Equal("task/42", entries[1].Branch);
        Assert.True(entries[2].IsDetached);
        Assert.Null(entries[2].Branch);
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("master", "main")]
    [InlineData("develop", "develop")]
    [InlineData("dev", "develop")]
    [InlineData("task/42", "task")]
    [InlineData("runner/agent-runner-01/AGT-2430", "runner")]
    [InlineData("feature/login", "feature")]
    [InlineData("feat/login", "feature")]
    [InlineData("hotfix/x", "other")]
    public void CategorizeBranch_ClassifiesByConvention(string name, string expected)
        => Assert.Equal(expected, GitService.CategorizeBranch(name));

    [Theory]
    [InlineData("[ahead 2, behind 1]", 2, 1)]
    [InlineData("[ahead 3]", 3, 0)]
    [InlineData("[behind 4]", 0, 4)]
    [InlineData("[gone]", 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData(null, 0, 0)]
    public void ParseAheadBehind_ReadsGitTrackString(string? track, int expectedAhead, int expectedBehind)
    {
        var (ahead, behind) = GitService.ParseAheadBehind(track);
        Assert.Equal(expectedAhead, ahead);
        Assert.Equal(expectedBehind, behind);
    }

    private (string repoRoot, string watchPath) SetupRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");
        return (repoRoot, watchPath);
    }

    private static GitService BuildGitService(string repoRoot, string watchPath)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
