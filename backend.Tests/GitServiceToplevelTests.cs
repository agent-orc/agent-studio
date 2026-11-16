using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// A configured project RootPath may sit one or more levels below the actual
/// git work-tree (e.g. <c>C:\Projects\Runbook\App</c> inside a repo at
/// <c>C:\Projects\Runbook</c>). The Git view must still recognise it as a
/// repository; git itself does, via <c>rev-parse --show-toplevel</c>.
/// </summary>
public class GitServiceToplevelTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceToplevelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-toplevel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // .git contains readonly pack files on Windows; clear before deleting.
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetSummaries_RootPathIsSubfolderOfGitRepo_IsReportedAsRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var subfolder = Path.Combine(repoRoot, "App");
        Directory.CreateDirectory(subfolder);

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");

        var git = BuildGitService(("Sub", subfolder));

        var summary = Assert.Single(await git.RefreshSummariesAsync());
        Assert.True(summary.IsRepo,
            "RootPath inside a git repo must resolve to its toplevel (git rev-parse --show-toplevel).");
        Assert.Equal(repoRoot, summary.RootPath);
        Assert.Equal(repoRoot, summary.RepositoryPath);
        Assert.Equal("main", summary.Branch);
    }

    [Fact]
    public async Task GetSummaries_RepositoryPathOverridesWorkingDirectory()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var workingDirectory = Path.Combine(_tempDir, "source", "App");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(workingDirectory);

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");

        var git = BuildGitService(("Split", workingDirectory, repoRoot));

        var summary = Assert.Single(await git.RefreshSummariesAsync());
        Assert.True(summary.IsRepo);
        Assert.Equal(repoRoot, summary.RootPath);
        Assert.Equal(repoRoot, summary.RepositoryPath);
    }

    [Fact]
    public async Task GetSummaries_RootPathOutsideAnyRepo_IsNotReportedAsRepo()
    {
        var bare = Path.Combine(_tempDir, "no-repo");
        Directory.CreateDirectory(bare);

        var git = BuildGitService(("Bare", bare));

        var summary = Assert.Single(await git.RefreshSummariesAsync());
        Assert.False(summary.IsRepo);
    }

    private GitService BuildGitService(params (string Name, string RootPath, string? RepositoryPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            if (!string.IsNullOrWhiteSpace(entries[i].RepositoryPath))
                dict[$"WatchPaths:{i}:RepositoryPath"] = entries[i].RepositoryPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
        => BuildGitService(entries.Select(e => (e.Name, e.RootPath, (string?)null)).ToArray());

    private static void RunGit(string cwd, string args)
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
        p.WaitForExit(15_000);
    }
}
