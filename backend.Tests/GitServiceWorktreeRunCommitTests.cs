using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Author hygiene for platform-owned landing commits. The regular completion
/// path lands a worktree run via <see cref="GitService.WorktreeRunCommit"/>,
/// which MUST use the configured git identity - not the <c>Crash Recovery</c>
/// author that <see cref="GitService.CrashRecoveryCommit"/> stamps for the
/// boot-time orphan-rescue exception net. Reusing CrashRecoveryCommit for
/// normal landings made every landing show <c>author='Crash Recovery'</c> once
/// Always-Worktree routed all runs through the worktree-integration path.
/// </summary>
public class GitServiceWorktreeRunCommitTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceWorktreeRunCommitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-worktree-commit-tests-" + Guid.NewGuid().ToString("N"));
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
    public void WorktreeRunCommit_UsesConfiguredIdentity_NotCrashRecoveryAuthor()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var worktree = Path.Combine(_tempDir, "identity-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/identity {worktree}");
        var git = BuildGitService(repoRoot);

        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "agent work");
        var trailer = GitService.WorktreeRunCommitTrailer("my-task");
        var result = git.WorktreeRunCommit(
            "Proj", worktree, $"Implement feature\n\n{trailer}",
            taskId: "my-task", expectedBranch: "task/identity");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Ada Lovelace", RunGitCapture(worktree, "log -1 --format=%an"));
        Assert.NotEqual("Crash Recovery", RunGitCapture(worktree, "log -1 --format=%an"));
        // The durable per-run trailer must survive so ASS-1712 history
        // reconstruction still finds the landing.
        Assert.Contains(trailer, RunGitCapture(worktree, "log -1 --format=%B"));
    }

    [Fact]
    public void WorktreeRunCommit_ExcludesRootStatusFromProductCommit()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".agent-studio"));
        File.WriteAllText(Path.Combine(repoRoot, DeliveryReportPathPolicy.OptInPath), "enabled\n");
        RunGit(repoRoot, "add .agent-studio/legacy-status-reports");
        RunGit(repoRoot, "commit -qm enable-reports");
        var worktree = Path.Combine(_tempDir, "report-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/report {worktree}");
        var git = BuildGitService(repoRoot);
        File.WriteAllText(Path.Combine(worktree, "status.md"), "card report\n");
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature\n");

        var result = git.WorktreeRunCommit("Proj", worktree, "feat: add feature",
            taskId: "my-task", expectedBranch: "task/report");

        Assert.True(result.Success, result.Error);
        Assert.Equal("feature", RunGitCapture(worktree, "show HEAD:feature.txt"));
        Assert.Equal("", RunGitCapture(worktree, "ls-tree --name-only HEAD -- status.md"));
        Assert.True(File.Exists(Path.Combine(worktree, "status.md")));
    }

    [Fact]
    public void WorktreeRunCommit_PreservesRootStatusAsContentWithoutProjectOptIn()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var worktree = Path.Combine(_tempDir, "content-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/content {worktree}");
        File.WriteAllText(Path.Combine(worktree, "status.md"), "product content\n");

        var result = BuildGitService(repoRoot).WorktreeRunCommit("Proj", worktree,
            "feat: update product status", taskId: "my-task", expectedBranch: "task/content");

        Assert.True(result.Success, result.Error);
        Assert.Equal("product content", RunGitCapture(worktree, "show HEAD:status.md"));
    }

    [Fact]
    public void CrashRecoveryCommit_StillStampsCrashRecoveryAuthor()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);

        File.WriteAllText(Path.Combine(repoRoot, "orphan.txt"), "rescued work");
        var result = git.CrashRecoveryCommit(
            "Proj", repoRoot, "chore(crash-recovery): rescue orphan changes for my-task",
            ["orphan.txt"]);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Crash Recovery", RunGitCapture(repoRoot, "log -1 --format=%an"));
    }

    [Fact]
    public void WorktreeRunCommit_CleanTree_ReportsNothingToCommit()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var worktree = Path.Combine(_tempDir, "clean-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/clean {worktree}");
        var git = BuildGitService(repoRoot);

        var result = git.WorktreeRunCommit(
            "Proj", worktree, "no changes", taskId: "clean", expectedBranch: "task/clean");

        Assert.False(result.Success);
        Assert.Contains("Nothing to commit", result.Error);
    }

    [Fact]
    public void WorktreeRunCommit_MissingExpectedBranchBlocksMainCheckout()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "feature.txt"), "agent work");

        var result = git.WorktreeRunCommit("Proj", repoRoot, "feat: change", taskId: "unsafe");

        Assert.False(result.Success);
        Assert.Contains(result.Gate!.Findings, f => f.Code == "not-isolated-task-worktree");
        Assert.Contains(result.Gate.Findings, f => f.Code == "expected-worktree-branch-required");
        Assert.Equal("", RunGitCapture(repoRoot, "diff --cached --name-only"));
    }

    [Fact]
    public void WorktreeRunCommit_ExcludesAgt2157Scratch_AndCommitsOnlyThreeUiFiles()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var worktree = Path.Combine(_tempDir, "task-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/AGT-2174 {worktree}");
        var git = BuildGitService(repoRoot);

        Directory.CreateDirectory(Path.Combine(worktree, "frontend", "src"));
        File.WriteAllText(Path.Combine(worktree, "frontend", "src", "escalation.ts"), "export const escalation = true;\n");
        File.WriteAllText(Path.Combine(worktree, "frontend", "src", "escalation.html"), "<p>Escalation</p>\n");
        File.WriteAllText(Path.Combine(worktree, "frontend", "src", "escalation.scss"), ".escalation { display: block; }\n");
        File.WriteAllText(Path.Combine(worktree, ".tmp-agt2157.mjs"), "console.log('helper');\n");

        var result = git.WorktreeRunCommit(
            "Proj", worktree, "feat: update escalation UI",
            taskId: "AGT-2174", runnerId: "runner-test", expectedBranch: "task/AGT-2174");

        Assert.True(result.Success, result.Error);
        Assert.Equal(CommitGateDecisions.Warn, result.Gate?.Decision);
        Assert.Contains(result.Gate!.Findings, f => f.Code == "root-scratch-artifact" && f.ExcludesCandidate);
        var committed = RunGitCapture(worktree, "show --name-only --pretty=format: HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, committed.Length);
        Assert.DoesNotContain(".tmp-agt2157.mjs", committed);
        Assert.Contains("?? .tmp-agt2157.mjs", RunGitCapture(worktree, "status --short"));
        var body = RunGitCapture(worktree, "log -1 --format=%B");
        Assert.Contains("Task-Id: AGT-2174", body);
        Assert.Contains("Runner-Id: runner-test", body);
        Assert.Contains("Commit-Gate: warn", body);
    }

    [Fact]
    public void WorktreeRunCommit_WrongTaskBranchBlocksBeforeStaging()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var worktree = Path.Combine(_tempDir, "wrong-branch-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/actual {worktree}");
        File.WriteAllText(Path.Combine(worktree, "change.txt"), "agent work\n");
        var git = BuildGitService(repoRoot);

        var result = git.WorktreeRunCommit(
            "Proj", worktree, "feat: change",
            taskId: "expected", expectedBranch: "task/expected");

        Assert.False(result.Success);
        Assert.Contains(result.Gate!.Findings, f => f.Code == "unexpected-worktree-branch");
        Assert.Equal("", RunGitCapture(worktree, "diff --cached --name-only"));
    }

    [Fact]
    public void CrashRecoveryCommit_PrivateKeyBlocksAndRedactsEvidence()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);
        const string secret = "-----BEGIN PRIVATE KEY-----\nvery-secret-body\n-----END PRIVATE KEY-----\n";
        File.WriteAllText(Path.Combine(repoRoot, "leaked.pem"), secret);

        var result = git.CrashRecoveryCommit(
            "Proj", repoRoot, "recovery", ["leaked.pem"], taskId: "AGT-secret");

        Assert.False(result.Success);
        Assert.Equal(CommitGateDecisions.Block, result.Gate?.Decision);
        Assert.Contains(result.Gate!.Findings, f => f.Code == "private-key-material");
        Assert.DoesNotContain("very-secret-body", System.Text.Json.JsonSerializer.Serialize(result.Gate));
        Assert.Equal("seed", RunGitCapture(repoRoot, "log -1 --format=%s"));
    }

    [Fact]
    public void CrashRecoveryCommit_HighConfidenceTokenBlocksWithoutEchoingValue()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
        File.WriteAllText(Path.Combine(repoRoot, "credentials.txt"), token + "\n");

        var result = git.CrashRecoveryCommit(
            "Proj", repoRoot, "recovery", ["credentials.txt"], taskId: "AGT-token");

        Assert.False(result.Success);
        Assert.Contains(result.Gate!.Findings, f => f.Code == "high-confidence-token");
        Assert.DoesNotContain(token, result.Error ?? "");
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(result.Gate));
    }

    [Fact]
    public void CrashRecoveryCommit_WithoutExplicitPathsBlocksDirectCheckout()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "interactive.txt"), "operator work\n");

        var result = git.CrashRecoveryCommit("Proj", repoRoot, "recovery");

        Assert.False(result.Success);
        Assert.Contains(result.Gate!.Findings, finding => finding.Code == "explicit-pathspec-required");
        Assert.Equal("seed", RunGitCapture(repoRoot, "log -1 --format=%s"));
    }

    [Fact]
    public void WorktreeRunCommit_PlaceholderTokenAllowed_AndIgnoredSecretNotCandidate()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        File.AppendAllText(Path.Combine(repoRoot, ".gitignore"), "\nignored-secret.env\n");
        RunGit(repoRoot, "add .gitignore");
        RunGit(repoRoot, "commit -q -m ignore-secret-fixture");
        var worktree = Path.Combine(_tempDir, "placeholder-worktree");
        RunGit(repoRoot, $"worktree add -q -b task/fixture {worktree}");
        var git = BuildGitService(repoRoot);

        File.WriteAllText(Path.Combine(worktree, "fixture.txt"),
            "placeholder token: ghp_000000000000000000000000000000000000\n");
        File.WriteAllText(Path.Combine(worktree, "ignored-secret.env"),
            "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCD\n");

        var result = git.WorktreeRunCommit(
            "Proj", worktree, "test: add placeholder fixture",
            taskId: "fixture", expectedBranch: "task/fixture");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(result.Gate!.Candidates, c => c.Path == "ignored-secret.env");
        Assert.DoesNotContain(result.Gate.Findings, f => f.Code == "high-confidence-token");
        Assert.Equal("fixture.txt", RunGitCapture(worktree, "show --name-only --pretty=format: HEAD"));
    }

    [Fact]
    public void CandidateManifest_ConcurrentFileCannotRideAlong()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        File.WriteAllText(Path.Combine(repoRoot, "intended.txt"), "inspected\n");
        var gateService = new CommitCandidateGate(NullLogger<GitService>.Instance);
        var gate = gateService.Inspect(new CommitGateRequest(
            "test", "Proj", repoRoot, "AGT-2174", "runner-test",
            ExpectedPaths: ["intended.txt"]));

        File.WriteAllText(Path.Combine(repoRoot, "concurrent.txt"), "appeared after inspection\n");
        Assert.True(gateService.VerifyUnchangedAndStage(gate, out var error), error);

        Assert.Equal("intended.txt", RunGitCapture(repoRoot, "diff --cached --name-only"));
        Assert.Contains("?? concurrent.txt", RunGitCapture(repoRoot, "status --short"));
    }

    [Fact]
    public void CandidateManifest_BoundIndexKeepsInspectedContentAfterWorkingTreeChanges()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        File.WriteAllText(Path.Combine(repoRoot, "intended.txt"), "inspected\n");
        var gateService = new CommitCandidateGate(NullLogger<GitService>.Instance);
        var gate = gateService.Inspect(new CommitGateRequest(
            "test", "Proj", repoRoot, "AGT-2174", "runner-test",
            ExpectedPaths: ["intended.txt"]));

        Assert.True(gateService.TryPrepareBoundIndex(gate, out var boundIndex, out var error), error);
        Assert.NotNull(boundIndex);
        using (boundIndex)
        {
            File.WriteAllText(Path.Combine(repoRoot, "intended.txt"), "changed after inspection\n");
            File.WriteAllText(Path.Combine(repoRoot, "concurrent.txt"), "appeared after inspection\n");

            Assert.Equal("inspected", RunGitCaptureWithIndex(
                repoRoot, "show :intended.txt", boundIndex.FilePath));
            Assert.Equal("intended.txt", RunGitCaptureWithIndex(
                repoRoot, "diff --cached --name-only", boundIndex.FilePath));
        }
    }

    [Fact]
    public void CandidateScannerSeam_IsAdditiveAndFailureDoesNotFalsePass()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        File.WriteAllText(Path.Combine(repoRoot, "quality-studio.txt"), "ordinary content\n");
        var gateService = new CommitCandidateGate(
            NullLogger<GitService>.Instance, [new RejectingQualityStudioScanner()]);

        var gate = gateService.Inspect(new CommitGateRequest(
            "test", "Proj", repoRoot, "AGT-2174", "runner-test",
            ExpectedPaths: ["quality-studio.txt"]));

        Assert.False(gate.CanCommit);
        Assert.Contains("built-in", gate.ScannerSources);
        Assert.Contains("quality-studio", gate.ScannerSources);
        Assert.Contains(gate.Findings, f => f.Code == "quality-studio-policy");
    }

    [Fact]
    public void CommitMessageParser_UsesFinalAgentMessageAfterCodexProgressFrames()
    {
        var jsonl = string.Join('\n',
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Inspecting the candidate manifest.\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"status\":\"completed\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"COMMIT_REVIEW: ALLOW\\nfeat(frontend): update escalation UI\"}}",
            "{\"type\":\"turn.completed\"}");

        var reply = GitService.ParseCodexAgentMessage(jsonl);

        Assert.Equal(
            "COMMIT_REVIEW: ALLOW\nfeat(frontend): update escalation UI",
            reply);
    }

    private sealed class RejectingQualityStudioScanner : ICommitCandidateScanner
    {
        public string Name => "quality-studio";

        public IReadOnlyList<CommitGateFinding> Scan(
            string repositoryRoot, string relativePath, ReadOnlyMemory<byte> content, bool binary) =>
        [
            new("quality-studio-policy", CommitGateSeverities.Block, relativePath,
                "Quality Studio policy rejected this candidate without exposing content.", Name)
        ];
    }

    private string SeedRepo(string userName, string userEmail)
    {
        var repoRoot = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, $"config user.email {userEmail}");
        RunGit(repoRoot, $"config user.name \"{userName}\"");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");
        return repoRoot;
    }

    private static GitService BuildGitService(string repoRoot)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Proj",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:Path"] = Path.Combine(repoRoot, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void RunGit(string cwd, string args)
    {
        using var p = Process.Start(MakePsi(cwd, args))!;
        p.WaitForExit(15_000);
    }

    private static string RunGitCapture(string cwd, string args)
    {
        using var p = Process.Start(MakePsi(cwd, args))!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output.Trim();
    }

    private static string RunGitCaptureWithIndex(string cwd, string args, string indexPath)
    {
        var psi = MakePsi(cwd, args);
        psi.Environment["GIT_INDEX_FILE"] = indexPath;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output.Trim();
    }

    private static ProcessStartInfo MakePsi(string cwd, string args) => new()
    {
        FileName = "git",
        Arguments = args,
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
}
