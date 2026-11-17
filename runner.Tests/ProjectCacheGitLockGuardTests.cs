using AgentRunner;
using AgentStudio.Git;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProjectCacheGitLockGuardTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "runner-lock-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Lock_owned_by_live_git_process_stays_after_one_wait()
    {
        var path = Path.Combine(_repo, ".git", "objects", "info", "commit-graphs", "commit-graph-chain.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        var waits = 0;
        var guard = new ProjectCacheGitLockGuard((_, _) => GitLockOwnership.Owned,
            wait: (_, _) => { waits++; return Task.CompletedTask; });

        await guard.EnsureWritableAsync(_repo, _ => { }, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task Running_git_in_cache_keeps_old_lock()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_repo, ".git", "objects", "maintenance.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("hash-object");
        start.ArgumentList.Add("--stdin");
        using var git = Process.Start(start)!;
        try
        {
            File.WriteAllBytes(path, []);
            var clock = new FakeTimeProvider(new DateTimeOffset(
                File.GetLastWriteTimeUtc(path), TimeSpan.Zero).AddMinutes(5));
            await new ProjectCacheGitLockGuard(time: clock, wait: (_, _) => Task.CompletedTask)
                .EnsureWritableAsync(_repo, _ => { }, CancellationToken.None);
            Assert.True(File.Exists(path));
        }
        finally
        {
            git.StandardInput.Close();
            await git.WaitForExitAsync();
        }
    }

    public void Dispose() { if (Directory.Exists(_repo)) Directory.Delete(_repo, true); }
}
