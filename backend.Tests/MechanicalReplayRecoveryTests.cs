using System.Diagnostics;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MechanicalReplayRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mechanical-replay-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public MechanicalReplayRecoveryTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        Git(_root, "init", "-q", "-b", "main", _repo);
        Git(_repo, "config", "user.name", "Test");
        Git(_repo, "config", "user.email", "test@example.invalid");
        Commit("base.txt", "base\n", "base");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cleanup of {_root} failed: {ex.Message}");
        }
    }

    [Fact]
    public void LinearReplay_WritesMappedCommitsWithoutCreatingAWorktree()
    {
        Git(_repo, "checkout", "-q", "-b", "delivery");
        var first = Commit("first.txt", "first\n", "first delivery");
        var second = Commit("second.txt", "second\n", "second delivery");
        Git(_repo, "checkout", "-q", "main");
        var integrationTip = Commit("lane.txt", "lane\n", "lane moved");
        var before = Git(_repo, "worktree", "list", "--porcelain");

        var result = Service().TryMechanicalRebase(_repo, "delivery", integrationTip);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Replacements.Count);
        Assert.Equal([first, second], result.Replacements.Select(item => item.OriginalSha));
        Assert.Equal(result.RebasedTip, result.Replacements[^1].RebasedSha);
        Assert.Equal(integrationTip, Git(_repo, "rev-parse", result.Replacements[0].RebasedSha + "^"));
        Assert.Equal("first\n", Git(_repo, "show", result.RebasedTip + ":first.txt") + "\n");
        Assert.Equal("second\n", Git(_repo, "show", result.RebasedTip + ":second.txt") + "\n");
        Assert.Equal("lane\n", Git(_repo, "show", result.RebasedTip + ":lane.txt") + "\n");
        Assert.Equal(before, Git(_repo, "worktree", "list", "--porcelain"));
    }

    [Fact]
    public void Replay_PreservesAnEmptyCommitWhenLaneAlreadyHasItsChange()
    {
        Git(_repo, "checkout", "-q", "-b", "delivery");
        var original = Commit("shared.txt", "same\n", "delivery change");
        Git(_repo, "checkout", "-q", "main");
        var integrationTip = Commit("shared.txt", "same\n", "same lane change");

        var result = Service().TryMechanicalRebase(_repo, "delivery", integrationTip);

        Assert.True(result.Success, result.Error);
        Assert.Equal(original, Assert.Single(result.Replacements).OriginalSha);
        Assert.Equal(integrationTip, Git(_repo, "rev-parse", result.RebasedTip + "^"));
        Assert.Equal(Git(_repo, "rev-parse", integrationTip + "^{tree}"),
            Git(_repo, "rev-parse", result.RebasedTip + "^{tree}"));
    }

    [Fact]
    public void LeftoverTemporaryRegistration_DoesNotBlockSuccessfulReplay()
    {
        Git(_repo, "checkout", "-q", "-b", "delivery");
        Commit("delivery.txt", "delivery\n", "delivery change");
        Git(_repo, "checkout", "-q", "main");
        var integrationTip = Commit("lane.txt", "lane\n", "lane change");
        var entry = Path.Combine(_repo, ".git", "worktrees", "old-rebase");
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(_root, "old-rebase", ".git") + "\n");
        File.WriteAllText(Path.Combine(entry, "locked"), "initializing");

        var result = Service().TryMechanicalRebase(_repo, "delivery", integrationTip);

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(entry));
    }

    [Fact]
    public void KilledAdd_RemovesItsLockedInitializingRegistrationAndIndexLock()
    {
        var container = Path.Combine(_root, "agent-studio-integration-rebase-test");
        var worktree = Path.Combine(container, "worktree");
        Directory.CreateDirectory(container);
        var entry = Path.Combine(_repo, ".git", "worktrees", "worktree1");
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(worktree, ".git") + "\n");
        File.WriteAllText(Path.Combine(entry, "locked"), "initializing");
        File.WriteAllText(Path.Combine(entry, "index.lock"), "");

        var error = Service().CleanupMechanicalRebaseWorktree(_repo, container, worktree, added: false);

        Assert.Null(error);
        Assert.False(Directory.Exists(entry));
        Assert.False(Directory.Exists(container));
    }

    [Fact]
    public void WorktreeRemoval_RetriesTwiceAndThenSucceeds()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();

        var removed = GitService.RemoveMechanicalWorktreeWithRetry(
            () => ++calls == 3, waits.Add);

        Assert.True(removed);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], waits);
    }

    private GitService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private string Commit(string path, string content, string message)
    {
        File.WriteAllText(Path.Combine(_repo, path), content);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", message);
        return Git(_repo, "rev-parse", "HEAD");
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output.Result.Trim();
    }
}
