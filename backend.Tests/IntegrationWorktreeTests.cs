using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2832: integration never runs in the project's own (developer) checkout.
/// QS-100 reproduced the failure shape once more - two unrelated files edited
/// days earlier in the developer checkout made acceptance refuse the merge with
/// "Integration working tree has uncommitted changes; refusing to merge".
/// These tests drive real git against throwaway repositories: the policy matrix
/// is direct, everything else is end to end.
/// </summary>
public sealed class IntegrationWorktreeTests : IDisposable
{
    private const string Project = "Fixture";

    private readonly string _tempDir;
    private readonly string _worktreeRoot;
    private readonly string _watchPath;

    public IntegrationWorktreeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-worktree-" + Guid.NewGuid().ToString("N"));
        _worktreeRoot = Path.Combine(_tempDir, "studio-worktrees");
        _watchPath = Path.Combine(_tempDir, "project-store");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---------------------------------------------------------------- policy

    [Theory]
    // repositoryResolved, directoryExists, registered, hasGitLink -> action
    [InlineData(false, false, false, false, IntegrationWorktreeAction.Unavailable)]
    [InlineData(false, true, true, true, IntegrationWorktreeAction.Unavailable)]
    [InlineData(true, false, false, false, IntegrationWorktreeAction.Create)]
    [InlineData(true, false, true, false, IntegrationWorktreeAction.Recreate)]
    [InlineData(true, true, false, false, IntegrationWorktreeAction.Recreate)]
    [InlineData(true, true, false, true, IntegrationWorktreeAction.Recreate)]
    [InlineData(true, true, true, false, IntegrationWorktreeAction.Recreate)]
    [InlineData(true, true, true, true, IntegrationWorktreeAction.Reuse)]
    public void Policy_DecidesEveryCombinationOfTheObservableFacts(
        bool repositoryResolved,
        bool directoryExists,
        bool registered,
        bool hasGitLink,
        IntegrationWorktreeAction expected)
    {
        var decision = IntegrationWorktreePolicy.Decide(
            new IntegrationWorktreeFacts(repositoryResolved, directoryExists, registered, hasGitLink));

        Assert.Equal(expected, decision.Action);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    // ------------------------------------------------------------- lifecycle

    [Fact]
    public void Prepare_CreatesAStudioOwnedWorktreeOutsideTheDeveloperCheckout()
    {
        var repo = SeedRepo();
        var service = BuildWorktreeService(repo);

        var lease = service.Prepare(Project, repo);

        Assert.True(lease.Success, lease.Error);
        Assert.Equal(service.PathFor(Project), lease.Path);
        Assert.NotEqual(repo, lease.Path);
        Assert.True(Directory.Exists(lease.Path));
        // Detached, so it never claims a branch the developer may want to check out.
        Assert.Equal("HEAD", RunGit(lease.Path!, "rev-parse --abbrev-ref HEAD").Out.Trim());
    }

    [Fact]
    public void Prepare_ResetsALeftoverDirtyWorktreeInsteadOfFailing()
    {
        var repo = SeedRepo();
        var service = BuildWorktreeService(repo);
        var first = service.Prepare(Project, repo);
        Assert.True(first.Success, first.Error);

        // A crashed integration left tracked edits and stray files behind.
        File.WriteAllText(Path.Combine(first.Path!, "README.md"), "half-merged");
        File.WriteAllText(Path.Combine(first.Path!, "stray.txt"), "leftover");

        var second = service.Prepare(Project, repo);

        Assert.True(second.Success, second.Error);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal("seed", File.ReadAllText(Path.Combine(second.Path!, "README.md")));
        Assert.False(File.Exists(Path.Combine(second.Path!, "stray.txt")));
    }

    [Fact]
    public void Prepare_RecreatesTheWorktreeAfterItsDirectoryWasDeleted()
    {
        var repo = SeedRepo();
        var service = BuildWorktreeService(repo);
        var first = service.Prepare(Project, repo);
        Assert.True(first.Success, first.Error);

        Directory.Delete(first.Path!, recursive: true);

        var second = service.Prepare(Project, repo);

        Assert.True(second.Success, second.Error);
        Assert.True(Directory.Exists(second.Path));
        Assert.Contains(
            "README.md",
            Directory.EnumerateFiles(second.Path!).Select(Path.GetFileName));
    }

    [Fact]
    public void Prepare_WithoutARepository_FailsInsteadOfFallingBackToTheCheckout()
    {
        var service = BuildWorktreeService(Path.Combine(_tempDir, "not-a-repo"));

        var lease = service.Prepare(Project, Path.Combine(_tempDir, "not-a-repo"));

        Assert.False(lease.Success);
        Assert.Null(lease.Path);
        Assert.Contains("Integration worktree unavailable", lease.Error);
    }

    // ------------------------------------------------------- the QS-100 case

    [Fact]
    public void Merge_WithDirtyDeveloperCheckoutOnTheIntegrationBranch_IntegratesAndLeavesTheCheckoutAlone()
    {
        var repo = SeedRepo();
        var git = BuildGitService(repo);
        var service = BuildWorktreeService(repo);
        SeedDeliveryBranch(repo, "task/qs-100", "delivery.txt", "delivered work\n");

        // The developer sits on the integration branch with unrelated edits -
        // exactly the QS-100 shape.
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "README.md"), "developer edit\n");
        File.WriteAllText(Path.Combine(repo, "scratch.md"), "unrelated note\n");

        var lease = service.Prepare(Project, repo);
        Assert.True(lease.Success, lease.Error);

        var result = git.MergeBranchIntoIntegration(lease.Path!, "task/qs-100", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        // develop really advanced and contains the delivery.
        Assert.Equal(0, RunGit(repo, "merge-base --is-ancestor task/qs-100 develop").Code);
        // The developer's working tree was neither read as a blocker nor rewritten.
        Assert.Equal("developer edit\n", File.ReadAllText(Path.Combine(repo, "README.md")));
        Assert.Equal("unrelated note\n", File.ReadAllText(Path.Combine(repo, "scratch.md")));
        Assert.Equal("develop", RunGit(repo, "rev-parse --abbrev-ref HEAD").Out.Trim());
    }

    [Fact]
    public void Merge_WithDirtyDeveloperCheckoutOnAnotherBranch_StillTargetsTheIntegrationBranch()
    {
        var repo = SeedRepo();
        var git = BuildGitService(repo);
        var service = BuildWorktreeService(repo);
        SeedDeliveryBranch(repo, "task/qs-102", "other.txt", "delivered work\n");

        // The developer is on main, so nothing holds develop; the merge must
        // still land on develop and must not fast-forward main (QS-102).
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "scratch.md"), "unrelated note\n");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var lease = service.Prepare(Project, repo);
        Assert.True(lease.Success, lease.Error);

        var result = git.MergeBranchIntoIntegration(lease.Path!, "task/qs-102", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(0, RunGit(repo, "merge-base --is-ancestor task/qs-102 develop").Code);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal("unrelated note\n", File.ReadAllText(Path.Combine(repo, "scratch.md")));
    }

    [Fact]
    public async Task MergeRunner_WithDirtyDeveloperCheckout_IntegratesInsteadOfRefusing()
    {
        var repo = SeedRepo();
        var git = BuildGitService(repo);
        var runner = new MergeIntoDevelopRunner(
            git,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<MergeIntoDevelopRunner>.Instance,
            integrationWorktrees: BuildWorktreeService(repo));
        SeedDeliveryBranch(repo, "task/qs-100-runner", "delivery.txt", "delivered work\n");

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "scratch.md"), "unrelated note from three days ago\n");

        var jobFolder = Path.Combine(_watchPath, TaskStates.HumanReview, "qs-100-runner");
        Directory.CreateDirectory(jobFolder);

        var result = await runner.RunAsync(
            Project,
            "qs-100-runner",
            jobFolder,
            _watchPath,
            "develop",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(0, RunGit(repo, "merge-base --is-ancestor task/qs-100-runner develop").Code);
        Assert.Equal("unrelated note from three days ago\n", File.ReadAllText(Path.Combine(repo, "scratch.md")));
    }

    [Fact]
    public void Hygiene_ReportsTheDeveloperCheckoutDirtAsAHintAndNamesTheIntegrationWorktree()
    {
        var repo = SeedRepo();
        var git = BuildGitService(repo);
        var service = BuildWorktreeService(repo);
        File.WriteAllText(Path.Combine(repo, "scratch.md"), "unrelated note\n");

        var before = git.GetProjectHygiene(Project);
        Assert.True(before.IsDirty);
        Assert.Null(before.IntegrationWorktreePath);

        Assert.True(service.Prepare(Project, repo).Success);
        git.InvalidateHygieneCache();
        var after = git.GetProjectHygiene(Project);

        Assert.True(after.IsDirty);
        Assert.Equal(service.PathFor(Project), after.IntegrationWorktreePath);
    }

    [Fact]
    public void Hygiene_OnIntegrationBranch_TracksWhetherTheCheckoutHoldsTheBranchIntegrationAdvances()
    {
        var repo = SeedRepo(withOrigin: true);
        var git = BuildGitService(repo);

        // origin/HEAD resolves the integration branch to main; the checkout is
        // on it, so the hint says so.
        Assert.True(git.GetProjectHygiene(Project).OnIntegrationBranch);

        RunGit(repo, "checkout -q develop");
        git.InvalidateHygieneCache();

        Assert.False(git.GetProjectHygiene(Project).OnIntegrationBranch);
    }

    // ----------------------------------------------------------------- setup

    private string SeedRepo(bool withOrigin = false)
    {
        var repo = Path.Combine(_tempDir, "developer-checkout");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, "branch develop");
        if (withOrigin)
        {
            var origin = Path.Combine(_tempDir, "origin.git");
            RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{origin}\"");
            RunGit(repo, $"remote add origin \"{origin}\"");
            RunGit(repo, "push -q origin main");
            RunGit(repo, "remote set-head origin main");
        }
        return repo;
    }

    private void SeedDeliveryBranch(string repo, string branch, string file, string content)
    {
        var deliveryWorktree = Path.Combine(_tempDir, "delivery-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(repo, $"worktree add -q -b {branch} \"{deliveryWorktree}\" develop");
        File.WriteAllText(Path.Combine(deliveryWorktree, file), content);
        RunGit(deliveryWorktree, "add -A");
        RunGit(deliveryWorktree, $"commit -q -m \"feat: {branch}\"");
        RunGit(repo, $"worktree remove --force \"{deliveryWorktree}\"");
    }

    private IConfiguration BuildConfig(string repo)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = _watchPath,
            ["Integration:WorktreeRoot"] = _worktreeRoot,
        }).Build();

    private GitService BuildGitService(string repo)
    {
        var config = BuildConfig(repo);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private IntegrationWorktreeService BuildWorktreeService(string repo)
        => new(
            BuildGitService(repo),
            NullLogger<IntegrationWorktreeService>.Instance,
            BuildConfig(repo));

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
