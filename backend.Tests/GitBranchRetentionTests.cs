using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BranchRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");

    [Fact]
    public void Evaluate_DeletesOnlyOldManagedBranchMergedIntoBothProtectedLines()
    {
        var decision = BranchRetentionPolicy.Evaluate(
            EligibleFacts(), Now, TimeSpan.FromDays(30));

        Assert.Equal(BranchRetentionDecision.Delete, decision);
    }

    [Theory]
    [InlineData(false, true, BranchRetentionDecision.NotMergedIntoDevelop)]
    [InlineData(true, false, BranchRetentionDecision.NotMergedIntoMain)]
    [InlineData(false, false, BranchRetentionDecision.NotMergedIntoDevelop)]
    public void Evaluate_RetainsWhenEitherProtectedLineDoesNotContainTip(
        bool mergedIntoDevelop,
        bool mergedIntoMain,
        BranchRetentionDecision expected)
    {
        var facts = EligibleFacts() with
        {
            MergedIntoDevelop = mergedIntoDevelop,
            MergedIntoMain = mergedIntoMain,
        };

        Assert.Equal(expected,
            BranchRetentionPolicy.Evaluate(facts, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Evaluate_RetainsYoungCheckedOutAndUnknownAgeBranches()
    {
        Assert.Equal(BranchRetentionDecision.TooYoung,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { TipCommittedAtUtc = Now.AddDays(-29) },
                Now,
                TimeSpan.FromDays(30)));
        Assert.Equal(BranchRetentionDecision.CheckedOut,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { CheckedOut = true },
                Now,
                TimeSpan.FromDays(30)));
        Assert.Equal(BranchRetentionDecision.MissingCommitTime,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { TipCommittedAtUtc = null },
                Now,
                TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Evaluate_FailsClosedWhenDevelopOrMainIsUnavailable()
    {
        Assert.Equal(BranchRetentionDecision.DevelopUnavailable,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { DevelopAvailable = false },
                Now,
                TimeSpan.FromDays(30)));
        Assert.Equal(BranchRetentionDecision.MainUnavailable,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { MainAvailable = false },
                Now,
                TimeSpan.FromDays(30)));
    }

    [Theory]
    [InlineData("feature/old")]
    [InlineData("release/1.0")]
    [InlineData("main")]
    public void Evaluate_NeverDeletesBranchesOutsideManagedNamespaces(string branch)
    {
        var ns = BranchRetentionPolicy.ClassifyBranch(branch);
        Assert.Equal(BranchRetentionDecision.UnsupportedNamespace,
            BranchRetentionPolicy.Evaluate(
                EligibleFacts() with { Branch = branch, Namespace = ns },
                Now,
                TimeSpan.FromDays(30)));
    }

    private static BranchRetentionFacts EligibleFacts() => new(
        Branch: "task/old",
        Namespace: BranchNamespace.Task,
        TipCommittedAtUtc: Now.AddDays(-31),
        CheckedOut: false,
        DevelopAvailable: true,
        MainAvailable: true,
        MergedIntoDevelop: true,
        MergedIntoMain: true,
        TaskKeyForRef: "old",
        IsIntegrated: true,
        IsReferenced: false);
}

[Trait("Category", "MachineBound")]
public sealed class GitBranchRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "git-retention-" + Guid.NewGuid().ToString("N"));

    public GitBranchRetentionServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void RunRepository_DeletesOnlyOldDoublyMergedRefsAndPrunesStaleWorktree()
    {
        var (repo, retention) = SetupRepository();
        var stalePath = Path.Combine(_tempDir, "stale-worktree");
        RunGit(repo, "worktree", "add", "--detach", stalePath, "main");
        Directory.Delete(stalePath, recursive: true);

        var report = retention.RunRepository("Demo", repo, Now, retentionDays: 30);

        Assert.Null(report.Error);
        Assert.Equal("origin/develop", report.DevelopRef);
        Assert.Equal("origin/main", report.MainRef);
        Assert.Equal(1, report.StaleWorktreesPruned);
        Assert.Equal(2, report.DeletedCount);
        Assert.Equal(0, GitCode(repo, "rev-parse", "--verify", "refs/heads/runner/develop-only"));
        Assert.Equal(0, GitCode(repo, "ls-remote", "--exit-code", "--heads", "origin", "runner/develop-only"));
        Assert.Equal(0, GitCode(repo, "rev-parse", "--verify", "refs/heads/task/young"));
        Assert.Equal(0, GitCode(repo, "rev-parse", "--verify", "refs/heads/task/checked"));
        Assert.NotEqual(0, GitCode(repo, "rev-parse", "--verify", "refs/heads/task/merged"));
        Assert.NotEqual(0, GitCode(repo, "ls-remote", "--exit-code", "--heads", "origin", "task/merged"));

        Assert.All(report.Actions.Where(action => action.Branch == "runner/develop-only"),
            action => Assert.Equal(BranchRetentionDecision.NotMergedIntoMain, action.Decision));
        Assert.All(report.Actions.Where(action => action.Branch == "task/young"),
            action => Assert.Equal(BranchRetentionDecision.TooYoung, action.Decision));
        Assert.All(report.Actions.Where(action => action.Branch == "task/checked"),
            action => Assert.Equal(BranchRetentionDecision.CheckedOut, action.Decision));

        var repeated = retention.RunRepository("Demo", repo, Now, retentionDays: 30);
        Assert.Equal(0, repeated.DeletedCount);
    }

    [Fact]
    public void DeleteRemoteBranchAtTip_LeaseRetainsBranchWhenExpectedTipIsStale()
    {
        var (repo, _) = SetupRepository();
        var git = GitFor(repo);
        var staleExpected = GitOut(repo, "rev-parse", "task/merged").Trim();
        RunGit(repo, "checkout", "-q", "task/merged");
        Commit(repo, "advanced.txt", "advanced", "advance branch", old: true);
        RunGit(repo, "push", "-q", "origin", "task/merged");
        RunGit(repo, "checkout", "-q", "main");

        var result = git.DeleteRemoteBranchAtTip(repo, "task/merged", staleExpected);

        Assert.False(result.Success);
        Assert.Equal(0, GitCode(repo, "ls-remote", "--exit-code", "--heads", "origin", "task/merged"));
    }

    [Fact]
    public void DeleteBranchAtTip_RetainsLocalBranchWhenExpectedTipIsStale()
    {
        var (repo, _) = SetupRepository();
        var git = GitFor(repo);
        var staleExpected = GitOut(repo, "rev-parse", "task/merged").Trim();
        RunGit(repo, "checkout", "-q", "task/merged");
        Commit(repo, "local-advanced.txt", "advanced", "advance local branch", old: true);
        RunGit(repo, "checkout", "-q", "main");

        var result = git.DeleteBranchAtTip(repo, "task/merged", staleExpected);

        Assert.False(result.Success);
        Assert.Equal(0, GitCode(repo, "rev-parse", "--verify", "refs/heads/task/merged"));
    }

    private (string Repo, GitBranchRetentionService Retention) SetupRepository()
    {
        var remote = Path.Combine(_tempDir, "origin.git");
        var repo = Path.Combine(_tempDir, "repo");
        RunGit(_tempDir, "init", "-q", "--bare", remote);
        RunGit(_tempDir, "init", "-q", "-b", "main", repo);
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        RunGit(repo, "remote", "add", "origin", remote);
        Commit(repo, "README.md", "seed", "seed", old: true);
        RunGit(repo, "checkout", "-q", "-b", "develop");

        RunGit(repo, "checkout", "-q", "-b", "task/merged");
        Commit(repo, "merged.txt", "merged", "merged task", old: true);
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/merged");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "develop");

        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "checkout", "-q", "-b", "runner/develop-only");
        Commit(repo, "runner.txt", "runner", "runner work", old: true);
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "runner/develop-only");

        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "checkout", "-q", "-b", "task/young");
        Commit(repo, "young.txt", "young", "young task", old: false);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/young");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/young");

        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "checkout", "-q", "-b", "task/checked");
        Commit(repo, "checked.txt", "checked", "checked task", old: true);
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/checked");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/checked");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "push", "-q", "--all", "origin");

        var checkedPath = Path.Combine(_tempDir, "checked-worktree");
        RunGit(repo, "worktree", "add", "-q", checkedPath, "task/checked");

        var configuration = Configuration(repo);
        var git = GitFor(repo, configuration);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            configuration, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var retention = new GitBranchRetentionService(
            git,
            registry,
            configuration,
            NullLogger<GitBranchRetentionService>.Instance);
        return (repo, retention);
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

    private static string GitOut(string cwd, params string[] args) => Run(cwd, args).Out;
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
}
