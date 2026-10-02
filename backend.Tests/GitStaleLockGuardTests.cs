using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3000: a git lock left by a dead git process made every workspace
/// evidence flush fail for 36 hours, and a 92-hour-old worktree index lock
/// blocked the Temp integration slot. The guard clears a lock that is older
/// than the threshold and held by no git process, waits once for a young or
/// owned lock, and otherwise leaves git to fail as before.
/// </summary>
public sealed class GitStaleLockGuardTests : IDisposable
{
    private readonly string _root;

    public GitStaleLockGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "git-stale-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Pure policy matrix -------------------------------------------------

    [Theory]
    [InlineData(0, GitLockOwnership.None, GitLockVerdict.KeepYoung)]
    [InlineData(9, GitLockOwnership.None, GitLockVerdict.KeepYoung)]
    [InlineData(9, GitLockOwnership.Owned, GitLockVerdict.KeepYoung)]
    [InlineData(9, GitLockOwnership.Unknown, GitLockVerdict.KeepYoung)]
    [InlineData(10, GitLockOwnership.None, GitLockVerdict.Clear)]
    [InlineData(2160, GitLockOwnership.None, GitLockVerdict.Clear)]
    [InlineData(10, GitLockOwnership.Owned, GitLockVerdict.KeepOwned)]
    [InlineData(5520, GitLockOwnership.Owned, GitLockVerdict.KeepOwned)]
    [InlineData(10, GitLockOwnership.Unknown, GitLockVerdict.KeepOwnerUnknown)]
    [InlineData(-5, GitLockOwnership.None, GitLockVerdict.KeepYoung)]
    public void Policy_decides_by_age_first_then_ownership(int ageMinutes, GitLockOwnership ownership, GitLockVerdict expected)
    {
        var verdict = GitStaleLockPolicy.Decide(
            TimeSpan.FromMinutes(ageMinutes), GitStaleLockPolicy.DefaultThreshold, ownership);

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void Policy_probes_ownership_only_for_aged_locks()
    {
        Assert.False(GitStaleLockPolicy.NeedsOwnershipProbe(TimeSpan.FromMinutes(9), GitStaleLockPolicy.DefaultThreshold));
        Assert.True(GitStaleLockPolicy.NeedsOwnershipProbe(TimeSpan.FromMinutes(10), GitStaleLockPolicy.DefaultThreshold));
    }

    [Theory]
    [InlineData(40, "40s")]
    [InlineData(12 * 60, "12m")]
    [InlineData(36 * 3600 + 43 * 60, "36h43m")]
    [InlineData(-3, "0s")]
    public void Age_is_logged_in_a_compact_token(int seconds, string expected)
        => Assert.Equal(expected, GitStaleLockPolicy.FormatAge(TimeSpan.FromSeconds(seconds)));

    // ---- Guard against a real repository -----------------------------------

    [Fact]
    public void Stale_index_lock_is_cleared_logged_and_the_write_succeeds()
    {
        var repo = SeedRepo("workspace");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(36));
        Assert.NotEqual(0, Git(repo, "add", "README.md").Code);

        var logger = new CapturingLogger<GitStaleLockGuard>();
        var waits = new List<TimeSpan>();
        var guard = Guard(new FixedProbe(GitLockOwnership.None), logger, waits);

        var result = guard.EnsureWritable(repo);

        Assert.False(File.Exists(lockPath));
        var cleared = Assert.Single(result.Cleared);
        Assert.Equal(lockPath, cleared.Path);
        Assert.Empty(result.Remaining);
        Assert.Empty(waits);
        Assert.Contains(logger.Messages, message =>
            message.StartsWith("git-stale-lock-cleared repo=" + repo, StringComparison.Ordinal)
            && message.Contains("age=36h0m", StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(repo, "README.md"), "more\n");
        Assert.Equal(0, Git(repo, "add", "README.md").Code);
    }

    [Fact]
    public void Lock_replaced_during_ownership_probe_is_kept()
    {
        var repo = SeedRepo("replaced");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(1));
        var replacement = new CallbackProbe(() =>
        {
            File.Delete(lockPath);
            File.WriteAllText(lockPath, "new git writer");
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromHours(1));
        });

        var result = Guard(replacement).EnsureWritable(repo);

        Assert.Equal("new git writer", File.ReadAllText(lockPath));
        Assert.Empty(result.Cleared);
        Assert.True(result.Waited);
        Assert.Equal(GitLockVerdict.KeepOwnerUnknown, Assert.Single(result.Remaining).Verdict);
    }

    [Fact]
    public void Unchanged_stale_lock_is_removed_after_ownership_probe()
    {
        var repo = SeedRepo("unchanged");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(1));
        var probe = new CallbackProbe(() => { });

        var result = Guard(probe).Sweep(repo);

        Assert.Equal(1, probe.Calls);
        Assert.False(File.Exists(lockPath));
        Assert.Equal(lockPath, Assert.Single(result.Cleared).Path);
    }

    [Fact]
    public void Fresh_lock_is_respected_after_exactly_one_wait()
    {
        var repo = SeedRepo("fresh");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromMinutes(2));
        var probe = new FixedProbe(GitLockOwnership.None);
        var waits = new List<TimeSpan>();
        var logger = new CapturingLogger<GitStaleLockGuard>();

        var result = Guard(probe, logger, waits).EnsureWritable(repo);

        Assert.True(File.Exists(lockPath));
        Assert.Empty(result.Cleared);
        var kept = Assert.Single(result.Remaining);
        Assert.Equal(GitLockVerdict.KeepYoung, kept.Verdict);
        Assert.True(result.Waited);
        Assert.Single(waits);
        Assert.Equal(0, probe.Calls);
        Assert.Contains(logger.Messages, message =>
            message.StartsWith("git-lock-kept", StringComparison.Ordinal) && message.Contains("reason=young"));
        Assert.DoesNotContain(logger.Messages, message => message.StartsWith("git-stale-lock-cleared", StringComparison.Ordinal));
        // The write then fails exactly as it did before the guard existed.
        Assert.Contains("index.lock", Git(repo, "add", "README.md").Err);
    }

    [Theory]
    [InlineData(GitLockOwnership.Owned, GitLockVerdict.KeepOwned)]
    [InlineData(GitLockOwnership.Unknown, GitLockVerdict.KeepOwnerUnknown)]
    public void Aged_lock_held_or_unprovable_is_kept(GitLockOwnership ownership, GitLockVerdict expected)
    {
        var repo = SeedRepo("owned-" + ownership);
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(4));
        var waits = new List<TimeSpan>();

        var result = Guard(new FixedProbe(ownership), NullLogger<GitStaleLockGuard>.Instance, waits).EnsureWritable(repo);

        Assert.True(File.Exists(lockPath));
        Assert.Equal(expected, Assert.Single(result.Remaining).Verdict);
        Assert.Single(waits);
    }

    [Fact]
    public void Stale_ref_and_packed_refs_locks_are_cleared()
    {
        var repo = SeedRepo("refs");
        var refLock = Path.Combine(repo, ".git", "refs", "heads", "main.lock");
        var packedLock = Path.Combine(repo, ".git", "packed-refs.lock");
        PlantLock(refLock, TimeSpan.FromHours(1));
        PlantLock(packedLock, TimeSpan.FromHours(1));

        var result = Guard(new FixedProbe(GitLockOwnership.None)).EnsureWritable(repo);

        Assert.False(File.Exists(refLock));
        Assert.False(File.Exists(packedLock));
        Assert.Equal(2, result.Cleared.Count);
        File.AppendAllText(Path.Combine(repo, "README.md"), "commit after ref lock\n");
        Assert.Equal(0, Git(repo, "commit", "-q", "-am", "after").Code);
    }

    [Fact]
    public void Linked_worktree_private_index_lock_is_cleared_through_the_worktree_path()
    {
        var repo = SeedRepo("dev");
        var worktree = Path.Combine(_root, "dev-7cad6883");
        Assert.Equal(0, Git(repo, "worktree", "add", "-q", "--detach", worktree).Code);
        var scope = GitStaleLockGuard.ResolveScope(worktree);
        Assert.NotNull(scope);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".git")), scope.CommonDirectory);
        var lockPath = Path.Combine(scope.GitDirectory, "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(92));

        var result = Guard(new FixedProbe(GitLockOwnership.None)).EnsureWritable(worktree);

        Assert.Equal(lockPath, Assert.Single(result.Cleared).Path);
        Assert.Equal(0, Git(worktree, "reset", "--hard", "-q").Code);
    }

    [Fact]
    public async Task Gate_worktree_clears_private_locks_without_touching_project_checkout_index()
    {
        var repo = SeedRepo("gate-source");
        var worktree = Path.Combine(_root, "gate-worktree");
        Assert.Equal(0, Git(repo, "worktree", "add", "-q", "--detach", worktree).Code);
        var scope = GitStaleLockGuard.ResolveScope(worktree)!;
        var gateIndexLock = Path.Combine(scope.GitDirectory, "index.lock");
        var gateHeadLock = Path.Combine(scope.GitDirectory, "HEAD.lock");
        var sourceIndexLock = Path.Combine(repo, ".git", "index.lock");
        var sourceHeadLock = Path.Combine(repo, ".git", "HEAD.lock");
        PlantLock(gateIndexLock, TimeSpan.FromHours(2));
        PlantLock(gateHeadLock, TimeSpan.FromHours(2));
        PlantLock(sourceIndexLock, TimeSpan.FromHours(2));
        PlantLock(sourceHeadLock, TimeSpan.FromHours(2));
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            staleLocks: Guard(new FixedProbe(GitLockOwnership.None)));

        var result = await runner.EnsureGateWorkspaceWritableAsync(worktree, CancellationToken.None);

        Assert.Equal(2, result.Cleared.Count);
        Assert.False(File.Exists(gateIndexLock));
        Assert.False(File.Exists(gateHeadLock));
        Assert.True(File.Exists(sourceIndexLock));
        Assert.True(File.Exists(sourceHeadLock));
        Assert.Equal(0, Git(worktree, "reset", "--hard", "-q").Code);
    }

    [Fact]
    public void Shared_refs_surface_never_touches_the_checkout_index()
    {
        var repo = SeedRepo("project-checkout");
        var indexLock = Path.Combine(repo, ".git", "index.lock");
        var refLock = Path.Combine(repo, ".git", "refs", "heads", "main.lock");
        PlantLock(indexLock, TimeSpan.FromHours(3));
        PlantLock(refLock, TimeSpan.FromHours(3));

        var result = Guard(new FixedProbe(GitLockOwnership.None))
            .EnsureWritable(repo, GitLockSurface.SharedRefs);

        Assert.True(File.Exists(indexLock));
        Assert.False(File.Exists(refLock));
        Assert.Equal(refLock, Assert.Single(result.Cleared).Path);
    }

    [Fact]
    public void Not_a_repository_is_a_clean_no_op()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        var probe = new FixedProbe(GitLockOwnership.None);

        var result = Guard(probe).EnsureWritable(plain);

        Assert.Empty(result.Cleared);
        Assert.Empty(result.Remaining);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        var repo = SeedRepo("threshold");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromMinutes(3));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitStaleLocks:ThresholdMinutes"] = "2" })
            .Build();
        var guard = new GitStaleLockGuard(config, null, new FixedProbe(GitLockOwnership.None), null, _ => { });

        guard.EnsureWritable(repo);

        Assert.Equal(TimeSpan.FromMinutes(2), guard.Threshold);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Workspace_evidence_commit_succeeds_through_a_stale_index_lock()
    {
        var repo = SeedRepo("evidence");
        var watchPath = Path.Combine(repo, "projects", "demo");
        Directory.CreateDirectory(Path.Combine(watchPath, "progress", "ASS-1"));
        File.WriteAllText(Path.Combine(watchPath, "progress", "ASS-1", "run.md"), "evidence\n");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromHours(36));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkspaceEvidence:IndexLockRetryBackoffMs"] = "0" })
            .Build();
        var commit = new WorkspaceArtifactCommitService(
            config,
            NullLogger<WorkspaceArtifactCommitService>.Instance,
            pushQueue: null,
            staleLocks: Guard(new FixedProbe(GitLockOwnership.None)));

        var result = commit.TryCommitEvidence(repo, [watchPath], [], "evidence: 1 transition\n");

        Assert.True(result.DidCommit, result.Error);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Workspace_evidence_commit_still_fails_on_a_fresh_lock()
    {
        var repo = SeedRepo("evidence-fresh");
        var watchPath = Path.Combine(repo, "projects", "demo");
        Directory.CreateDirectory(watchPath);
        File.WriteAllText(Path.Combine(watchPath, "run.md"), "evidence\n");
        var lockPath = Path.Combine(repo, ".git", "index.lock");
        PlantLock(lockPath, TimeSpan.FromMinutes(1));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceEvidence:IndexLockRetryAttempts"] = "1",
            })
            .Build();
        var commit = new WorkspaceArtifactCommitService(
            config,
            NullLogger<WorkspaceArtifactCommitService>.Instance,
            pushQueue: null,
            staleLocks: Guard(new FixedProbe(GitLockOwnership.None)));

        var result = commit.TryCommitEvidence(repo, [watchPath], [], "evidence: 1 transition\n");

        Assert.False(result.Success);
        Assert.StartsWith("git-add:", result.Error);
        Assert.Contains("index.lock", result.Error);
        Assert.True(File.Exists(lockPath));
    }

    // ---- Ownership probe ----------------------------------------------------

    [Fact]
    public void Probe_reports_owned_for_a_live_server_child_in_the_repository()
    {
        var scope = new GitLockScope("/srv/workspace", "/srv/workspace/.git", "/srv/workspace/.git");
        var probe = new GitProcessLockOwnerProbe(() => ["/srv/workspace/projects"], () => []);

        Assert.Equal(GitLockOwnership.Owned, probe.Probe(scope));
    }

    [Fact]
    public void Probe_ignores_git_processes_elsewhere_and_reports_unknown_without_an_inventory()
    {
        var scope = new GitLockScope("/srv/workspace", "/srv/workspace/.git", "/srv/workspace/.git");
        var elsewhere = new GitProcessLockOwnerProbe(
            () => ["/srv/workspace-other"],
            () => [new GitProcessObservation(7, "git", "/srv/workspace-other", "git status")]);
        var unreadable = new GitProcessLockOwnerProbe(() => [], () => null);

        Assert.Equal(GitLockOwnership.None, elsewhere.Probe(scope));
        Assert.Equal(GitLockOwnership.Unknown, unreadable.Probe(scope));
    }

    [Fact]
    public void Probe_matches_a_windows_git_command_line_naming_the_repository()
    {
        var inventory = GitProcessLockOwnerProbe.ParseWindowsInventory(
            "[{\"ProcessId\":4242,\"Name\":\"git.exe\",\"CommandLine\":\"git.exe -C C:\\\\Projects\\\\agent-taskboard-workspace add -A\"}," +
            "{\"ProcessId\":4243,\"Name\":\"git.exe\",\"CommandLine\":null}]");
        var paths = new[] { "C:/Projects/agent-taskboard-workspace" };

        Assert.Equal(2, inventory.Count);
        Assert.True(GitProcessLockOwnerProbe.IsTiedTo(inventory[0], paths));
        Assert.False(GitProcessLockOwnerProbe.IsTiedTo(inventory[1], paths));
    }

    [SkippableFact]
    public void Probe_sees_a_running_git_process_whose_cwd_is_the_repository_on_linux()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the /proc inventory is Linux-only");
        var repo = SeedRepo("live-git");
        var scope = GitStaleLockGuard.ResolveScope(repo)!;
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("hash-object");
        psi.ArgumentList.Add("--stdin");
        using var blocked = Process.Start(psi)!;
        try
        {
            // Started by the test, not through GitNetworkProcessRunner, so it is
            // not in GitChildProcessRegistry: only the /proc inventory can see
            // it - the path for an external or orphaned git process.
            var probe = new GitProcessLockOwnerProbe();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var ownership = probe.Probe(scope);
            while (ownership != GitLockOwnership.Owned && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
                ownership = probe.Probe(scope);
            }
            Assert.Equal(GitLockOwnership.Owned, ownership);
        }
        finally
        {
            blocked.StandardInput.Close();
            blocked.WaitForExit(5_000);
        }
    }

    [Fact]
    public void Server_child_registry_tracks_working_directories_for_the_process_lifetime()
    {
        var cwd = Path.Combine(_root, "tracked-" + Guid.NewGuid().ToString("N"));
        using (GitChildProcessRegistry.Track(cwd))
        {
            Assert.Contains(cwd, GitChildProcessRegistry.WorkingDirectories());
        }
        Assert.DoesNotContain(cwd, GitChildProcessRegistry.WorkingDirectories());
    }

    // ---- helpers ------------------------------------------------------------

    private static GitStaleLockGuard Guard(
        IGitLockOwnerProbe probe,
        ILogger<GitStaleLockGuard>? logger = null,
        List<TimeSpan>? waits = null)
        => new(configuration: null, logger, probe, time: null, wait: span => waits?.Add(span));

    private static void PlantLock(string path, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        Assert.Equal(0, Git(repo, "init", "-q", "-b", "main").Code);
        Git(repo, "config", "user.name", "test");
        Git(repo, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        Git(repo, "add", "README.md");
        Assert.Equal(0, Git(repo, "commit", "-q", "-m", "seed").Code);
        return repo;
    }

    private static (string Out, string Err, int Code) Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (stdout, stderr, process.ExitCode);
    }

    private sealed class FixedProbe(GitLockOwnership ownership) : IGitLockOwnerProbe
    {
        public int Calls { get; private set; }

        public GitLockOwnership Probe(GitLockScope scope)
        {
            Calls++;
            return ownership;
        }
    }

    private sealed class CallbackProbe(Action callback) : IGitLockOwnerProbe
    {
        public int Calls { get; private set; }

        public GitLockOwnership Probe(GitLockScope scope)
        {
            Calls++;
            callback();
            return GitLockOwnership.None;
        }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
