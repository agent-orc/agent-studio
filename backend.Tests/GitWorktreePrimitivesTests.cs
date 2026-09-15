using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ADR-0052 slice 1 (config + worktree plumbing in GitService): real-worktree
/// coverage for the new low-level git primitives that the parallel-task model
/// builds on - worktree add/remove, rebase-onto, fast-forward merge, and the
/// branch-parameterized push. Every test drives git against a throwaway temp
/// repo so the behaviour is exercised end to end, not mocked.
/// </summary>
public sealed class GitWorktreePrimitivesTests : IDisposable
{
    private readonly string _tempDir;

    public GitWorktreePrimitivesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-worktree-primitives-" + Guid.NewGuid().ToString("N"));
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
    public void WorktreeAdd_CreatesIsolatedWorktreeOnNewBranch()
    {
        var repo = SeedRepo("add");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-add");

        var result = git.WorktreeAdd(repo, wtPath, "task/1", "main");

        Assert.True(result.Success, result.Error);
        Assert.Equal(wtPath, result.Path);
        Assert.True(Directory.Exists(wtPath));
        // The new branch exists and the seeded file is checked out in the worktree.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/1").Code);
        Assert.True(File.Exists(Path.Combine(wtPath, "README.md")));
    }

    [Fact]
    public void ResolveIntegrationBranch_DefaultDevelopMissing_FallsBackToMain()
    {
        var repo = SeedRepo("main-only");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-main-only");

        var resolved = git.ResolveIntegrationBranch(repo, "develop");
        var result = git.WorktreeAdd(repo, wtPath, "task/main-only", resolved);

        Assert.Equal("main", resolved);
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/main-only").Code);
        Assert.Equal(
            RunGit(repo, "rev-parse main").Out.Trim(),
            RunGit(wtPath, "rev-parse HEAD").Out.Trim());
    }

    [Fact]
    public void ResolveIntegrationBranch_ConfiguredBranchExists_WinsOverRepositoryDefault()
    {
        var repo = SeedRepo("configured-branch");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-configured");

        RunGit(repo, "checkout -q -b integration");
        File.WriteAllText(Path.Combine(repo, "integration.txt"), "integration branch");
        Commit(repo, "feat: integration branch");
        var integrationTip = RunGit(repo, "rev-parse integration").Out.Trim();
        RunGit(repo, "checkout -q main");

        var resolved = git.ResolveIntegrationBranch(repo, "integration");
        var result = git.WorktreeAdd(repo, wtPath, "task/configured", resolved);

        Assert.Equal("integration", resolved);
        Assert.True(result.Success, result.Error);
        Assert.Equal(integrationTip, RunGit(wtPath, "rev-parse HEAD").Out.Trim());
    }

    [Fact]
    public void ResolveIntegrationBranch_UnconfiguredUsesOriginHeadEvenWhenDevelopExists()
    {
        var repo = SeedRepo("origin-head-main");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "update-ref refs/remotes/origin/main refs/heads/main");
        RunGit(repo, "symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/main");

        var resolved = git.ResolveIntegrationBranch(repo, null);

        Assert.Equal("main", resolved);
    }

    [Fact]
    public void WorktreeAdd_PathWithSpaces_IsHandledByArgumentList()
    {
        var repo = SeedRepo("add-spaces");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "work tree with spaces");

        var result = git.WorktreeAdd(repo, wtPath, "task/2", "main");

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(wtPath));
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("bad..name")]
    [InlineData("trailing/")]
    [InlineData("with space")]
    public void WorktreeAdd_InvalidBranchName_FailsWithoutTouchingGit(string branch)
    {
        var repo = SeedRepo("add-invalid-" + Math.Abs(branch.GetHashCode()));
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-invalid");

        var result = git.WorktreeAdd(repo, wtPath, branch, "main");

        Assert.False(result.Success);
        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public void WorktreeRemove_TearsDownTheWorktree()
    {
        var repo = SeedRepo("remove");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-remove");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/3", "main").Success);

        var result = git.WorktreeRemove(repo, wtPath);

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(wtPath));
        // Branch ref survives removal so the work can be integrated separately.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/3").Code);
    }

    [Fact]
    public void RebaseOnto_ReplaysTaskBranchOnLatestIntegrationTip()
    {
        var repo = SeedRepo("rebase");
        var git = BuildGitService(("Fixture", repo));

        // main advances after the task branch was cut.
        var wtPath = Path.Combine(_tempDir, "wt-rebase");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/4", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");

        File.WriteAllText(Path.Combine(repo, "main.txt"), "main moved");
        Commit(repo, "chore: advance main");
        var mainTip = RunGit(repo, "rev-parse main").Out.Trim();

        var result = git.RebaseOnto(wtPath, "main");

        Assert.True(result.Success, result.Error);
        // The task branch now descends from the new main tip (linear history).
        Assert.Equal(0, RunGit(wtPath, $"merge-base --is-ancestor {mainTip} HEAD").Code);
    }

    [Fact]
    public void RebaseOnto_Conflict_AbortsAndLeavesWorktreeClean()
    {
        var repo = SeedRepo("rebase-conflict");
        var git = BuildGitService(("Fixture", repo));

        var wtPath = Path.Combine(_tempDir, "wt-conflict");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/5", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "shared.txt"), "task version");
        Commit(wtPath, "feat: task edits shared");

        // main edits the same file differently -> rebase must conflict.
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "main version");
        Commit(repo, "chore: main edits shared");

        var result = git.RebaseOnto(wtPath, "main");

        Assert.False(result.Success);
        // The rebase was aborted: no rebase is in progress in the worktree.
        Assert.NotEqual(0, RunGit(wtPath, "rev-parse --verify REBASE_HEAD").Code);
    }

    [Fact]
    public void MergeFastForward_FoldsRebasedBranchIntoIntegration()
    {
        var repo = SeedRepo("ff");
        var git = BuildGitService(("Fixture", repo));

        // Task branch is a pure descendant of main, so main can fast-forward.
        var wtPath = Path.Combine(_tempDir, "wt-ff");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/6", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");
        var taskTip = RunGit(wtPath, "rev-parse HEAD").Out.Trim();

        // repo has main checked out; fast-forward it to the task branch tip.
        var result = git.MergeFastForward(repo, "task/6");

        Assert.True(result.Success, result.Error);
        Assert.Equal(taskTip, RunGit(repo, "rev-parse main").Out.Trim());
    }

    [Fact]
    public void MergeFastForward_NonFastForward_FailsWithoutMergeCommit()
    {
        var repo = SeedRepo("ff-no");
        var git = BuildGitService(("Fixture", repo));

        var wtPath = Path.Combine(_tempDir, "wt-ff-no");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/7", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");

        // main diverges, so the task branch is no longer a fast-forward.
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main diverged");
        Commit(repo, "chore: diverge main");
        var mainTipBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var result = git.MergeFastForward(repo, "task/7");

        Assert.False(result.Success);
        // No merge commit was created; main is exactly where it was.
        Assert.Equal(mainTipBefore, RunGit(repo, "rev-parse main").Out.Trim());
    }

    [Fact]
    public async Task PushShaAsync_InvalidTargetBranch_IsRejectedBeforeAnyGitCall()
    {
        var repo = SeedRepo("push-guard");
        var git = BuildGitService(("Fixture", repo));
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var result = await git.PushShaAsync(sha, repo, default, targetBranch: "-not-a-branch");

        Assert.False(result.Success);
        Assert.Equal("invalid-branch", result.Status);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    public async Task PushShaAsync_PushesToRequestedBranch(string targetBranch)
    {
        var repo = SeedRepo("push-" + targetBranch);
        var bare = Path.Combine(_tempDir, "remote-" + targetBranch + ".git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var git = BuildGitService(("Fixture", repo));
        var result = await git.PushShaAsync(sha, repo, default, targetBranch: targetBranch);

        Assert.True(result.Success, result.Error);
        // The remote is bare; safe.bareRepository=explicit means we must name
        // the git dir explicitly rather than cd into it.
        Assert.Equal(sha, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/{targetBranch}").Out.Trim());
    }

    [Fact]
    public async Task PushShaAsync_MainWithDevelop_BlocksRawCandidate()
    {
        var repo = SeedRepo("push-main-lineage-guard");
        var bare = Path.Combine(_tempDir, "remote-main-lineage-guard.git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, "push -q -u origin main").Code);
        var publishedTip = RunGit(repo, "rev-parse HEAD").Out.Trim();
        Assert.Equal(0, RunGit(repo, "branch develop").Code);
        Assert.Equal(0, RunGit(repo, "push -q -u origin develop").Code);

        File.WriteAllText(Path.Combine(repo, "raw-delivery.txt"), "raw delivery");
        Commit(repo, "feat: raw delivery");
        var rawCandidate = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var git = BuildGitService(("Fixture", repo));
        var result = await git.PushShaAsync(rawCandidate, repo);

        Assert.False(result.Success);
        Assert.Equal("lineage-blocked", result.Status);
        Assert.Equal(publishedTip, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/main").Out.Trim());
        Assert.Equal(publishedTip, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/develop").Out.Trim());
    }

    [Fact]
    public async Task PushShaAsync_MainWithDevelop_AllowsExactPublishedDevelopTip()
    {
        var repo = SeedRepo("push-published-develop");
        var bare = Path.Combine(_tempDir, "remote-published-develop.git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, "push -q -u origin main").Code);
        Assert.Equal(0, RunGit(repo, "checkout -q -b develop").Code);
        File.WriteAllText(Path.Combine(repo, "integrated.txt"), "integrated delivery");
        Commit(repo, "merge: integrated delivery");
        var developTip = RunGit(repo, "rev-parse HEAD").Out.Trim();
        Assert.Equal(0, RunGit(repo, "push -q -u origin develop").Code);

        var git = BuildGitService(("Fixture", repo));
        var result = await git.PushShaAsync(developTip, repo);

        Assert.True(result.Success, result.Error);
        Assert.Equal(developTip, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/main").Out.Trim());
    }

    [Fact]
    public void MergeBranchIntoIntegration_CreatesMergeCommitOnDevelop()
    {
        var repo = SeedRepo("merge-dev");
        var git = BuildGitService(("Fixture", repo));

        // develop branches off main; task/10 adds a commit on its own file.
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/10");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        var taskTip = RunGit(repo, "rev-parse task/10").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var result = git.MergeBranchIntoIntegration(repo, "task/10", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        // develop now contains the task tip and the new HEAD is a --no-ff merge
        // commit (two parents), so the delivery is a single revertable commit.
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {taskTip} develop").Code);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code);
        Assert.Equal(result.MergedSha, RunGit(repo, "rev-parse develop").Out.Trim());
        // Working tree is clean after the merge.
        Assert.False(git.RepoHasUncommittedChanges(repo));
    }

    [Fact]
    public void MergeBranchIntoIntegration_BehindCurrentTarget_DirectMergePreservesDeliverySha()
    {
        var repo = SeedRepo("merge-behind-target");
        var git = BuildGitService(("Fixture", repo));

        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/behind");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        var originalTaskTip = RunGit(repo, "rev-parse task/behind").Out.Trim();

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "develop.txt"), "integration advanced");
        Commit(repo, "chore: advance integration target");

        var result = git.MergeBranchIntoIntegration(repo, "task/behind", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Empty(result.RebasedCommits);
        Assert.Equal(originalTaskTip, RunGit(repo, "rev-parse develop^2").Out.Trim());
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {originalTaskTip} develop").Code);
        Assert.False(git.RepoHasUncommittedChanges(repo));
    }

    [Fact]
    public void MergeBranchIntoIntegration_DirectConflict_UsesRerereMergeWithoutRewritingDelivery()
    {
        var repo = SeedRepo("merge-rerere");
        var git = BuildGitService(("Fixture", repo));

        File.WriteAllText(Path.Combine(repo, "shared.txt"), "base\n");
        Commit(repo, "chore: add shared base");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/rerere");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "task version\n");
        Commit(repo, "feat: task edits shared");
        var deliverySha = RunGit(repo, "rev-parse task/rerere").Out.Trim();

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version\n");
        Commit(repo, "chore: develop edits shared");
        RunGit(repo, "config rerere.enabled true");
        RunGit(repo, "config rerere.autoupdate true");

        var training = RunGit(repo, "merge --no-ff --no-commit task/rerere");
        Assert.NotEqual(0, training.Code);
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "combined resolution\n");
        RunGit(repo, "add shared.txt");
        RunGit(repo, "rerere");
        RunGit(repo, "merge --abort");

        var result = git.MergeBranchIntoIntegration(repo, "task/rerere", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Empty(result.RebasedCommits);
        Assert.Equal(deliverySha, RunGit(repo, "rev-parse develop^2").Out.Trim());
        Assert.Equal(deliverySha, RunGit(repo, "rev-parse task/rerere").Out.Trim());
        Assert.Equal("combined resolution", File.ReadAllText(Path.Combine(repo, "shared.txt")).Trim());
        Assert.False(git.RepoHasUncommittedChanges(repo));
    }

    [Fact]
    public void MergeBranchIntoIntegration_AlreadyContained_IsIdempotentNoOp()
    {
        var repo = SeedRepo("merge-already");
        var git = BuildGitService(("Fixture", repo));

        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        // task/11 points at develop's tip, so it is already fully contained.
        RunGit(repo, "branch task/11 develop");
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = git.MergeBranchIntoIntegration(repo, "task/11", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, result.Outcome);
        // develop did not move; no spurious merge commit was created.
        Assert.Equal(developTip, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void MergeBranchIntoIntegration_UnresolvedConflict_RequestsAgentRoundAndLeavesTreeClean()
    {
        var repo = SeedRepo("merge-conflict");
        var git = BuildGitService(("Fixture", repo));

        RunGit(repo, "checkout -q -b develop");
        // task/12 edits shared.txt one way ...
        RunGit(repo, "checkout -q -b task/12");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "task version");
        Commit(repo, "feat: task edits shared");
        var taskTipBefore = RunGit(repo, "rev-parse task/12").Out.Trim();
        // ... develop edits the same file differently -> merge must conflict.
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();
        var worktreesBefore = git.ListWorktrees(repo).Select(item => item.Path).ToArray();

        var result = git.MergeBranchIntoIntegration(repo, "task/12", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.AgentRoundRequired, result.Outcome);
        // The conflict is made visible, not silent.
        Assert.Contains("shared.txt", result.ConflictedFiles);
        // The merge was aborted: develop is unchanged, the tree is clean, and no
        // merge is in progress (MERGE_HEAD gone).
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(taskTipBefore, RunGit(repo, "rev-parse task/12").Out.Trim());
        Assert.False(git.RepoHasUncommittedChanges(repo));
        Assert.NotEqual(0, RunGit(repo, "rev-parse --verify MERGE_HEAD").Code);
        Assert.Equal(worktreesBefore, git.ListWorktrees(repo).Select(item => item.Path));
    }

    [Fact]
    public void MergeBranchIntoIntegration_NoTaskBranch_ReturnsNoTaskBranch()
    {
        var repo = SeedRepo("merge-no-branch");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");

        var result = git.MergeBranchIntoIntegration(repo, "task/does-not-exist", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.NoTaskBranch, result.Outcome);
    }

    [Fact]
    public void MergeBranchIntoIntegration_DirtyTree_RefusesWithError()
    {
        var repo = SeedRepo("merge-dirty");
        var git = BuildGitService(("Fixture", repo));

        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/13");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        // Leave an uncommitted edit in the integration working tree.
        File.WriteAllText(Path.Combine(repo, "README.md"), "operator edit in flight");
        File.WriteAllText(Path.Combine(repo, "untracked-notes.txt"), "service evidence");

        var result = git.MergeBranchIntoIntegration(repo, "task/13", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Contains("README.md", result.Error);
        Assert.Contains("untracked-notes.txt", result.Error);
    }

    [Fact]
    public void MergeBranchFastForward_InIntegrationWorktree_AdvancesTheReleaseBranch()
    {
        var repo = SeedRepo("release-ff-worktree");
        var git = BuildGitService(("Fixture", repo));

        // develop carries the tested release commit; main is one commit behind.
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "release.txt"), "tested release work");
        Commit(repo, "feat: tested release work");
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        var mainTipBefore = RunGit(repo, "rev-parse main").Out.Trim();
        // The developer checkout keeps unrelated work in flight; integration
        // runs elsewhere and must not care (AGT-2832).
        File.WriteAllText(Path.Combine(repo, "scratch.txt"), "uncommitted developer work");

        // The Studio-owned integration worktree is detached, so the release
        // merge never takes a branch away from the developer checkout.
        var integration = Path.Combine(_tempDir, "integration-release-ff");
        Assert.Equal(0, RunGit(repo, $"worktree add -q --detach \"{integration}\" main").Code);

        var result = git.MergeBranchFastForward(integration, "develop", "main", developTip, mainTipBefore);

        Assert.Null(result.Error);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(developTip, result.MergedSha);
        // The branch ref itself advanced, published from the detached head.
        Assert.Equal(developTip, RunGit(repo, "rev-parse refs/heads/main").Out.Trim());
        // The developer checkout kept its branch and its uncommitted file.
        Assert.Equal("develop", RunGit(repo, "rev-parse --abbrev-ref HEAD").Out.Trim());
        Assert.True(File.Exists(Path.Combine(repo, "scratch.txt")));
    }

    [Fact]
    public void MergeBranchFastForward_DirtyDeveloperCheckoutHoldsRelease_AdvancesBranchByReference()
    {
        var repo = SeedRepo("release-ff-dirty-holder");
        var git = BuildGitService(("Fixture", repo));

        // The release commit touches README.md, the same file the developer is
        // editing, so git refuses to carry the checkout along.
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "README.md"), "released readme");
        Commit(repo, "feat: tested readme release");
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        var mainTipBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "operator edit in flight");

        var integration = Path.Combine(_tempDir, "integration-dirty-holder");
        Assert.Equal(0, RunGit(repo, $"worktree add -q --detach \"{integration}\" main").Code);

        var result = git.MergeBranchFastForward(integration, "develop", "main", developTip, mainTipBefore);

        // The dirty developer checkout does not block the release.
        Assert.Null(result.Error);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(developTip, RunGit(repo, "rev-parse refs/heads/main").Out.Trim());
        // It keeps the branch and the uncommitted edit: the ref moved
        // underneath it, nothing in its working tree was overwritten.
        Assert.Equal("main", RunGit(repo, "rev-parse --abbrev-ref HEAD").Out.Trim());
        Assert.Equal("operator edit in flight", File.ReadAllText(Path.Combine(repo, "README.md")));
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

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private GitService BuildGitService(params (string Name, string RepoPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:RepositoryPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RepoPath, ".orchestrator", "jobs");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
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
