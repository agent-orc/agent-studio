using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3409: on 2026-11-01 an evidence flush's <c>git add</c> hit the 30-second
/// limit, the runner killed it, and the <c>index.lock</c> it left blocked every
/// workspace write for 70 minutes. These tests kill real git processes in real
/// repositories. A clean filter keeps git busy at a known point: a FIFO pins
/// the live-writer test, and a timeout pins the killed-add test. <c>git add</c>
/// holds <c>index.lock</c> while it runs, while <c>git hash-object</c> runs the
/// filter without taking any lock.
/// </summary>
public sealed class GitKilledCommandLockTests : IDisposable
{
    private readonly string _root;

    public GitKilledCommandLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "git-killed-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [SkippableFact]
    public void Killed_git_add_leaves_no_index_lock_and_the_next_write_succeeds()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "SIGKILL and the /proc ownership inventory are exercised on Linux");
        var repo = SeedSlowFilterRepo("killed-add");
        var lockPath = Path.Combine(repo, ".git", "index.lock");

        var killed = GitProcessBoundary.Run(
            Git(repo, "-c", "filter.slow.clean=sleep 30; cat", "add", "-A"), timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(GitProcessFailureKind.TimedOut, killed.FailureKind);
        if (File.Exists(lockPath))
        {
            // A shared host can have unrelated git processes whose cwd is
            // unreadable. The production probe must keep the lock when that
            // inventory is inconclusive; this repository is private to the
            // test, so pin the no-owner fact before checking the next write.
            var written = File.GetLastWriteTimeUtc(lockPath);
            var scope = GitStaleLockGuard.ResolveScope(repo)!;
            var owner = new GitProcessLockOwnerProbe().Probe(scope, GitStaleLockPolicy.OwnerStartedBefore(written));
            Assert.NotEqual(GitLockOwnership.Owned, owner);
            var swept = new GitStaleLockGuard(probe: new NoGitOwnerProbe()).Sweep(repo);
            Assert.Contains(swept.Cleared, finding => finding.Path == lockPath);
        }
        Assert.False(File.Exists(lockPath), "the killed add's index.lock must be removed");
        var next = GitProcessBoundary.Run(Git(repo, "add", "-A"), timeout: TimeSpan.FromSeconds(30));
        Assert.True(next.Success, next.StandardError);
    }

    [SkippableFact]
    public async Task Lock_held_by_a_live_git_process_stays_and_clears_on_the_next_write_once_it_exits()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "SIGKILL and the /proc ownership inventory are exercised on Linux");
        var repo = SeedSlowFilterRepo("live-owner");
        var lockPath = Path.Combine(repo, ".git", "index.lock");

        // Each filter announces that git reached it, then waits on a FIFO.
        // Cancellation begins only after the external writer owns index.lock.
        using var killedReady = new ManualResetEventSlim();
        using var liveReady = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(_root) { Filter = "*.ready" };
        watcher.Created += (_, change) =>
        {
            if (change.Name == "killed.ready") killedReady.Set();
            if (change.Name == "live.ready") liveReady.Set();
        };
        watcher.EnableRaisingEvents = true;
        var killedCommand = BlockedFilterGit(repo, "killed", "hash-object", "a.slow");
        var liveCommand = BlockedFilterGit(repo, "live", "add", "-A");
        using var cancel = new CancellationTokenSource();
        var killedTask = Task.Run(() => GitProcessBoundary.Run(
            killedCommand, timeout: TimeSpan.FromMinutes(1), cancellationToken: cancel.Token));
        Process? liveWriter = null;
        try
        {
            Assert.True(killedReady.Wait(TimeSpan.FromSeconds(10)), "the server git filter did not start");
            // Started outside the boundary: only the OS inventory can see it.
            liveWriter = Process.Start(liveCommand)!;
            Assert.True(liveReady.Wait(TimeSpan.FromSeconds(10)), "the live writer filter did not start");
            Assert.True(File.Exists(lockPath), "the live writer did not take index.lock");
            cancel.Cancel();

            var killed = await killedTask;

            Assert.Equal(GitProcessFailureKind.Cancelled, killed.FailureKind);
            Assert.True(File.Exists(lockPath), "a lock a live git process holds must stay");
            var guard = new GitStaleLockGuard();
            var whileAlive = guard.Sweep(repo);
            Assert.Empty(whileAlive.Cleared);
            Assert.Contains(whileAlive.Remaining, finding =>
                finding.Path == lockPath && finding.Verdict == GitLockVerdict.KeepOwned);

            // Kill git itself so a filter child cannot finish first and let
            // git change or release the lock during process-tree teardown.
            liveWriter.Kill();
            Assert.True(liveWriter.WaitForExit(10_000));
            Assert.True(File.Exists(lockPath), "SIGKILL leaves the writer's lock behind");

            // The lock is seconds old, below the one-minute threshold, yet
            // the next write clears it: its writer died during a killed command.
            // WaitForExit established that this writer is gone. A host-wide
            // /proc scan can still be inconclusive when unrelated git
            // processes churn, so pin the no-owner fact for this decision.
            var afterExit = new GitStaleLockGuard(probe: new NoGitOwnerProbe()).Sweep(repo);
            Assert.Contains(afterExit.Cleared, finding => finding.Path == lockPath);
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            cancel.Cancel();
            if (liveWriter is not null)
            {
                if (!liveWriter.HasExited) liveWriter.Kill();
                liveWriter.WaitForExit(10_000);
                // The orphaned filter has a FIFO reader. Opening read/write
                // releases it without waiting for a separate writer process.
                using var release = new FileStream(Path.Combine(_root, "live.fifo"),
                    FileMode.Open, FileAccess.ReadWrite);
                release.WriteByte(0);
                liveWriter.Dispose();
            }
            await killedTask;
        }
    }

    [Fact]
    public void Command_directory_follows_dash_C_and_skips_an_explicit_git_dir()
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add("repo");
        start.ArgumentList.Add("status");
        Assert.Equal(Path.Combine(_root, "repo"), GitProcessBoundary.CommandDirectory(start));

        var bare = new ProcessStartInfo("git") { WorkingDirectory = _root };
        bare.ArgumentList.Add("--git-dir=" + Path.Combine(_root, "bare.git"));
        bare.ArgumentList.Add("fetch");
        Assert.Null(GitProcessBoundary.CommandDirectory(bare));
    }

    [Fact]
    public void Enclosing_scope_is_found_from_a_subdirectory()
    {
        var repo = Path.Combine(_root, "nested");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = Path.Combine(repo, "projects", "a");
        Directory.CreateDirectory(sub);

        var scope = GitStaleLockGuard.ResolveEnclosingScope(sub);

        Assert.NotNull(scope);
        Assert.Equal(Path.GetFullPath(repo), scope!.WorkTree);
        Assert.Null(GitStaleLockGuard.ResolveEnclosingScope(Path.Combine(_root, "elsewhere")));
    }

    private string SeedSlowFilterRepo(string name)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        RunToEnd(Git(repo, "init", "-q", "-b", "main"));
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.slow filter=slow\n");
        File.WriteAllText(Path.Combine(repo, "a.slow"), "slow\n");
        return repo;
    }

    private static ProcessStartInfo Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    private static void RunToEnd(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        Assert.Equal(0, process.ExitCode);
    }

    private ProcessStartInfo BlockedFilterGit(string repo, string signal, params string[] command)
    {
        var fifo = new ProcessStartInfo("mkfifo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        fifo.ArgumentList.Add(Path.Combine(_root, signal + ".fifo"));
        RunToEnd(fifo);
        var filter = $"filter.slow.clean=printf ready > \"$FILTER_SIGNAL_DIR/{signal}.ready\"; "
            + $"exec cat < \"$FILTER_SIGNAL_DIR/{signal}.fifo\"";
        var psi = Git(repo, ["-c", filter, .. command]);
        psi.Environment["FILTER_SIGNAL_DIR"] = _root;
        return psi;
    }

    private sealed class NoGitOwnerProbe : IGitLockOwnerProbe
    {
        public GitLockOwnership Probe(GitLockScope scope, DateTime lockLastWriteTimeUtc)
            => GitLockOwnership.None;
    }

}
