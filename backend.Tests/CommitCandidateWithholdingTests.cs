using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2828 end-to-end. The WEB-21 delivery (screenshots plus two edited files)
/// hit the commit candidate gate, every screenshot raised an unresolved
/// <c>binary-surprise</c> warning, nothing was committed, and the card said
/// nothing about it.
///
/// <para>These tests drive the real gate against a real repository and cover the
/// three halves of the fix: a declared evidence asset commits without a manual
/// step, a withheld set is recorded on the card with its files and reasons, and
/// the operator action commits that set after review.</para>
/// </summary>
public class CommitCandidateWithholdingTests : IDisposable
{
    private readonly string _tempDir;

    public CommitCandidateWithholdingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "commit-withholding-" + Guid.NewGuid().ToString("N"));
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
    public void Undeclared_screenshots_still_withhold_the_whole_delivery()
    {
        var env = Setup(declaredAssetPaths: null);
        WriteScreenshots(env.Worktree, 12);
        WriteFile(env.Worktree, "docs/report.md", "# Report\n");

        var result = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);

        Assert.False(result.Success);
        Assert.Equal(CommitGateDecisions.Warn, result.Gate!.Decision);
        Assert.Equal(12, result.Gate.Findings.Count(f => f.Code == "binary-surprise"));
    }

    [Fact]
    public void Declared_screenshots_commit_without_a_manual_step()
    {
        var env = Setup(declaredAssetPaths: ["docs/assets"]);
        WriteScreenshots(env.Worktree, 12);
        WriteFile(env.Worktree, "docs/report.md", "# Report\n");

        var result = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(CommitGateDecisions.Allow, result.Gate!.Decision);
        Assert.DoesNotContain(result.Gate.Findings, f => f.Code == "binary-surprise");
        Assert.Equal(12, result.Gate.Findings.Count(f =>
            f.Code == EvidenceAssetCodes.Admitted && f.Severity == CommitGateSeverities.Info));
        var committed = RunGitCapture(env.Worktree, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(13, committed.Length);
        Assert.Contains("docs/assets/shot-01.png", committed);
    }

    [Fact]
    public void Declared_but_oversized_asset_still_requires_explicit_review()
    {
        var env = Setup(declaredAssetPaths: ["docs/assets"]);
        WriteBinary(env.Worktree, "docs/assets/huge.png", EvidenceAssetPolicy.MaxAssetBytes + 1);

        var result = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);

        Assert.False(result.Success);
        Assert.Contains(result.Gate!.Findings, f =>
            f.Code == "binary-surprise" && f.Message.Contains(EvidenceAssetCodes.Oversized, StringComparison.Ordinal));
    }

    [Fact]
    public void Declared_path_never_admits_secret_material()
    {
        var env = Setup(declaredAssetPaths: ["docs/assets"]);
        WriteScreenshots(env.Worktree, 1);
        WriteFile(env.Worktree, "docs/assets/leaked.pem",
            "-----BEGIN PRIVATE KEY-----\nvery-secret-body\n-----END PRIVATE KEY-----\n");

        var result = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);

        Assert.False(result.Success);
        Assert.Equal(CommitGateDecisions.Block, result.Gate!.Decision);
        Assert.Contains(result.Gate.Findings, f => f.Code == "private-key-material");
    }

    [Fact]
    public void Withheld_set_is_recorded_on_the_card_with_files_and_reasons()
    {
        var env = Setup(declaredAssetPaths: null);
        WriteScreenshots(env.Worktree, 12);
        WriteFile(env.Worktree, "docs/report.md", "# Report\n");

        var result = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);
        WithheldCommitCandidateStore.Persist(
            env.JobFolder, WithheldCommitCandidatePolicy.Describe(result.Gate));

        var record = WithheldCommitCandidateStore.TryRead(env.JobFolder);
        Assert.NotNull(record);
        Assert.True(record!.NothingCommitted);
        Assert.Equal(13, record.Count);
        Assert.Equal("binary-surprise",
            record.Candidates.Single(c => c.Path == "docs/assets/shot-01.png").Reason);
        Assert.Contains(record.Candidates, c => c.Path == "docs/report.md");

        var reason = WithheldCommitCandidatePolicy.ComposeParkReason(
            "Run finished without a terminal sentinel.", record);
        Assert.Contains("commit candidate gate (warn) withheld 13 file(s)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Operator_action_commits_the_withheld_candidates_after_review()
    {
        var env = Setup(declaredAssetPaths: null);
        WriteScreenshots(env.Worktree, 12);
        WriteFile(env.Worktree, "docs/report.md", "# Report\n");
        var blocked = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);
        Assert.False(blocked.Success);
        WithheldCommitCandidateStore.Persist(
            env.JobFolder, WithheldCommitCandidatePolicy.Describe(blocked.Gate));

        var committed = env.Git.CommitWithheldCandidates(env.JobId, env.WatchPath);

        Assert.True(committed.Success, committed.Error);
        var files = RunGitCapture(env.Worktree, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(13, files.Length);
        Assert.Contains("docs/assets/shot-01.png", files);
        Assert.Contains("docs/report.md", files);
        // The card no longer claims work is waiting once it landed.
        Assert.Null(WithheldCommitCandidateStore.TryRead(env.JobFolder));
    }

    [Fact]
    public void Operator_action_commits_only_the_reviewed_subset()
    {
        var env = Setup(declaredAssetPaths: null);
        WriteScreenshots(env.Worktree, 3);
        WriteFile(env.Worktree, "docs/report.md", "# Report\n");
        var blocked = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);
        WithheldCommitCandidateStore.Persist(
            env.JobFolder, WithheldCommitCandidatePolicy.Describe(blocked.Gate));

        var committed = env.Git.CommitWithheldCandidates(
            env.JobId, env.WatchPath, "chore: land the reviewed report",
            ["docs/report.md"]);

        Assert.True(committed.Success, committed.Error);
        Assert.Equal("docs/report.md",
            RunGitCapture(env.Worktree, "show", "--name-only", "--pretty=format:", "HEAD"));
        Assert.Contains("?? docs/assets/", RunGitCapture(env.Worktree, "status", "--short"));
        // The three screenshots are still waiting, so the card must keep saying
        // so rather than losing them when the reviewed subset landed.
        var remaining = WithheldCommitCandidateStore.TryRead(env.JobFolder);
        Assert.NotNull(remaining);
        Assert.Equal(3, remaining!.Count);
        Assert.False(remaining.NothingCommitted);
        Assert.DoesNotContain(remaining.Candidates, c => c.Path == "docs/report.md");
    }

    [Fact]
    public void Operator_action_still_refuses_blocked_material()
    {
        var env = Setup(declaredAssetPaths: null);
        WriteFile(env.Worktree, "docs/leaked.pem",
            "-----BEGIN PRIVATE KEY-----\nvery-secret-body\n-----END PRIVATE KEY-----\n");
        var blocked = env.Git.WorktreeRunCommit(
            "Demo", env.Worktree, "feat: capture evidence",
            taskId: env.JobId, expectedBranch: "task/" + env.JobId);
        WithheldCommitCandidateStore.Persist(
            env.JobFolder, WithheldCommitCandidatePolicy.Describe(blocked.Gate));

        var attempt = env.Git.CommitWithheldCandidates(env.JobId, env.WatchPath);

        Assert.False(attempt.Success);
        Assert.Contains("private-key-material", attempt.Error, StringComparison.Ordinal);
        Assert.NotNull(WithheldCommitCandidateStore.TryRead(env.JobFolder));
    }

    [Fact]
    public void Operator_action_without_a_recorded_set_reports_nothing_to_do()
    {
        var env = Setup(declaredAssetPaths: null);

        var attempt = env.Git.CommitWithheldCandidates(env.JobId, env.WatchPath);

        Assert.False(attempt.Success);
        Assert.Contains("No withheld commit candidates", attempt.Error, StringComparison.Ordinal);
    }

    private sealed record Environment(
        GitService Git, string RepoRoot, string Worktree, string JobId, string JobFolder, string WatchPath);

    private Environment Setup(IReadOnlyList<string>? declaredAssetPaths)
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);
        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        const string jobId = "WEB-21";
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = jobId,
            title = "Capture board evidence",
            state = "3-progress",
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow.ToString("o"),
        }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Capture 12 screenshots.");

        var worktree = Path.Combine(_tempDir, "worktrees", jobId);
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        RunGit(repoRoot, "worktree", "add", "-q", worktree, "-b", "task/" + jobId);

        var settingsRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(settingsRoot);
        File.WriteAllText(
            Path.Combine(settingsRoot, "project-settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Demo"] = new { evidenceAssetPaths = declaredAssetPaths },
            }));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath,
            ["TaskRepository"] = settingsRoot,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(
            NullLogger<GitService>.Instance, scanner, config, projectSettings: settings);

        return new Environment(git, repoRoot, worktree, jobId, jobFolder, watchPath);
    }

    private static void WriteScreenshots(string root, int count)
    {
        for (var i = 1; i <= count; i++)
            WriteBinary(root, $"docs/assets/shot-{i:00}.png", 2048);
    }

    private static void WriteBinary(string root, string relativePath, long bytes)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // A PNG signature plus a NUL run: the gate classifies binary from a NUL
        // byte in the first 8000 bytes, exactly like a real screenshot.
        var buffer = new byte[bytes];
        buffer[0] = 0x89;
        buffer[1] = (byte)'P';
        buffer[2] = (byte)'N';
        buffer[3] = (byte)'G';
        File.WriteAllBytes(full, buffer);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        using var process = Process.Start(MakePsi(cwd, args))!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        using var process = Process.Start(MakePsi(cwd, args))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        return output.Trim();
    }

    private static ProcessStartInfo MakePsi(string cwd, string[] args)
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
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }
}
