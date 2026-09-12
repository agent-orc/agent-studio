using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ADR-0052 slice 2: the worktree pre-step + merge / cleanup post-steps
/// (<see cref="WorktreeTaskLifecycle"/>) driven end to end against a throwaway
/// temp repo. Covers the direct-merge happy path, the rebase-replay path when
/// the integration branch advances under a running task, the conflict path, the
/// pull-request (no auto-merge) path, and teardown.
/// </summary>
public sealed class WorktreeTaskLifecycleTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeTaskLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "worktree-lifecycle-" + Guid.NewGuid().ToString("N"));
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
    public void Prepare_CreatesWorktreeOnTaskBranchOffIntegrationBranch()
    {
        var (repo, life) = SeedWithDevelop("prepare");

        var prep = life.Prepare(repo, "ATP-101", "develop", WorktreeRoot());

        Assert.True(prep.Success, prep.Error);
        Assert.Equal("task/ATP-101", prep.Branch);
        Assert.False(prep.Reused); // a fresh cut, not a reuse
        Assert.True(Directory.Exists(prep.WorktreePath));
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/ATP-101").Code);
        // Branch was cut from develop, not main: its merge-base is develop's tip.
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.Equal(0, RunGit(prep.WorktreePath!, $"merge-base --is-ancestor {developTip} HEAD").Code);
    }

    [Fact]
    public void TwoParallelLocalFolderRuns_SameProject_NeverSeeEachOthersUncommittedFiles()
    {
        var (repo, life) = SeedWithDevelop("parallel-isolation");
        var taskStorage = Path.Combine(_tempDir, "central-task-storage", "PROJ-015");
        Directory.CreateDirectory(taskStorage);
        var entry = new WatchPathEntry
        {
            Name = "Token Economy",
            Path = taskStorage,
            RootPath = taskStorage,
            RepositoryPath = repo,
        };
        var repositoryRoot = BuildGitService(repo).ResolveRepositoryRoot(entry);
        var worktreeRoot = WorktreeRoot();
        var first = life.Prepare(repositoryRoot!, "TE-20", "develop", worktreeRoot);
        var second = life.Prepare(repositoryRoot!, "TE-21", "develop", worktreeRoot);

        Assert.Equal(Path.GetFullPath(repo), repositoryRoot);
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.WorktreePath, second.WorktreePath);
        Assert.Equal(
            RunGit(first.WorktreePath!, "rev-parse HEAD").Out.Trim(),
            RunGit(second.WorktreePath!, "rev-parse HEAD").Out.Trim());

        Parallel.Invoke(
            () => File.WriteAllText(Path.Combine(first.WorktreePath!, "first-run.txt"), "first"),
            () => File.WriteAllText(Path.Combine(second.WorktreePath!, "second-run.txt"), "second"));

        Assert.True(File.Exists(Path.Combine(first.WorktreePath!, "first-run.txt")));
        Assert.False(File.Exists(Path.Combine(first.WorktreePath!, "second-run.txt")));
        Assert.True(File.Exists(Path.Combine(second.WorktreePath!, "second-run.txt")));
        Assert.False(File.Exists(Path.Combine(second.WorktreePath!, "first-run.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot!, "first-run.txt")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot!, "second-run.txt")));
        Assert.False(File.Exists(Path.Combine(taskStorage, "first-run.txt")));
        Assert.False(File.Exists(Path.Combine(taskStorage, "second-run.txt")));
    }

    [Fact]
    public void ResolveRepositoryRoot_LocalTaskStoragePath_DoesNotBypassConfiguredRepository()
    {
        var (repo, _) = SeedWithDevelop("repository-resolution");
        var taskStorage = Path.Combine(_tempDir, "central-task-storage", "PROJ-015");
        Directory.CreateDirectory(taskStorage);
        var git = BuildGitService(repo);
        var entry = new WatchPathEntry
        {
            Name = "Token Economy",
            Path = taskStorage,
            RootPath = taskStorage,
            RepositoryPath = repo,
        };

        var resolved = git.ResolveRepositoryRoot(entry);

        Assert.Equal(Path.GetFullPath(repo), resolved);
        Assert.NotEqual(Path.GetFullPath(taskStorage), resolved);
        Assert.Null(git.ResolveRepositoryRoot(entry with { RepositoryPath = "" }));
    }

    [Fact]
    public void DirectMerge_FoldsTaskBranchIntoDevelop_ThenTeardownRemovesEverything()
    {
        var (repo, life) = SeedWithDevelop("direct");
        var prep = life.Prepare(repo, "task-7", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var taskTip = RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(taskTip, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(taskTip, result.IntegratedSha);

        var teardown = life.Teardown(repo, prep.WorktreePath!, prep.Branch, deleteBranch: true, force: true);
        Assert.True(teardown.Success, teardown.Error);
        Assert.False(Directory.Exists(prep.WorktreePath));
        Assert.NotEqual(0, RunGit(repo, "rev-parse --verify task/task-7").Code);
    }

    [Fact]
    public async Task PushTaskBranchWithRetry_PushesTaskBranchToOrigin()
    {
        var (repo, life) = SeedWithDevelop("branch-push");
        var bare = AddBareOrigin(repo, "branch-push-origin");
        var prep = life.Prepare(repo, "task-push", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var taskTip = RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim();

        var pushed = await life.PushTaskBranchWithRetryAsync(
            repo,
            taskTip,
            prep.Branch!,
            CancellationToken.None,
            attempts: 2,
            retryDelay: TimeSpan.Zero);

        Assert.True(pushed.Success, pushed.Error);
        Assert.Equal(taskTip, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/{prep.Branch}").Out.Trim());
    }

    [Fact]
    public async Task TeardownIfIntegrated_RemovesRemoteTaskBranch_WhenMerged()
    {
        var (repo, life) = SeedWithDevelop("remote-cleanup");
        var bare = AddBareOrigin(repo, "remote-cleanup-origin");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-remote-cleanup", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var taskTip = RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim();
        var push = await life.PushTaskBranchWithRetryAsync(
            repo,
            taskTip,
            prep.Branch!,
            CancellationToken.None,
            attempts: 2,
            retryDelay: TimeSpan.Zero);
        Assert.True(push.Success, push.Error);
        Assert.Equal(taskTip, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/{prep.Branch}").Out.Trim());
        Assert.Equal(IntegrationOutcome.Merged,
            life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge).Outcome);

        var td = life.TeardownIfIntegrated(repo, "task-remote-cleanup", "develop", wtRoot);

        Assert.True(td.Success, td.Error);
        Assert.False(Directory.Exists(prep.WorktreePath));
        Assert.NotEqual(0, RunGit(repo, "rev-parse --verify task/task-remote-cleanup").Code);
        Assert.NotEqual(0, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse --verify refs/heads/{prep.Branch}").Code);
    }

    [Fact]
    public void DirectMerge_RebasesOntoAdvancedDevelop_KeepsLinearHistory()
    {
        var (repo, life) = SeedWithDevelop("advance");
        var prep = life.Prepare(repo, "task-8", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        // Task does its work in the worktree.
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "task.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");

        // Meanwhile develop advances in the main checkout (a sibling task merged).
        File.WriteAllText(Path.Combine(repo, "other.txt"), "other work");
        Commit(repo, "feat: sibling work on develop");
        var advancedTip = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Merged, result.Outcome);
        // develop now contains both the sibling work and the rebased task work,
        // with the sibling commit as an ancestor (linear history, no merge commit).
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {advancedTip} develop").Code);
        Assert.True(File.Exists(Path.Combine(repo, "task.txt")));
        Assert.True(File.Exists(Path.Combine(repo, "other.txt")));
    }

    [Fact]
    public void DirectMerge_Conflict_LeavesDevelopUntouched_AndKeepsBranch()
    {
        var (repo, life) = SeedWithDevelop("conflict", seedShared: true);
        var prep = life.Prepare(repo, "task-9", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "task version");
        Commit(prep.WorktreePath!, "feat: task edits shared");

        // develop edits the same file differently -> rebase must conflict.
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Conflict, result.Outcome);
        Assert.Contains("shared.txt", result.ConflictedFiles ?? Array.Empty<string>());
        // develop is exactly where it was; nothing was merged.
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        // The branch survives so a conflict-resolution agent / PR can pick it up.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-9").Code);
        // The worktree was left clean (rebase aborted): no rebase in progress.
        Assert.NotEqual(0, RunGit(prep.WorktreePath!, "rev-parse --verify REBASE_HEAD").Code);

        var watchPath = Path.Combine(repo, ".orchestrator", "jobs");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(watchPath, state));
        var jobFolder = Path.Combine(watchPath, TaskStates.AutoReview, "task-9");
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            $"{{\"id\":\"task-9\",\"title\":\"conflicted task\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"codex\"}}");

        var info = new TaskInfo
        {
            Id = "task-9",
            Title = "conflicted task",
            State = TaskStates.AutoReview,
            FolderPath = jobFolder,
            WatchPath = watchPath
        };
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var issueMessage = ProjectRunner.BuildWorktreeIntegrationIssueMessage(
            "Worktree branch integration is blocked by a merge conflict.",
            prep.Branch,
            prep.WorktreePath,
            "develop",
            result);

        Assert.True(chatLog.Append(info, OrchestratorMessageKind.IntegrationConflict, issueMessage));
        var persistedLog = File.ReadAllText(TaskPaths.CliOutputLog(jobFolder));
        Assert.Contains("[integration-conflict]", persistedLog);
        Assert.Contains("task/task-9", persistedLog);
        Assert.Contains(prep.WorktreePath!, persistedLog);
        Assert.Contains("shared.txt", persistedLog);

        var issue = BuildScanner(repo).FindJob("task-9", watchPath)?.OutcomeIssue;
        Assert.NotNull(issue);
        Assert.Equal("integration-conflict", issue!.Kind);
        Assert.Equal("High", issue.Severity);
        Assert.Contains("task/task-9", issue.Summary);
    }

    [Fact]
    public void DirectMerge_Conflict_WhenPreserved_LeavesRebaseInProgress_ForResolver()
    {
        var (repo, life) = SeedWithDevelop("conflict-preserved", seedShared: true);
        var prep = life.Prepare(repo, "task-preserve", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "task version");
        Commit(prep.WorktreePath!, "feat: task edits shared");

        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(
            repo,
            prep.WorktreePath!,
            prep.Branch!,
            "develop",
            IntegrationStrategies.DirectMerge,
            preserveConflictForResolution: true);

        Assert.Equal(IntegrationOutcome.Conflict, result.Outcome);
        Assert.Contains("shared.txt", result.ConflictedFiles ?? Array.Empty<string>());
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Contains("UU shared.txt", RunGit(prep.WorktreePath!, "status --porcelain").Out);
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(prep.WorktreePath!, "shared.txt")));
    }

    [Fact]
    public void CompleteIntegrationAfterResolution_UnresolvedConflict_ReportsConflictFiles()
    {
        var (repo, life) = SeedWithDevelop("conflict-unresolved", seedShared: true);
        var prep = life.Prepare(repo, "task-unresolved", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "task version");
        Commit(prep.WorktreePath!, "feat: task edits shared");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        Assert.Equal(IntegrationOutcome.Conflict, life.Integrate(
            repo,
            prep.WorktreePath!,
            prep.Branch!,
            "develop",
            IntegrationStrategies.DirectMerge,
            preserveConflictForResolution: true).Outcome);

        var result = life.CompleteIntegrationAfterResolution(repo, prep.WorktreePath!, prep.Branch!, "develop");

        Assert.Equal(IntegrationOutcome.Conflict, result.Outcome);
        Assert.Contains("shared.txt", result.ConflictedFiles ?? Array.Empty<string>());
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void CompleteIntegrationAfterResolution_ResolvedRebase_FastForwardsDevelop()
    {
        var (repo, life) = SeedWithDevelop("conflict-resolved", seedShared: true);
        var prep = life.Prepare(repo, "task-resolved", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "task version");
        Commit(prep.WorktreePath!, "feat: task edits shared");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");

        Assert.Equal(IntegrationOutcome.Conflict, life.Integrate(
            repo,
            prep.WorktreePath!,
            prep.Branch!,
            "develop",
            IntegrationStrategies.DirectMerge,
            preserveConflictForResolution: true).Outcome);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "develop version + task version");
        RunGit(prep.WorktreePath!, "add shared.txt");

        var result = life.CompleteIntegrationAfterResolution(repo, prep.WorktreePath!, prep.Branch!, "develop");

        Assert.Equal(IntegrationOutcome.Merged, result.Outcome);
        Assert.Equal("develop version + task version", File.ReadAllText(Path.Combine(repo, "shared.txt")));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(prep.WorktreePath!, "status --porcelain").Out));
        Assert.Equal(RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim(), RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void PullRequestStrategy_DoesNotAutoMerge()
    {
        var (repo, life) = SeedWithDevelop("pr");
        var prep = life.Prepare(repo, "task-10", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.PullRequest);

        Assert.Equal(IntegrationOutcome.PushedForReview, result.Outcome);
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void Prepare_EmptyTaskId_FailsWithoutTouchingGit()
    {
        var (repo, life) = SeedWithDevelop("empty");
        var prep = life.Prepare(repo, "   ", "develop", WorktreeRoot());
        Assert.False(prep.Success);
    }

    [Fact]
    public void PrepareOrReuse_FreshCut_WhenNoBranchExists()
    {
        var (repo, life) = SeedWithDevelop("por-fresh");
        var wtRoot = WorktreeRoot();

        var prep = life.PrepareOrReuse(repo, "ATP-201", "develop", wtRoot);

        Assert.True(prep.Success, prep.Error);
        Assert.Equal("task/ATP-201", prep.Branch);
        Assert.False(prep.Reused); // no prior branch/worktree -> fresh cut
        Assert.True(Directory.Exists(prep.WorktreePath));
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/ATP-201").Code);
    }

    [Fact]
    public void PrepareOrReuse_ReusesLiveWorktree_OnResume_NoBranchExistsFailure()
    {
        // The resume bug: the second prepare must NOT fail with "branch already
        // exists" and must hand back the SAME worktree, preserving the branch's
        // commits (no fallback to a fresh checkout).
        var (repo, life) = SeedWithDevelop("por-resume");
        var wtRoot = WorktreeRoot();

        var first = life.PrepareOrReuse(repo, "task-resume", "develop", wtRoot);
        Assert.True(first.Success, first.Error);
        Assert.False(first.Reused); // first run cuts the worktree fresh
        File.WriteAllText(Path.Combine(first.WorktreePath!, "work.txt"), "run 1 work");
        Commit(first.WorktreePath!, "feat: run 1");
        var tipAfterRun1 = RunGit(first.WorktreePath!, "rev-parse HEAD").Out.Trim();

        var second = life.PrepareOrReuse(repo, "task-resume", "develop", wtRoot);

        Assert.True(second.Success, second.Error);
        Assert.Equal(first.WorktreePath, second.WorktreePath);
        Assert.Equal("task/task-resume", second.Branch);
        Assert.True(second.Reused); // resume re-uses the live worktree -> session may resume
        // The reused worktree still carries run 1's commit (history preserved).
        Assert.Equal(tipAfterRun1, RunGit(second.WorktreePath!, "rev-parse HEAD").Out.Trim());
        Assert.True(File.Exists(Path.Combine(second.WorktreePath!, "work.txt")));
    }

    [Fact]
    public void PrepareOrReuse_ReAttachesExistingBranch_AfterWorktreeRemoved()
    {
        // Worktree torn down but branch kept (e.g. an unmerged run): a resume
        // must re-attach the existing branch, not re-cut it off develop.
        var (repo, life) = SeedWithDevelop("por-reattach");
        var wtRoot = WorktreeRoot();

        var first = life.PrepareOrReuse(repo, "task-reattach", "develop", wtRoot);
        Assert.True(first.Success, first.Error);
        File.WriteAllText(Path.Combine(first.WorktreePath!, "work.txt"), "kept work");
        Commit(first.WorktreePath!, "feat: kept work");
        var tip = RunGit(first.WorktreePath!, "rev-parse HEAD").Out.Trim();

        // Remove the worktree but keep the branch (mirrors a partial teardown).
        Assert.True(life.Teardown(repo, first.WorktreePath!, first.Branch, deleteBranch: false, force: true).Success);
        Assert.False(Directory.Exists(first.WorktreePath));
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-reattach").Code);

        var second = life.PrepareOrReuse(repo, "task-reattach", "develop", wtRoot);

        Assert.True(second.Success, second.Error);
        Assert.Equal("task/task-reattach", second.Branch);
        Assert.True(second.Reused); // re-attaching an existing branch is a reuse
        // Re-attached the SAME branch with its commit, not a fresh cut off develop.
        Assert.Equal(tip, RunGit(second.WorktreePath!, "rev-parse HEAD").Out.Trim());
    }

    [Fact]
    public void PrepareOrReuse_ReAttachesExistingBranch_AfterOrphanDirectoryAtCanonicalPath()
    {
        var (repo, life) = SeedWithDevelop("por-orphan");
        var wtRoot = WorktreeRoot();
        var taskId = "task-orphan";
        var orphanPath = Path.Combine(wtRoot, taskId);

        Assert.Equal(0, RunGit(repo, $"branch {WorktreeTaskLifecycle.BranchFor(taskId)} develop").Code);
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "orphan.txt"), "stale worktree directory");

        var prep = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);

        Assert.True(prep.Success, prep.Error);
        Assert.Equal(WorktreeTaskLifecycle.BranchFor(taskId), prep.Branch);
        Assert.True(prep.Reused); // pre-existing branch re-attached -> reuse, not fresh cut
        Assert.Equal(orphanPath, prep.WorktreePath);
        Assert.False(File.Exists(Path.Combine(orphanPath, "orphan.txt")));
        Assert.Equal(WorktreeTaskLifecycle.BranchFor(taskId), RunGit(prep.WorktreePath!, "branch --show-current").Out.Trim());
    }

    [Fact]
    public void PrepareOrReuse_RecutsMergedStaleBranch_AfterOrphanDir()
    {
        // S11 (AGT-1785): a branch folded into develop AND strictly behind its tip
        // (a failed/escalated leftover) must NOT be re-attached onto stale code —
        // it is deleted and fresh-cut off the CURRENT develop tip.
        var (repo, life) = SeedWithDevelop("por-recut");
        var wtRoot = WorktreeRoot();
        var taskId = "task-recut";
        var branch = WorktreeTaskLifecycle.BranchFor(taskId);

        // Branch at the OLD develop tip, then advance develop so it is merged+stale.
        Assert.Equal(0, RunGit(repo, $"branch {branch} develop").Code);
        File.WriteAllText(Path.Combine(repo, "newer.txt"), "advanced develop");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m advance");
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();

        var orphanPath = Path.Combine(wtRoot, taskId);
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "orphan.txt"), "stale");

        var prep = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);

        Assert.True(prep.Success, prep.Error);
        Assert.False(prep.Reused); // merged-stale branch deleted -> fresh cut
        Assert.True(Directory.Exists(prep.WorktreePath));
        Assert.False(File.Exists(Path.Combine(orphanPath, "orphan.txt")));
        // Fresh branch is cut off the CURRENT develop tip, not the stale one.
        Assert.Equal(0, RunGit(prep.WorktreePath!, $"merge-base --is-ancestor {developTip} HEAD").Code);
    }

    [Fact]
    public void PrepareOrReuse_PreservesUnmergedBranch_AfterOrphanDir()
    {
        // S11: a branch with commits NOT on develop (real in-progress work) must
        // survive the self-heal — re-attached, never deleted.
        var (repo, life) = SeedWithDevelop("por-keepwork");
        var wtRoot = WorktreeRoot();
        var taskId = "task-keepwork";

        var first = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);
        Assert.True(first.Success, first.Error);
        File.WriteAllText(Path.Combine(first.WorktreePath!, "work.txt"), "unmerged work");
        Commit(first.WorktreePath!, "feat: unmerged");
        var taskTip = RunGit(first.WorktreePath!, "rev-parse HEAD").Out.Trim();

        // Orphan the dir out-of-band: drop the registration, leave a stale dir.
        RunGit(repo, $"worktree remove --force \"{first.WorktreePath}\"");
        Directory.CreateDirectory(first.WorktreePath!);
        File.WriteAllText(Path.Combine(first.WorktreePath!, "orphan.txt"), "stale dir");

        var second = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);

        Assert.True(second.Success, second.Error);
        Assert.True(second.Reused); // unmerged branch preserved + re-attached
        Assert.Equal(taskTip, RunGit(second.WorktreePath!, "rev-parse HEAD").Out.Trim());
    }

    // MachineBound 23.07.: FileShare.None blocks orphan-directory removal on
    // Windows, while Linux permits unlinking the open file.
    [Trait("Category", "MachineBound")]
    [Fact]
    public void PrepareOrReuse_RejectsCleanly_WhenOrphanDirCannotBeRenamed()
    {
        // S11: a holder (e.g. a leftover capture server) keeping the orphan dir
        // busy can prevent both deletion and rename. That must yield a precise
        // reject, not a confusing 'already exists' collision or an exception.
        // The "busy" semantics are Windows-specific: POSIX happily removes a
        // directory whose files are open, so the reject this test asserts can
        // never occur there (surfaced by remote executor worktrees on Linux).
        if (!OperatingSystem.IsWindows()) return;
        var (repo, life) = SeedWithDevelop("por-busy");
        var wtRoot = WorktreeRoot();
        var taskId = "task-busy";
        var canonical = Path.Combine(wtRoot, taskId);
        Directory.CreateDirectory(canonical);
        var lockedFile = Path.Combine(canonical, "locked.txt");
        File.WriteAllText(lockedFile, "held");

        using var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var prep = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);

        Assert.False(prep.Success);
        Assert.Equal(canonical, prep.WorktreePath);
        Assert.NotNull(prep.Error);
        Assert.Contains("busy", prep.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Teardown_WhenDeleteRetriesFail_RenamesAside_AndNextPrepareSucceeds()
    {
        var (repo, _) = SeedWithDevelop("stale-rename");
        var wtRoot = WorktreeRoot();
        const string taskId = "task-stale-rename";
        var canonical = Path.Combine(wtRoot, taskId);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "held.txt"), "simulated holder");
        var stamp = new DateTime(2026, 9, 12, 12, 34, 56, DateTimeKind.Utc);
        var cleanup = new WorktreeDirectoryCleanup(
            _ => throw new IOException("simulated access denied"),
            () => stamp);

        var teardownLife = new WorktreeTaskLifecycle(
            BuildGitService(repo),
            NullLogger<WorktreeTaskLifecycle>.Instance,
            cleanup);

        var result = teardownLife.Teardown(
            repo,
            canonical,
            taskBranch: null,
            deleteBranch: false,
            force: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(canonical + ".stale-20260912123456000", result.StalePath);
        Assert.False(Directory.Exists(canonical));
        Assert.True(Directory.Exists(result.StalePath));

        var life = new WorktreeTaskLifecycle(
            BuildGitService(repo),
            NullLogger<WorktreeTaskLifecycle>.Instance);
        var prep = life.PrepareOrReuse(repo, taskId, "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        Assert.Equal(canonical, prep.WorktreePath);
    }

    [Fact]
    public void CommitInWorktree_ThenIntegrate_BringsTaskCommitToDevelop()
    {
        // Fix-acceptance #3: after a worktree run the task branch has >= 1 commit
        // over develop, and Integrate folds it into develop.
        var (repo, life) = SeedWithDevelop("commit-integrate");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-ci", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        // Simulate the agent's (uncommitted) edits, then the runner's
        // commit-in-worktree onto the task branch.
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "agent.txt"), "agent edits");
        Commit(prep.WorktreePath!, "feat: agent work");

        // The task branch is now ahead of develop by exactly one commit.
        Assert.NotEqual(developBefore, RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim());
        Assert.Equal("1", RunGit(repo, "rev-list --count develop..task/task-ci").Out.Trim());

        var res = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Merged, res.Outcome);
        Assert.True(File.Exists(Path.Combine(repo, "agent.txt")));
    }

    [Fact]
    public void TeardownIfIntegrated_RemovesWorktreeAndBranch_WhenMerged()
    {
        var (repo, life) = SeedWithDevelop("tdi-merged");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-tdi", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "f.txt"), "work");
        Commit(prep.WorktreePath!, "feat: work");
        Assert.Equal(IntegrationOutcome.Merged,
            life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge).Outcome);

        var td = life.TeardownIfIntegrated(repo, "task-tdi", "develop", wtRoot);

        Assert.True(td.Success, td.Error);
        Assert.False(Directory.Exists(prep.WorktreePath));
        Assert.NotEqual(0, RunGit(repo, "rev-parse --verify task/task-tdi").Code);
    }

    [Fact]
    public void TeardownIfIntegrated_KeepsUnmergedBranch_ForResolution()
    {
        // A branch whose work never landed on develop must survive terminal
        // teardown so a human / conflict agent can still pick it up.
        var (repo, life) = SeedWithDevelop("tdi-unmerged");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-keep", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "f.txt"), "unmerged work");
        Commit(prep.WorktreePath!, "feat: unmerged");

        // No Integrate -> branch is ahead of develop, not an ancestor.
        var td = life.TeardownIfIntegrated(repo, "task-keep", "develop", wtRoot);

        Assert.True(td.Success, td.Error);
        // Branch preserved (work not dropped).
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-keep").Code);
    }

    [Fact]
    public void TeardownIfIntegrated_NoBranch_IsCleanNoOp()
    {
        var (repo, life) = SeedWithDevelop("tdi-noop");
        var td = life.TeardownIfIntegrated(repo, "never-ran", "develop", WorktreeRoot());
        Assert.True(td.Success, td.Error);
    }

    [Fact]
    public void TeardownIfIntegrated_PreservesUncommittedWork_OnTaskBranch()
    {
        // AGT-1945 regression: a run whose auto-commit failed/was skipped leaves
        // the deliverable ONLY as uncommitted changes in the worktree. The task
        // branch tip then still equals develop, so a naive "is the branch merged?"
        // check reads it as safe-to-remove and the force-remove wipes the work
        // irreversibly. Terminal teardown MUST snapshot the work onto task/<id>
        // first so a reissue / human review can still reach it.
        var (repo, life) = SeedWithDevelop("tdi-dirty");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-dirty", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);

        // Uncommitted work only: no Commit(), simulating a failed auto-commit.
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "deliverable.txt"), "hard-won work");

        var td = life.TeardownIfIntegrated(repo, "task-dirty", "develop", wtRoot);

        Assert.True(td.Success, td.Error);
        // Branch survives and now carries the work as a WIP safety commit.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-dirty").Code);
        Assert.Equal("hard-won work", RunGit(repo, "show task/task-dirty:deliverable.txt").Out.Trim());
        // Not folded into develop -> teardown deferred, worktree kept.
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor task/task-dirty develop").Code);
    }

    [Fact]
    public void TeardownIfIntegrated_PreservesUncommittedWork_EvenWhenEarlierWorkMerged()
    {
        // A reissue can produce NEW uncommitted edits on top of already-merged
        // work. The branch tip is an ancestor of develop, so the merge check
        // alone would allow teardown - the new edits must be snapshotted first.
        var (repo, life) = SeedWithDevelop("tdi-dirty-merged");
        var wtRoot = WorktreeRoot();
        var prep = life.PrepareOrReuse(repo, "task-dm", "develop", wtRoot);
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "first.txt"), "first pass");
        Commit(prep.WorktreePath!, "feat: first pass");
        Assert.Equal(IntegrationOutcome.Merged,
            life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge).Outcome);

        // New uncommitted edits from a follow-up run that never got committed.
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "second.txt"), "second pass");

        var td = life.TeardownIfIntegrated(repo, "task-dm", "develop", wtRoot);

        Assert.True(td.Success, td.Error);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-dm").Code);
        Assert.Equal("second pass", RunGit(repo, "show task/task-dm:second.txt").Out.Trim());
    }

    // --- harness ------------------------------------------------------------

    private string WorktreeRoot()
    {
        var root = Path.Combine(_tempDir, "wts-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        return root;
    }

    private (string Repo, WorktreeTaskLifecycle Life) SeedWithDevelop(string name, bool seedShared = false)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        if (seedShared) File.WriteAllText(Path.Combine(repo, "shared.txt"), "base");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        // The integration branch is develop; main stays the released line.
        RunGit(repo, "checkout -q -b develop");

        var git = BuildGitService(repo);
        var life = new WorktreeTaskLifecycle(git, NullLogger<WorktreeTaskLifecycle>.Instance);
        return (repo, life);
    }

    private string AddBareOrigin(string repo, string name)
    {
        var bare = Path.Combine(_tempDir, name + ".git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        return bare;
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static GitService BuildGitService(string repo)
    {
        var config = BuildConfig(repo);
        var scanner = BuildScanner(config);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static TaskScannerService BuildScanner(string repo)
        => BuildScanner(BuildConfig(repo));

    private static TaskScannerService BuildScanner(IConfiguration config)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static IConfiguration BuildConfig(string repo)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
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
