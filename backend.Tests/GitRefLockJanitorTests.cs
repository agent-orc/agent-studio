using System.Diagnostics;
using AgentStudio.Bus;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3337: a fetch killed by the 30-second git timeout left hundreds of
/// <c>refs/**/*.lock</c> files; one empty <c>refs/remotes/origin/develop.lock</c>
/// blocked every update of origin/develop for 90 minutes. The janitor removes
/// such locks once they are older than its threshold, only while no git process
/// of this server runs in the repository, and records every removal as an
/// operations event.
/// </summary>
public sealed class GitRefLockJanitorTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly string _root;
    private readonly string _repo;
    private readonly string _gitDir;
    private readonly string _events;
    private readonly AgentMessageBusStore _store = new();

    public GitRefLockJanitorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-ref-lock-janitor-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _gitDir = Path.Combine(_repo, ".git");
        _events = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(_gitDir, "refs", "remotes", "origin"));
        Directory.CreateDirectory(_events);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort; a locked file must not fail the test.
            Console.Error.WriteLine($"cleanup of {_root} failed: {ex.Message}");
        }
    }

    [Fact]
    public void BeforeFetch_RemovesOnlyLocksOlderThanTheThreshold_AndRecordsAnOperationsEvent()
    {
        var oldRef = Lock("refs/remotes/origin/develop.lock", TimeSpan.FromMinutes(6));
        var oldPacked = Lock("packed-refs.lock", TimeSpan.FromMinutes(90));
        var youngRef = Lock("refs/remotes/origin/feature.lock", TimeSpan.FromMinutes(4));
        var index = Lock("index.lock", TimeSpan.FromHours(3));
        var janitor = Janitor(OwnServerChildrenOnly());

        var result = janitor.BeforeFetch(_repo);

        Assert.False(File.Exists(oldRef));
        Assert.False(File.Exists(oldPacked));
        Assert.True(File.Exists(youngRef));
        // The checkout's own index is not part of the shared ref store.
        Assert.True(File.Exists(index));
        Assert.Equal(2, result.Cleared.Count);
        var message = Assert.Single(Events(), m => m.Topic == "git-stale-lock-cleared");
        Assert.Equal("intervention", message.Kind);
        Assert.Contains(oldRef, message.Body);
        Assert.Contains(oldPacked, message.Body);
        Assert.DoesNotContain(youngRef, message.Body);
    }

    [Fact]
    public void BeforeFetch_NeverRemovesALockWhileAnOwnGitProcessRunsInTheRepository()
    {
        var oldRef = Lock("refs/remotes/origin/develop.lock", TimeSpan.FromHours(2));
        var janitor = Janitor(OwnServerChildrenOnly());

        using (GitChildProcessRegistry.Track(_repo))
        {
            var kept = janitor.BeforeFetch(_repo);
            Assert.True(File.Exists(oldRef));
            Assert.Empty(kept.Cleared);
            Assert.Equal(GitLockVerdict.KeepOwned, Assert.Single(kept.Remaining).Verdict);
            Assert.DoesNotContain(Events(), m => m.Topic == "git-stale-lock-cleared");
        }

        var cleared = janitor.BeforeFetch(_repo);
        Assert.False(File.Exists(oldRef));
        Assert.Single(cleared.Cleared);
    }

    [Fact]
    public void AfterFetchTimeout_KeepsTheKilledFetchsYoungLocks_AndReportsThemOncePerWindow()
    {
        var young = Lock("refs/remotes/origin/develop.lock", TimeSpan.FromSeconds(30));
        var janitor = Janitor(OwnServerChildrenOnly());
        const string timeout = "git operation timed out after 30 seconds";

        var first = janitor.AfterFetchFailure(_repo, timeout);
        var second = janitor.AfterFetchFailure(_repo, timeout);

        Assert.True(File.Exists(young));
        Assert.True(first.Reported);
        Assert.False(second.Reported);
        var blocked = Assert.Single(Events(), m => m.Topic == "git-ref-lock-blocked");
        Assert.Contains("after-timeout", blocked.Summary);
        Assert.Contains(young, blocked.Body);

        // Past the threshold the next pass removes the lock the killed fetch left.
        _clock.Advance(TimeSpan.FromMinutes(6));
        var later = janitor.AfterFetchFailure(_repo, timeout);
        Assert.False(File.Exists(young));
        Assert.Single(later.Cleared);
    }

    [Fact]
    public void CannotLockRefFailure_IsAVisibleOperationsEvent()
    {
        var janitor = Janitor(OwnServerChildrenOnly());

        var result = janitor.AfterFetchFailure(_repo,
            "error: cannot lock ref 'refs/remotes/origin/develop': Unable to create '.git/refs/remotes/origin/develop.lock': File exists.");

        Assert.True(result.Reported);
        var blocked = Assert.Single(Events(), m => m.Topic == "git-ref-lock-blocked");
        Assert.Contains("cannot-lock-ref", blocked.Summary);
    }

    [Fact]
    public void OfflineFetchFailure_IsNotALockCondition()
    {
        var oldRef = Lock("refs/remotes/origin/develop.lock", TimeSpan.FromHours(2));
        var janitor = Janitor(OwnServerChildrenOnly());

        var result = janitor.AfterFetchFailure(_repo, "fatal: unable to access 'https://example.invalid/': Could not resolve host");

        Assert.Same(GitRefLockJanitorResult.Clean, result);
        Assert.True(File.Exists(oldRef));
        Assert.Empty(Events());
    }

    [Fact]
    public void BeforeFetch_PrunesWorktreeEntriesWhoseWorkingTreeIsGone_OnlyWhenNoOwnGitRuns()
    {
        RunGit(_root, "init", "-q", _repo);
        var orphan = WorktreeEntry("worktree1", Path.Combine(_root, "agent-studio-batch-merge-1"), TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(orphan, "index.lock"), "");
        Directory.SetLastWriteTimeUtc(orphan, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));
        var liveTree = Path.Combine(_root, "live-tree");
        Directory.CreateDirectory(liveTree);
        Directory.CreateDirectory(Path.Combine(liveTree, ".git"));
        var live = WorktreeEntry("worktree2", liveTree, TimeSpan.FromHours(1));
        var locked = WorktreeEntry("worktree3", Path.Combine(_root, "unmounted"), TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(locked, "locked"), "on removable disk");
        var fresh = WorktreeEntry("worktree4", Path.Combine(_root, "being-created"), TimeSpan.FromMinutes(1));
        var janitor = Janitor(OwnServerChildrenOnly());

        using (GitChildProcessRegistry.Track(_repo))
        {
            Assert.Empty(janitor.BeforeFetch(_repo).PrunedWorktrees);
            Assert.True(Directory.Exists(orphan));
        }

        var result = janitor.BeforeFetch(_repo);

        // Git prunes a missing gitdir even when its registration is recent;
        // --expire only controls registrations Git treats as expired.
        Assert.Equal(new[] { orphan, fresh }, result.PrunedWorktrees.OrderBy(path => path, StringComparer.Ordinal));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(locked));
        Assert.False(Directory.Exists(fresh));
        var messages = Events().Where(m => m.Topic == "git-stale-lock-cleared").ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Contains(messages, message => message.Body.Contains(orphan, StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Body.Contains(fresh, StringComparison.Ordinal));
    }

    [Fact]
    public void BeforeFetch_RemovesOnlyOldMissingInitializingEntriesWithoutAnOwnGitProcess()
    {
        RunGit(_root, "init", "-q", _repo);
        var old = InitializingEntry("old", Path.Combine(_root, "missing-old"), TimeSpan.FromMinutes(20));
        File.WriteAllText(Path.Combine(old, "index.lock"), "");
        Directory.SetLastWriteTimeUtc(old, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(20));
        var fresh = InitializingEntry("fresh", Path.Combine(_root, "missing-fresh"), TimeSpan.FromMinutes(2));
        var livePath = Path.Combine(_root, "live");
        Directory.CreateDirectory(livePath);
        File.WriteAllText(Path.Combine(livePath, ".git"), "gitdir: elsewhere");
        var live = InitializingEntry("live", livePath, TimeSpan.FromMinutes(20));
        var other = WorktreeEntry("other", Path.Combine(_root, "missing-other"), TimeSpan.FromMinutes(20));
        File.WriteAllText(Path.Combine(other, "locked"), "on removable disk");
        var janitor = Janitor(OwnServerChildrenOnly());

        using (GitChildProcessRegistry.Track(_repo))
        {
            Assert.Empty(janitor.BeforeFetch(_repo).PrunedWorktrees);
            Assert.True(Directory.Exists(old));
        }

        var result = janitor.BeforeFetch(_repo);

        Assert.Equal([old], result.PrunedWorktrees);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(other));
        var message = Assert.Single(Events(), m => m.Topic == "git-stale-lock-cleared");
        Assert.Contains(old, message.Body);
    }

    [Fact]
    public void GitServiceFetch_ClearsAStaleRefLockLeftByAKilledFetch_BeforeFetching()
    {
        var remote = Path.Combine(_root, "origin.git");
        var repo = Path.Combine(_root, "clone");
        RunGit(_root, "init", "--bare", "-q", "--initial-branch=main", remote);
        RunGit(_root, "init", "-q", "-b", "main", repo);
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "seed");
        RunGit(repo, "remote", "add", "origin", remote);
        RunGit(repo, "push", "-q", "-u", "origin", "main");
        // Advance the remote behind the mirror's back, so the fetch must update
        // refs/remotes/origin/main and therefore take its lock.
        File.WriteAllText(Path.Combine(repo, "README.md"), "next");
        RunGit(repo, "commit", "-q", "-am", "next");
        RunGit(repo, "push", "-q", remote, "main");
        var stale = Path.Combine(repo, ".git", "refs", "remotes", "origin", "main.lock");
        File.WriteAllText(stale, "");
        File.SetLastWriteTimeUtc(stale, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(20));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _events,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(
            NullLogger<GitService>.Instance, scanner, config,
            lockJanitor: Janitor(OwnServerChildrenOnly()));

        var fetched = git.Fetch(repo);

        Assert.True(fetched.Success);
        Assert.Null(fetched.Error);
        Assert.False(File.Exists(stale));
        Assert.Single(Events(), m => m.Topic == "git-stale-lock-cleared");
        Assert.Equal(ReadRef(repo, "refs/heads/main"), ReadRef(repo, "refs/remotes/origin/main"));
    }

    private GitRefLockJanitor Janitor(IGitLockOwnerProbe probe)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _events,
        }).Build();
        var bus = new AgentMessageBusBridge(_store, config, NullLogger<AgentMessageBusBridge>.Instance, _clock);
        return new GitRefLockJanitor(config, NullLogger<GitRefLockJanitor>.Instance, bus, probe: probe, time: _clock);
    }

    /// <summary>Only this server's own git children count; no OS inventory, so other test processes cannot interfere.</summary>
    private static IGitLockOwnerProbe OwnServerChildrenOnly()
        => new GitProcessLockOwnerProbe(GitChildProcessRegistry.WorkingDirectories, () => []);

    private IReadOnlyList<AgentMessage> Events() => _store.Recent(_events, project: null, limit: 50);

    private string Lock(string relative, TimeSpan age)
    {
        var path = Path.Combine(_gitDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime - age);
        return path;
    }

    private string WorktreeEntry(string name, string workTree, TimeSpan age)
    {
        var entry = Path.Combine(_gitDir, "worktrees", name);
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(workTree, ".git") + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(entry, "gitdir"), _clock.GetUtcNow().UtcDateTime - age);
        Directory.SetLastWriteTimeUtc(entry, _clock.GetUtcNow().UtcDateTime - age);
        return entry;
    }

    private string InitializingEntry(string name, string workTree, TimeSpan age)
    {
        var entry = WorktreeEntry(name, workTree, age);
        var locked = Path.Combine(entry, "locked");
        File.WriteAllText(locked, "initializing");
        File.SetLastWriteTimeUtc(locked, _clock.GetUtcNow().UtcDateTime - age);
        Directory.SetLastWriteTimeUtc(entry, _clock.GetUtcNow().UtcDateTime - age);
        return entry;
    }

    private static string ReadRef(string repo, string name)
        => File.ReadAllText(Path.Combine(repo, ".git", name.Replace('/', Path.DirectorySeparatorChar))).Trim();

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout.Result} {stderr}");
    }
}
