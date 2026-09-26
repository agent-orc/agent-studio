using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GitConfigSignatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "git-config-index-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IncludedAndWorktreeConfigurationChangesAdvanceTheEffectiveSignature()
    {
        var main = Path.Combine(_root, "main");
        var worktree = Path.Combine(_root, "linked");
        Directory.CreateDirectory(main);
        RunGit(main, "init", "-q");
        RunGit(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "seed");
        var included = Path.Combine(_root, "extra.conf");
        File.WriteAllText(included, "[remote \"origin\"]\nurl = https://example.invalid/one.git\n");
        RunGit(main, "config", "--local", "include.path", included);
        var first = GitConfigSignature.Capture(main);
        Assert.True(GitConfigSignature.FilesUnchanged(first));
        File.WriteAllText(included, "[remote \"origin\"]\nurl = https://example.invalid/two.git\n");
        Assert.False(GitConfigSignature.FilesUnchanged(first));
        var changedInclude = GitConfigSignature.Capture(main);
        Assert.NotEqual(first.Signature, changedInclude.Signature);
        Assert.Equal("https://example.invalid/two.git", changedInclude.OriginUrl);

        RunGit(main, "config", "extensions.worktreeConfig", "true");
        RunGit(main, "worktree", "add", "--detach", "-q", worktree);
        RunGit(worktree, "config", "--worktree", "remote.origin.url",
            "https://example.invalid/worktree-one.git");
        var beforeWorktreeChange = GitConfigSignature.Capture(worktree);
        Assert.True(GitConfigSignature.FilesUnchanged(beforeWorktreeChange));
        RunGit(worktree, "config", "--worktree", "remote.origin.url",
            "https://example.invalid/worktree-two.git");
        Assert.False(GitConfigSignature.FilesUnchanged(beforeWorktreeChange));
        var changedWorktree = GitConfigSignature.Capture(worktree);
        Assert.NotEqual(beforeWorktreeChange.Signature, changedWorktree.Signature);
        Assert.Equal("https://example.invalid/worktree-two.git", changedWorktree.OriginUrl);
    }

    [Fact]
    public void PackedRefsInTheCommonGitDirectoryAdvanceAWorktreeSignature()
    {
        var main = Path.Combine(_root, "main");
        var worktree = Path.Combine(_root, "linked");
        Directory.CreateDirectory(main);
        RunGit(main, "init", "-q");
        RunGit(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "seed");
        RunGit(main, "worktree", "add", "--detach", "-q", worktree);
        var before = GitRefSignature.Capture(worktree);
        RunGit(main, "branch", "feature");
        RunGit(main, "pack-refs", "--all");
        Assert.NotEqual(before, GitRefSignature.Capture(worktree));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
