using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class GitBranchRetentionBareRemoteTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "git-retention-bare-" + Guid.NewGuid().ToString("N"));

    public GitBranchRetentionBareRemoteTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void AllNamespacesAreClassifiedAndDeletedAppropriately()
    {
        var (repo, bare, retention) = SetupBareRemoteRepository();

        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7);

        Assert.Null(report.Error);
        Assert.NotNull(report.DevelopRef);
        Assert.NotNull(report.MainRef);

        // Verify that refs in each namespace are classified correctly
        var byNamespace = report.Actions
            .GroupBy(a => a.Namespace)
            .ToDictionary(g => g.Key, g => g.ToList());

        // task/* - should delete merged old ones. The fixture leaves both a
        // local branch (git checkout switched away without deleting it) and a
        // remote-tracking ref behind, so both scopes are eligible.
        AssertNamespaceActions(byNamespace, BranchNamespace.Task,
            (actions) =>
            {
                var merged = actions.Where(a => a.Branch == "task/merged-old").ToList();
                Assert.NotEmpty(merged);
                Assert.All(merged, action =>
                {
                    Assert.True(action.Deleted);
                    Assert.Equal(BranchRetentionDecision.Delete, action.Decision);
                });
            });

        // runner/* - should delete merged old ones
        AssertNamespaceActions(byNamespace, BranchNamespace.Runner,
            (actions) =>
            {
                var merged = actions.Where(a => a.Branch == "runner/agent/task-key").ToList();
                Assert.NotEmpty(merged);
                Assert.All(merged, action => Assert.True(action.Deleted));
            });

        // delivery/* - should delete merged old ones
        AssertNamespaceActions(byNamespace, BranchNamespace.Delivery,
            (actions) =>
            {
                var merged = actions.Where(a => a.Branch == "delivery/task-key").ToList();
                Assert.NotEmpty(merged);
                Assert.All(merged, action => Assert.True(action.Deleted));
            });

        // results/* - should delete only after in main
        AssertNamespaceActions(byNamespace, BranchNamespace.ResultsRef,
            (actions) =>
            {
                var inMain = actions.Where(a =>
                    a.Branch.StartsWith("agent-studio/results/") && a.Deleted).ToList();
                Assert.NotEmpty(inMain);
                foreach (var action in inMain)
                    Assert.Equal(BranchRetentionDecision.Delete, action.Decision);
            });

        // salvage/* - should delete if >= 14 days old OR in main
        AssertNamespaceActions(byNamespace, BranchNamespace.SalvageRef,
            (actions) =>
            {
                var deleted = actions.Where(a => a.Deleted).ToList();
                Assert.NotEmpty(deleted);
            });

        // quarantine/* - should delete only after 30 days
        var quarantineCount = report.Actions
            .Count(a => a.Namespace == BranchNamespace.QuarantineRef && a.Deleted);
        Assert.Equal(0, quarantineCount); // Fresh quarantine, not yet eligible
    }

    [Fact]
    public void IntegrationAndPromotionDeleteRefs()
    {
        var (repo, bare, retention) = SetupBareRemoteRepository();

        // A salvage ref that is neither merged into main nor yet 14 days old
        // at T: retained at T, eligible once the 14-day window elapses. Every
        // other namespace in the base fixture is already merged and old
        // enough to be deleted on the very first pass, so without this ref
        // there is nothing left for a later pass to find.
        const string pendingSalvageRef = "agent-studio/salvage/agent/task/attempt2/fence-1/pending123";
        RunGit(repo, "checkout", "-q", "-b", pendingSalvageRef);
        File.WriteAllText(Path.Combine(repo, "salvage-pending.txt"), "still in progress");
        RunGit(repo, "add", "salvage-pending.txt");
        Run(repo, ["commit", "-q", "-m", "salvage still in progress"], new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = Now.AddDays(-10).ToString("o"),
            ["GIT_COMMITTER_DATE"] = Now.AddDays(-10).ToString("o"),
        });
        RunGit(repo, "push", "-q", "-u", "origin", pendingSalvageRef);
        RunGit(repo, "checkout", "-q", "main");

        // First run at time T: the merged refs from the base fixture are
        // deleted; the 10-day-old pending salvage ref is retained.
        var reportT = retention.RunRepository("Demo", repo, Now, retentionDays: 7);
        var deletedAtT = reportT.DeletedCount;
        Assert.True(deletedAtT > 0, "Some merged refs should be deleted at T");
        Assert.Contains(reportT.Actions, a => a.Branch == pendingSalvageRef && !a.Deleted);

        // Simulate promotion: all main ancestry checks pass
        var mainTip = RunGitOut(repo, "rev-parse", "main").Trim();
        Assert.False(string.IsNullOrWhiteSpace(mainTip));

        // Second run 20 days later: the pending salvage ref is now 30 days
        // old, past the 14-day window, so it becomes eligible too.
        var laterTime = Now.AddDays(20);
        var reportLater = retention.RunRepository("Demo", repo, laterTime, retentionDays: 7);
        Assert.Contains(reportLater.Actions, a => a.Branch == pendingSalvageRef && a.Deleted);
    }

    [Fact]
    public void ProofCommitsRemainReachableAfterRefDeletion()
    {
        var (repo, bare, retention) = SetupBareRemoteRepository();
        var git = GitFor(repo);

        // Get proof SHAs before deletion
        var proofShas = new[]
        {
            RunGitOut(repo, "rev-parse", "agent-studio/results/attempt1/fence-1/abc123").Trim(),
            RunGitOut(repo, "rev-parse", "agent-studio/salvage/agent/task/attempt1/fence-1/def456").Trim()
        }.Where(s => !string.IsNullOrWhiteSpace(s) && s != "fatal: Not a valid object name").ToList();

        Assert.NotEmpty(proofShas);

        // Run retention to delete refs
        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7);
        Assert.True(report.DeletedCount > 0);

        // Verify proof commits are still reachable via main
        foreach (var sha in proofShas)
        {
            // Verify SHA is reachable from main
            var isAncestor = git.IsAncestor(repo, sha, "main");
            Assert.True(isAncestor,
                $"Proof commit {sha} should remain reachable from main after ref deletion");
        }
    }

    [Fact]
    public void DeletedRefsAndReachabilityAreRecorded()
    {
        var (repo, bare, retention) = SetupBareRemoteRepository();

        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7, dryRun: false);

        Assert.True(report.DeletedCount > 0);
        var deletedActions = report.Actions.Where(a => a.Deleted).ToList();
        Assert.NotEmpty(deletedActions);

        foreach (var action in deletedActions)
        {
            Assert.NotNull(action.Branch);
            Assert.NotNull(action.TipSha);
            Assert.Equal(BranchRetentionDecision.Delete, action.Decision);
            Assert.NotNull(action.Reason);
            Assert.NotNull(action.Namespace);
        }
    }

    /// <summary>
    /// AGT-2793 requirement 3: every deletion lands in the per-project
    /// <c>reports/git-branch-reclaim.jsonl</c> evidence file, so the audit
    /// trail survives even after the ref itself is gone.
    /// </summary>
    [Fact]
    public void RunRepository_WritesEvidenceRowForEveryDeletedRef()
    {
        var (repo, _, _) = SetupBareRemoteRepository();
        var configuration = Configuration(repo);
        var git = GitFor(repo, configuration);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            configuration, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var evidence = new BranchRetentionEvidenceWriter(
            configuration, NullLogger<BranchRetentionEvidenceWriter>.Instance);
        var retention = new GitBranchRetentionService(
            git, registry, configuration, NullLogger<GitBranchRetentionService>.Instance,
            new MockTimeProvider(Now), evidence);

        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 7);

        Assert.True(report.DeletedCount > 0);
        var file = evidence.ReportFile("Demo")!;
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.Equal(report.DeletedCount, lines.Length);
        Assert.Contains(lines, line => line.Contains("\"ref\":\"task/merged-old\"", StringComparison.Ordinal));
    }

    private void AssertNamespaceActions(
        Dictionary<BranchNamespace?, List<BranchRetentionAction>> byNamespace,
        BranchNamespace ns,
        Action<List<BranchRetentionAction>> assertions)
    {
        if (byNamespace.TryGetValue(ns, out var actions))
        {
            Assert.NotEmpty(actions);
            assertions(actions);
        }
    }

    private (string Repo, string Bare, GitBranchRetentionService Retention) SetupBareRemoteRepository()
    {
        var bare = Path.Combine(_tempDir, "origin.git");
        var repo = Path.Combine(_tempDir, "repo");
        RunGit(_tempDir, "init", "-q", "--bare", bare);
        RunGit(_tempDir, "init", "-q", "-b", "main", repo);
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        RunGit(repo, "remote", "add", "origin", bare);

        // Create main and develop with seed commits
        Commit(repo, "README.md", "seed", "seed", old: true);
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "-u", "origin", "main", "develop");

        // 1. task/* namespace - create and merge
        RunGit(repo, "checkout", "-q", "-b", "task/merged-old");
        Commit(repo, "task.txt", "task", "task work", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/merged-old");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/merged-old");
        RunGit(repo, "push", "-q", "--all", "origin");

        // 2. runner/* namespace - create and merge
        RunGit(repo, "checkout", "-q", "-b", "runner/agent/task-key");
        Commit(repo, "runner.txt", "runner", "runner work", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "runner/agent/task-key");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "runner/agent/task-key");
        RunGit(repo, "push", "-q", "--all", "origin");

        // 3. delivery/* namespace - create and merge
        RunGit(repo, "checkout", "-q", "-b", "delivery/task-key");
        Commit(repo, "delivery.txt", "delivery", "delivery work", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "delivery/task-key");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "delivery/task-key");
        RunGit(repo, "push", "-q", "--all", "origin");

        // 4. results/* namespace - create and merge
        RunGit(repo, "checkout", "-q", "-b", "agent-studio/results/attempt1/fence-1/abc123");
        Commit(repo, "result.txt", "result", "result proof", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "agent-studio/results/attempt1/fence-1/abc123");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "agent-studio/results/attempt1/fence-1/abc123");
        RunGit(repo, "push", "-q", "--all", "origin");

        // 5. salvage/* namespace - create and merge
        RunGit(repo, "checkout", "-q", "-b", "agent-studio/salvage/agent/task/attempt1/fence-1/def456");
        Commit(repo, "salvage.txt", "salvage", "salvage backup", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "agent-studio/salvage/agent/task/attempt1/fence-1/def456");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "agent-studio/salvage/agent/task/attempt1/fence-1/def456");
        RunGit(repo, "push", "-q", "--all", "origin");

        // 6. quarantine/* namespace - create but don't merge (too recent to delete)
        RunGit(repo, "checkout", "-q", "-b", "agent-studio/quarantine/runner/task/attempt1/ghi789");
        Commit(repo, "quarantine.txt", "quarantine", "rejected delivery", old: false);
        RunGit(repo, "push", "-q", "-u", "origin", "agent-studio/quarantine/runner/task/attempt1/ghi789");

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
        return (repo, bare, retention);
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
