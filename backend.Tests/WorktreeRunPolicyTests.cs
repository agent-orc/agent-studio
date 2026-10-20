using System.IO;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ASS-1732 "always-worktree" invariant as pure logic (<see cref="WorktreeRunPolicy"/>):
/// every coding run is isolated in a task worktree - fresh pickup, reissue, resume,
/// or crash-recovery alike - while read-only (planning / research)
/// runs may run in the shared main checkout. The guard refuses a coding run that
/// resolved to the main checkout, and the resume gate forbids reusing a CLI session
/// born in a different working directory.
/// </summary>
public sealed class WorktreeRunPolicyTests
{
    [Fact]
    public void ResolveRunnerEntry_RepositoryOnlyProject_UsesRepositoryAsWorkingDirectory()
    {
        var storage = Path.Combine(Path.GetTempPath(), "task-storage", "PROJ-020");
        var repository = Path.Combine(Path.GetTempPath(), "repos", "docs");
        var raw = new WatchPathEntry { Name = "Docs", Path = storage };
        var record = new ProjectRecord
        {
            Id = "PROJ-020",
            DisplayName = "Docs",
            StorageLocation = storage,
            RepositoryPath = repository,
        };

        var resolved = TaskRunnerService.ResolveRunnerEntry(raw, record);

        Assert.Equal(repository, resolved.RepositoryPath);
        Assert.Equal(repository, resolved.RootPath);
    }

    [Fact]
    public void ResolveRunnerEntry_ExternalConfiguredCwd_PreservesRepositoryAuthority()
    {
        var storage = Path.Combine(Path.GetTempPath(), "task-storage", "PROJ-021");
        var repository = Path.Combine(Path.GetTempPath(), "repos", "patterns");
        var raw = new WatchPathEntry { Name = "Patterns", Path = storage };
        var record = new ProjectRecord
        {
            Id = "PROJ-021",
            DisplayName = "Patterns",
            StorageLocation = storage,
            RepositoryPath = repository,
            RootPath = storage,
        };

        var resolved = TaskRunnerService.ResolveRunnerEntry(raw, record);

        Assert.Equal(repository, resolved.RepositoryPath);
        Assert.Equal(storage, resolved.RootPath);
    }

    // --- RequiresWorktree ---------------------------------------------------

    [Theory]
    [InlineData(TaskModes.Coding)]
    [InlineData(null)]          // unknown/empty -> coerced to coding
    [InlineData("something-else")]
    public void RequiresWorktree_CodingModes_True(string? mode)
        => Assert.True(WorktreeRunPolicy.RequiresWorktree(mode));

    [Theory]
    [InlineData(TaskModes.Planning)]
    [InlineData(TaskModes.Research)]
    public void RequiresWorktree_ReadOnlyModes_False(string mode)
        => Assert.False(WorktreeRunPolicy.RequiresWorktree(mode));

    [Fact]
    public void RequiresWorktree_ConceptMode_True()
        => Assert.True(WorktreeRunPolicy.RequiresWorktree(TaskModes.Concept));


    // --- IsMainCheckoutViolation -------------------------------------------

    [Fact]
    public void IsMainCheckoutViolation_CodingRunAtMainCheckout_True()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.True(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true, runWorkingDir: main, mainCheckoutRoot: main));
    }

    [Fact]
    public void IsMainCheckoutViolation_CodingRunInWorktree_False()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        var worktree = Path.Combine(Path.GetTempPath(), "wts", "task-1");
        Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true, runWorkingDir: worktree, mainCheckoutRoot: main));
    }

    [Fact]
    public void IsMainCheckoutViolation_CodingRunInMainCheckoutSubfolder_True()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        var docs = Path.Combine(main, "docs");
        Assert.True(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true, runWorkingDir: docs, mainCheckoutRoot: main));
    }

    [Fact]
    public void IsMainCheckoutViolation_ReadOnlyRunAtMainCheckout_False()
    {
        // Read-only runs (requiresWorktree == false) legitimately run in-place.
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: false, runWorkingDir: main, mainCheckoutRoot: main));
    }

    [Theory]
    [InlineData(null, "C:/repo")]
    [InlineData("C:/repo", null)]
    [InlineData("   ", "C:/repo")]
    [InlineData("C:/repo", "")]
    public void IsMainCheckoutViolation_MissingPaths_False(string? runDir, string? mainRoot)
        => Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(true, runDir, mainRoot));

    [Fact]
    public void IsMainCheckoutViolation_TrailingSeparatorDifference_StillTrips()
    {
        // The same directory written with vs. without a trailing separator must
        // still count as the main checkout (defense in depth against path drift).
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.True(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true,
            runWorkingDir: main + Path.DirectorySeparatorChar,
            mainCheckoutRoot: main));
    }

    // --- ResolveWorkingDirectory -------------------------------------------

    [Fact]
    public void ResolveWorkingDirectory_MonorepoSubfolder_MapsIntoWorktree()
    {
        var repository = Path.Combine(Path.GetTempPath(), "repo-main");
        var configured = Path.Combine(repository, "docs", "product");
        var worktree = Path.Combine(Path.GetTempPath(), "worktrees", "task-1");

        var resolved = WorktreeRunPolicy.ResolveWorkingDirectory(
            repository, configured, worktree);

        Assert.Equal(Path.Combine(worktree, "docs", "product"), resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_TaskStorageOutsideRepository_UsesWorktreeRoot()
    {
        var repository = Path.Combine(Path.GetTempPath(), "repo-main");
        var taskStorage = Path.Combine(Path.GetTempPath(), "task-storage", "PROJ-015");
        var worktree = Path.Combine(Path.GetTempPath(), "worktrees", "TE-20");

        var resolved = WorktreeRunPolicy.ResolveWorkingDirectory(
            repository, taskStorage, worktree);

        Assert.Equal(Path.GetFullPath(worktree), resolved);
        Assert.False(WorktreeRunPolicy.PathsEqual(resolved, taskStorage));
    }

    // --- CanResumeSession ---------------------------------------------------

    [Fact]
    public void CanResumeSession_NonWorktreeRun_True()
        => Assert.True(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: false, worktreeReused: false));

    [Fact]
    public void CanResumeSession_WorktreeRun_Reused_True()
        => Assert.True(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: true, worktreeReused: true));

    [Fact]
    public void CanResumeSession_WorktreeRun_FreshCut_False()
    {
        // A freshly-cut worktree means any prior session lived elsewhere (the old
        // main checkout) and would hang on --resume; force a fresh session.
        Assert.False(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: true, worktreeReused: false));
    }

    // --- CanResumeSession cwd-binding (S2 / AGT-1784) -----------------------

    [Fact]
    public void CanResumeSession_ReusedWorktree_CrossPathBirthCwd_False()
    {
        // The exact AGT-1776 bug: the task/<slug> branch still existed so
        // worktreeReused==true, but the absolute worktree path moved (the project
        // display name changed from Agent-Task-Processor to Agent-Software-Studio).
        // Resuming a session born in the old path crashes the CLI -> must be FALSE.
        Assert.False(WorktreeRunPolicy.CanResumeSession(
            isWorktreeRun: true, worktreeReused: true,
            runWorkingDir: Path.Combine(Path.GetTempPath(), "wts", "Agent-Software-Studio", "proj"),
            sessionBirthCwd: Path.Combine(Path.GetTempPath(), "wts", "Agent-Task-Processor", "proj")));
    }

    [Fact]
    public void CanResumeSession_ReusedWorktree_SameBirthCwd_True()
    {
        var p = Path.Combine(Path.GetTempPath(), "wts", "Studio", "proj");
        Assert.True(WorktreeRunPolicy.CanResumeSession(
            isWorktreeRun: true, worktreeReused: true, runWorkingDir: p, sessionBirthCwd: p));
    }

    [Fact]
    public void CanResumeSession_ReusedWorktree_BirthCwdTrailingSeparator_StillTrue()
    {
        var p = Path.Combine(Path.GetTempPath(), "wts", "Studio", "proj");
        Assert.True(WorktreeRunPolicy.CanResumeSession(
            isWorktreeRun: true, worktreeReused: true,
            runWorkingDir: p, sessionBirthCwd: p + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CanResumeSession_ReusedWorktree_UnknownBirthCwd_FallsBackToReuse_True()
    {
        // Legacy rows recorded before Cwd was tracked: birth cwd null -> keep the
        // old reuse behavior so normal same-path reissues never regress.
        Assert.True(WorktreeRunPolicy.CanResumeSession(
            isWorktreeRun: true, worktreeReused: true,
            runWorkingDir: Path.Combine(Path.GetTempPath(), "wts", "Studio", "proj"),
            sessionBirthCwd: null));
    }

    [Fact]
    public void CanResumeSession_FreshCut_CrossPathIrrelevant_False()
    {
        // Fresh-cut still wins regardless of any recorded birth cwd.
        Assert.False(WorktreeRunPolicy.CanResumeSession(
            isWorktreeRun: true, worktreeReused: false,
            runWorkingDir: "/a", sessionBirthCwd: "/a"));
    }

    [Fact]
    public void CanResumeSession_TwoArgOverload_DelegatesUnchanged()
    {
        // The 2-arg overload must behave exactly as before (nulls -> reuse path).
        Assert.True(WorktreeRunPolicy.CanResumeSession(true, true));
        Assert.False(WorktreeRunPolicy.CanResumeSession(true, false));
        Assert.True(WorktreeRunPolicy.CanResumeSession(false, false));
    }

    // --- PathsEqual ---------------------------------------------------------

    [Fact]
    public void PathsEqual_SamePath_True()
    {
        var p = Path.Combine(Path.GetTempPath(), "x", "y");
        Assert.True(WorktreeRunPolicy.PathsEqual(p, p));
    }

    [Fact]
    public void PathsEqual_TrailingSeparator_NormalizedEqual()
    {
        var p = Path.Combine(Path.GetTempPath(), "x", "y");
        Assert.True(WorktreeRunPolicy.PathsEqual(p, p + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PathsEqual_DifferentPaths_False()
    {
        var a = Path.Combine(Path.GetTempPath(), "x", "y");
        var b = Path.Combine(Path.GetTempPath(), "x", "z");
        Assert.False(WorktreeRunPolicy.PathsEqual(a, b));
    }

    [Fact]
    public void PathsEqual_CaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = Path.Combine(Path.GetTempPath(), "Repo", "Main");
        Assert.True(WorktreeRunPolicy.PathsEqual(p.ToUpperInvariant(), p.ToLowerInvariant()));
    }
}
