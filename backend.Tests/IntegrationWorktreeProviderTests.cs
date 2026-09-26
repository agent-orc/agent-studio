using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2832: the Studio-owned integration worktree. Integration used to run in
/// the registered project checkout, where a developer's unrelated uncommitted
/// edits refused the merge outright. These tests drive the real provider against
/// throwaway repositories and assert the three properties the integration path
/// depends on: the slot is outside the checkout, it never claims the integration
/// branch, and it starts every integration clean.
/// </summary>
public sealed class IntegrationWorktreeProviderTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationWorktreeProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Resolve_CreatesTheSlotForAProjectThatNeverHadOne()
    {
        var repo = SeedRepo("never-integrated");
        RunGit(repo, "checkout -q -b develop");
        var provider = Provider(repo);

        var resolution = provider.Resolve(repo, "develop");

        Assert.True(resolution.Success, resolution.Error);
        Assert.Equal(IntegrationWorktreeAction.Create, resolution.Action);
        Assert.NotEqual(Path.GetFullPath(repo), Path.GetFullPath(resolution.Path!));
        Assert.False(
            Path.GetFullPath(resolution.Path!).StartsWith(Path.GetFullPath(repo) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the integration worktree must not live inside the developer checkout");
        Assert.True(File.Exists(Path.Combine(resolution.Path!, ".git")));
        Assert.Equal(
            RunGit(repo, "rev-parse develop").Out.Trim(),
            RunGit(resolution.Path!, "rev-parse HEAD").Out.Trim());
    }

    [Fact]
    public void Resolve_LeavesTheIntegrationBranchAvailableToTheDeveloper()
    {
        var repo = SeedRepo("branch-not-claimed");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b feature/local");
        var provider = Provider(repo);

        var resolution = provider.Resolve(repo, "develop");

        Assert.True(resolution.Success, resolution.Error);
        // Detached: a linked worktree that checked the branch out would make
        // "git checkout develop" impossible in the developer's own checkout.
        Assert.Equal("HEAD", RunGit(resolution.Path!, "rev-parse --abbrev-ref HEAD").Out.Trim());
        Assert.Equal(0, RunGit(repo, "checkout -q develop").Code);
    }

    [Fact]
    public void Resolve_ReusesTheSlotAndDropsWhatThePreviousIntegrationLeft()
    {
        var repo = SeedRepo("reused-slot");
        RunGit(repo, "checkout -q -b develop");
        var provider = Provider(repo);
        var first = provider.Resolve(repo, "develop");
        Assert.True(first.Success, first.Error);
        File.WriteAllText(Path.Combine(first.Path!, "README.md"), "half-finished merge");
        File.WriteAllText(Path.Combine(first.Path!, "stray.txt"), "left behind");

        var second = provider.Resolve(repo, "develop");

        Assert.True(second.Success, second.Error);
        Assert.Equal(IntegrationWorktreeAction.Reuse, second.Action);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(string.Empty, RunGit(second.Path!, "status --porcelain").Out.Trim());
        Assert.False(File.Exists(Path.Combine(second.Path!, "stray.txt")));
    }

    [Fact]
    public void Resolve_RemovesOldOwnedIndexLockAndResetsToCurrentIntegrationTip()
    {
        var repo = SeedRepo("stale-index-lock");
        RunGit(repo, "checkout -q -b develop");
        var provider = Provider(repo);
        var first = provider.Resolve(repo, "develop");
        Assert.True(first.Success, first.Error);

        File.WriteAllText(Path.Combine(repo, "README.md"), "new integration tip");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m advance");
        var pointer = File.ReadAllText(Path.Combine(first.Path!, ".git")).Trim()[7..].Trim();
        var gitDir = Path.IsPathRooted(pointer)
            ? pointer
            : Path.GetFullPath(Path.Combine(first.Path!, pointer));
        var lockPath = Path.Combine(gitDir, "index.lock");
        File.WriteAllText(lockPath, "orphaned reset");
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(Path.Combine(gitDir, IntegrationWorktreeProvider.LastIntegrationMarker),
            DateTimeOffset.UtcNow.ToString("O"));

        var second = provider.Resolve(repo, "develop");

        Assert.True(second.Success, second.Error);
        Assert.False(File.Exists(lockPath));
        Assert.Equal(RunGit(repo, "rev-parse develop").Out.Trim(),
            RunGit(second.Path!, "rev-parse HEAD").Out.Trim());
        Assert.Equal(string.Empty, RunGit(second.Path!, "status --porcelain").Out.Trim());
    }

    /// <summary>
    /// A crashed integration leaves a conflicted merge in the worktree. The next
    /// integration must not inherit it: git would refuse to switch while the
    /// index is half-resolved.
    /// </summary>
    [Fact]
    public void Resolve_ClearsAMergeAnInterruptedIntegrationLeftBehind()
    {
        var repo = SeedRepo("interrupted-integration");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop side");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m \"feat: develop side\"");
        RunGit(repo, "checkout -q -b task/conflict HEAD~1");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "task side");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m \"feat: task side\"");
        RunGit(repo, "checkout -q develop");
        var provider = Provider(repo);
        var first = provider.Resolve(repo, "develop");
        Assert.True(first.Success, first.Error);
        Assert.NotEqual(0, RunGit(first.Path!, "merge --no-commit task/conflict").Code);

        var second = provider.Resolve(repo, "develop");

        Assert.True(second.Success, second.Error);
        Assert.Equal(string.Empty, RunGit(second.Path!, "status --porcelain").Out.Trim());
        Assert.NotEqual(0, RunGit(second.Path!, "rev-parse --verify --quiet MERGE_HEAD").Code);
    }

    [Fact]
    public void Resolve_RecreatesTheSlotAfterItsDirectoryDisappeared()
    {
        var repo = SeedRepo("vanished-slot");
        RunGit(repo, "checkout -q -b develop");
        var provider = Provider(repo);
        var first = provider.Resolve(repo, "develop");
        Assert.True(first.Success, first.Error);
        Directory.Delete(first.Path!, recursive: true);

        var second = provider.Resolve(repo, "develop");

        Assert.True(second.Success, second.Error);
        Assert.Equal(IntegrationWorktreeAction.Recreate, second.Action);
        Assert.Equal(first.Path, second.Path);
        Assert.True(Directory.Exists(second.Path!));
        var registrations = RunGit(repo, "worktree list --porcelain").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));
        Assert.Equal(2, registrations);
    }

    [Fact]
    public void Resolve_FailsClosedInsteadOfHandingBackTheDeveloperCheckout()
    {
        var notARepo = Path.Combine(_tempDir, "not-a-repo");
        Directory.CreateDirectory(notARepo);
        var provider = Provider(notARepo);

        var resolution = provider.Resolve(notARepo, "develop");

        Assert.False(resolution.Success);
        Assert.Null(resolution.Path);
        Assert.Contains("integration worktree", resolution.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private IntegrationWorktreeProvider Provider(string repo)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new IntegrationWorktreeProvider(
            git,
            NullLogger<IntegrationWorktreeProvider>.Instance,
            Path.Combine(_tempDir, "fallback"));
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
