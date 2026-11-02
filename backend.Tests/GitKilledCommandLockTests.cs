using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3409: on 2026-11-01 an evidence flush's <c>git add</c> hit the 30-second
/// limit, the runner killed it, and the <c>index.lock</c> it left blocked every
/// workspace write for 70 minutes. These tests kill real git processes in real
/// repositories. A clean filter that sleeps keeps git busy at a known point:
/// <c>git add</c> holds <c>index.lock</c> while it runs, <c>git hash-object</c>
/// runs the filter without taking any lock.
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

        // The server's command runs (and is killed) while another git writes.
        var killedTask = Task.Run(() => GitProcessBoundary.Run(
            Git(repo, "-c", "filter.slow.clean=sleep 30; cat", "hash-object", "a.slow"),
            timeout: TimeSpan.FromSeconds(4)));
        Assert.True(WaitUntil(() => GitChildProcessRegistry.WorkingDirectories().Contains(repo)),
            "the server command did not start");

        // Started outside the boundary: only the OS inventory can see it.
        using var liveWriter = Process.Start(Git(repo, "-c", "filter.slow.clean=sleep 60; cat", "add", "-A"))!;
        try
        {
            Assert.True(WaitUntil(() => File.Exists(lockPath)), "the live writer did not take index.lock");

            var killed = await killedTask;

            Assert.Equal(GitProcessFailureKind.TimedOut, killed.FailureKind);
            Assert.True(File.Exists(lockPath), "a lock a live git process holds must stay");
            var guard = new GitStaleLockGuard();
            var whileAlive = guard.Sweep(repo);
            Assert.Empty(whileAlive.Cleared);
            Assert.Contains(whileAlive.Remaining, finding =>
                finding.Path == lockPath && finding.Verdict == GitLockVerdict.KeepOwned);

            liveWriter.Kill(entireProcessTree: true);
            liveWriter.WaitForExit(10_000);
            Assert.True(File.Exists(lockPath), "SIGKILL leaves the writer's lock behind");

            // The lock is seconds old, far below the 10-minute threshold, yet
            // the next write clears it: its writer died during a killed command.
            var afterExit = guard.Sweep(repo);
            Assert.Contains(afterExit.Cleared, finding => finding.Path == lockPath);
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            if (!liveWriter.HasExited) liveWriter.Kill(entireProcessTree: true);
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

    private static bool WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) return false;
            Thread.Sleep(20);
        }
        return true;
    }
}
