using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression for ASS-1731: the per-task live Git view must read the task's
/// own run-location, not the shared main checkout. When a task runs in its own
/// <c>task/&lt;id&gt;</c> worktree (the parallel model, ADR-0052), the view has
/// to show THAT worktree's branch + dirty tree. The bug was that
/// <see cref="GitService.GetStatus"/> / <see cref="GitService.GetDiffResult"/>
/// always resolved to the main checkout, so a parallel run's uncommitted files
/// were cross-attributed to this task.
///
/// These tests stand up a real repo with a registered <c>task/demo-task</c>
/// worktree, dirty the main checkout with a *foreign* file (the sibling run's
/// work) and the worktree with the *task* file, then assert:
///  - <c>preferRunLocation: true</c> (what the view endpoints pass) reads the
///    worktree only - its branch, its file, never the sibling's;
///  - the default (internal callers, e.g. auto-commit scoping) still reads the
///    main checkout unchanged;
///  - with no live worktree the view falls back to the main checkout, labelled
///    as such (IsWorktree=false), rather than failing or showing nothing.
/// </summary>
public class GitServiceRunLocationTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceRunLocationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-run-location-" + Guid.NewGuid().ToString("N"));
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
    public async Task Networked_commit_message_review_uses_the_one_shot_dispatcher()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        WriteFile(repoRoot, "README.md", "changed");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath,
            ["Security:Profile"] = "networked",
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var oneShot = new CommitMessageOneShot();
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config,
            oneShotRegistry: new CliOneShotRegistry([oneShot]));

        var result = await git.GenerateCommitMessageAsync(jobId, watchPath);

        Assert.Equal("Describe the change", result.Message);
        Assert.Null(result.Error);
        Assert.NotNull(oneShot.Request);
        Assert.Equal("commit-message", oneShot.Request.Source);
        Assert.Equal("commit-message", oneShot.Request.StepId);
        Assert.Equal(jobId, oneShot.Request.JobId);
    }

    private sealed class CommitMessageOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Codex;
        public CliOneShotRequest? Request { get; private set; }
        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(CliOneShotResult.SpawnFailure(string.Empty,
                DateTime.UnixEpoch, DateTime.UnixEpoch) with
            {
                Ok = true,
                ExitCode = 0,
                ParsedText = "COMMIT_REVIEW: ALLOW\nDescribe the change",
                Error = null,
            });
        }
    }

    [Fact]
    public void PreferRunLocation_ReadsTaskWorktree_NotSiblingDirtyMainCheckout()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();

        // Seed commit on main so HEAD exists for both checkouts.
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        // The task runs in its own worktree on branch task/demo-task.
        var worktreePath = Path.Combine(_tempDir, "worktrees", "demo-task");
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        RunGit(repoRoot, "worktree", "add", worktreePath, "-b", "task/demo-task");

        // Main checkout gets a sibling run's uncommitted file; the worktree
        // gets the task's own uncommitted file. These must never cross over.
        WriteFile(repoRoot, "sibling-dirty.txt", "another run's work");
        WriteFile(worktreePath, "task-work.txt", "this task's work");

        var git = BuildGitService(repoRoot, watchPath);

        // View path: must show the worktree only.
        var view = git.GetStatus(jobId, watchPath, preferRunLocation: true);
        Assert.True(view.IsRepo);
        Assert.True(view.IsWorktree);
        Assert.Equal("task/demo-task", view.Branch);
        Assert.Contains(view.Files, f => f.Path == "task-work.txt");
        Assert.DoesNotContain(view.Files, f => f.Path == "sibling-dirty.txt");

        // Internal callers (default): unchanged main-checkout behaviour.
        var main = git.GetStatus(jobId, watchPath);
        Assert.True(main.IsRepo);
        Assert.False(main.IsWorktree);
        Assert.Equal("main", main.Branch);
        Assert.Contains(main.Files, f => f.Path == "sibling-dirty.txt");
        Assert.DoesNotContain(main.Files, f => f.Path == "task-work.txt");

        // Diff endpoint must follow the same checkout as the status list, so a
        // file shown in the worktree tree resolves to the worktree's content.
        var diff = git.GetDiffResult(jobId, watchPath, "task-work.txt", preferRunLocation: true);
        Assert.True(diff.Success);
        Assert.Contains("this task's work", diff.Diff);
    }

    [Fact]
    public void PreferRunLocation_NoWorktree_FallsBackToMainCheckout()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        // No worktree for this task (sequential run, or post-teardown). The
        // view must still resolve - to the main checkout - and label it as such.
        WriteFile(repoRoot, "main-dirty.txt", "main checkout work");

        var git = BuildGitService(repoRoot, watchPath);

        var view = git.GetStatus(jobId, watchPath, preferRunLocation: true);
        Assert.True(view.IsRepo);
        Assert.False(view.IsWorktree);
        Assert.Equal("main", view.Branch);
        Assert.Contains(view.Files, f => f.Path == "main-dirty.txt");
    }

    [Fact]
    public void PreferRunLocation_StatusCache_IsExplicitlyInvalidatable()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var git = BuildGitService(repoRoot, watchPath);

        var initial = git.GetStatus(jobId, watchPath, preferRunLocation: true);
        WriteFile(repoRoot, "after-cache.txt", "new work");

        var cached = git.GetStatus(jobId, watchPath, preferRunLocation: true);
        Assert.DoesNotContain(cached.Files, f => f.Path == "after-cache.txt");

        git.InvalidateStatusCache();
        var refreshed = git.GetStatus(jobId, watchPath, preferRunLocation: true);
        Assert.Contains(refreshed.Files, f => f.Path == "after-cache.txt");
    }

    private (string repoRoot, string jobId, string watchPath) SetupRepoAndJob()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");

        var jobId = "demo-task";
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(jobFolder);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
        var jobJson = new
        {
            id = jobId,
            title = "Demo task",
            state = "3-progress",
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Do the thing.");
        return (repoRoot, jobId, watchPath);
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
