using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class GitBranchRetentionReplayTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "git-retention-replay-" + Guid.NewGuid().ToString("N"));

    public GitBranchRetentionReplayTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void DeletedResultsRefDoesNotBreakReplay()
    {
        var (repo, bare, retention, git) = SetupReplayRepository();

        // Get the results ref SHA before deletion
        var resultsSha = RunGitOut(repo, "rev-parse", "agent-studio/results/delivery1/fence-1/abc123").Trim();
        Assert.False(string.IsNullOrWhiteSpace(resultsSha));

        // Get proof commit SHA (should be reachable from main after merge)
        var proofSha = RunGitOut(repo, "rev-parse", "main").Trim();
        Assert.False(string.IsNullOrWhiteSpace(proofSha));

        // Run retention to delete the results ref
        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: false);
        var deletedResultsRefs = report.Actions.Where(a =>
            a.Namespace == BranchNamespace.ResultsRef && a.Deleted).ToList();
        Assert.NotEmpty(deletedResultsRefs);

        // Verify the results ref is actually deleted from origin
        var refExists = GitCode(repo, "ls-remote", "--exit-code", "--heads", "origin", "agent-studio/results/delivery1/fence-1/abc123");
        Assert.NotEqual(0, refExists); // Should fail - ref is deleted

        // Verify proof commit is still reachable from main
        var isAncestor = git.IsAncestor(repo, resultsSha, "main");
        Assert.True(isAncestor, "Proof commit should remain reachable from main after ref deletion");

        // Simulate replay: create a new delivery based on the proof commit
        RunGit(repo, "checkout", "-q", "-b", "task/replayed");
        Commit(repo, "replay-work.txt", "replayed", "replay work", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/replayed");

        // Verify replay succeeded
        var replayProofSha = RunGitOut(repo, "rev-parse", "main").Trim();
        Assert.False(string.IsNullOrWhiteSpace(replayProofSha));
        Assert.NotEqual(proofSha, replayProofSha); // Should have advanced

        // Verify the original proof commit is still part of the history
        var commitLog = RunGitOut(repo, "log", "--oneline", "-20");
        Assert.Contains(resultsSha.Substring(0, 7), commitLog);
    }

    [Fact]
    public void TaskCanBeReissuedAfterResultsRefDeletion()
    {
        var (repo, bare, retention, git) = SetupReplayRepository();

        // Store original task delivery info
        var originalBranch = "task/original-delivery";
        var originalSha = RunGitOut(repo, "rev-parse", originalBranch).Trim();

        // Run retention to delete refs
        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: false);
        Assert.True(report.DeletedCount > 0);

        // Simulate task reissue: create new delivery on same or different branch
        RunGit(repo, "checkout", "-q", "-b", "task/reissued-delivery");
        Commit(repo, "reissued.txt", "reissued", "reissued work", old: true);
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/reissued-delivery");

        // Verify reissue succeeds
        var newSha = RunGitOut(repo, "rev-parse", "task/reissued-delivery").Trim();
        Assert.False(string.IsNullOrWhiteSpace(newSha));
        Assert.NotEqual(originalSha, newSha);

        // Verify develop is updated: the reissued delivery is now reachable
        // from develop (not a literal substring of develop's own tip SHA).
        var isAncestor = git.IsAncestor(repo, newSha, "develop");
        Assert.True(isAncestor, "Reissued delivery should be reachable from develop");
    }

    [Fact]
    public void AllProofCommitsRemainReachableAfterMassRefDeletion()
    {
        var (repo, bare, retention, git) = SetupReplayRepository();

        // Collect proof SHAs before deletion
        var proofShas = new[]
        {
            RunGitOut(repo, "rev-parse", "agent-studio/results/delivery1/fence-1/abc123").Trim(),
            RunGitOut(repo, "rev-parse", "agent-studio/results/delivery2/fence-1/def456").Trim(),
        }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        Assert.True(proofShas.Count >= 2);

        // Run retention to delete all eligible refs
        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: false);
        var deletedCount = report.Actions.Count(a => a.Deleted);
        Assert.True(deletedCount > proofShas.Count);

        // Verify all proof commits remain reachable
        var main = RunGitOut(repo, "rev-parse", "main").Trim();
        foreach (var sha in proofShas)
        {
            var isAncestor = git.IsAncestor(repo, sha, main);
            Assert.True(isAncestor,
                $"Proof commit {sha} must remain reachable from main after deletion of {deletedCount} refs");
        }
    }

    [Fact]
    public void DryRunDoesNotDeleteRefs()
    {
        var (repo, bare, retention, git) = SetupReplayRepository();

        // Get ref count before dry-run
        var beforeDelete = RunGitOut(repo, "show-ref").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // Run in dry-run mode
        var dryReport = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: true);
        Assert.True(dryReport.DeletedCount == 0); // Dry-run should not delete

        // Run actual deletion
        var liveReport = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: false);
        Assert.True(liveReport.DeletedCount > 0); // Live run should delete

        // Verify refs were actually deleted
        var afterDelete = RunGitOut(repo, "show-ref").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(afterDelete < beforeDelete);
    }

    private (string Repo, string Bare, GitBranchRetentionService Retention, GitService Git) SetupReplayRepository()
    {
        var bare = Path.Combine(_tempDir, "origin.git");
        var repo = Path.Combine(_tempDir, "repo");
        RunGit(_tempDir, "init", "-q", "--bare", bare);
        RunGit(_tempDir, "init", "-q", "-b", "main", repo);
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        RunGit(repo, "remote", "add", "origin", bare);

        // Create main and develop
        Commit(repo, "README.md", "seed", "seed", old: true);
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "-u", "origin", "main", "develop");

        // Create first delivery with results ref
        RunGit(repo, "checkout", "-q", "-b", "task/original-delivery");
        Commit(repo, "delivery1.txt", "delivery", "first delivery", old: true);
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/original-delivery");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/original-delivery");
        RunGit(repo, "push", "-q", "--all", "origin");

        // Create results ref for first delivery (proof of execution). It is
        // an immutable pointer at the SHA already merged into main above, not
        // a new commit of its own - matching real production results refs
        // (FencedGitRefs.ImmutableResult), which name the delivered SHA
        // rather than adding to it.
        RunGit(repo, "checkout", "-q", "-b", "agent-studio/results/delivery1/fence-1/abc123");
        RunGit(repo, "push", "-q", "-u", "origin", "agent-studio/results/delivery1/fence-1/abc123");

        // Create second delivery with results ref
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "checkout", "-q", "-b", "task/second-delivery");
        Commit(repo, "delivery2.txt", "delivery", "second delivery", old: true);
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/second-delivery");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/second-delivery");
        RunGit(repo, "push", "-q", "--all", "origin");

        // Create results ref for second delivery, same immutable-pointer shape.
        RunGit(repo, "checkout", "-q", "-b", "agent-studio/results/delivery2/fence-1/def456");
        RunGit(repo, "push", "-q", "-u", "origin", "agent-studio/results/delivery2/fence-1/def456");

        var configuration = Configuration(repo);
        var git = GitFor(repo, configuration);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            configuration, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var retention = new GitBranchRetentionService(
            git,
            registry,
            configuration,
            NullLogger<GitBranchRetentionService>.Instance,
            new MockTimeProvider(Now));
        return (repo, bare, retention, git);
    }

    private IConfiguration Configuration(string repo)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_tempDir, "task-repository"),
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();

    private GitService GitFor(string repo, IConfiguration? configuration = null)
    {
        configuration ??= Configuration(repo);
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(
            configuration, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, configuration);
    }

    private static void Commit(string repo, string path, string content, string message, bool old)
    {
        File.WriteAllText(Path.Combine(repo, path), content);
        RunGit(repo, "add", path);
        Run(repo, ["commit", "-q", "-m", message], old
            ? new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2020-01-01T12:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2020-01-01T12:00:00Z",
            }
            : null);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = Run(cwd, args);
        if (result.Code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Err}");
    }

    private static string RunGitOut(string cwd, params string[] args) => Run(cwd, args).Out;

    private static int GitCode(string cwd, params string[] args) => Run(cwd, args).Code;

    private static (string Out, string Err, int Code) Run(
        string cwd,
        string[] args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (output, error, process.ExitCode);
    }

    private sealed class MockTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public MockTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
