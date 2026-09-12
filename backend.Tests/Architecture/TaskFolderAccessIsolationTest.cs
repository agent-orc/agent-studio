using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Mechanically enforces the rule from ADR-0024 and the queued task
/// <c>task-access-api-layer-extraction</c>: only the small set of
/// services that own on-disk job state may construct lane folder paths
/// or perform structural directory mutations against the job-folder
/// tree (<c>&lt;watchPath&gt;/&lt;lane&gt;/&lt;slug&gt;/</c>).
///
/// <para>
/// Motivation: in 2026-05 a class of bugs (zombie folders, 409 on move,
/// state-field-out-of-lane) was traced back to direct filesystem
/// manipulation of job folders that bypassed <c>TaskMutationService</c>
/// and <c>TaskStateMachine</c>. Once <c>ITaskAccess</c> implements
/// phases 2-4, the whitelist below shrinks to <c>Services/TaskAccess/</c>
/// plus <c>Services/Jobs/</c>; everything else is forced through the
/// typed API. Today the whitelist is the migration ledger.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The test scans source files, not compiled IL, on purpose: a textual
/// rule is the cheapest review surface and the diff-time grep stays
/// useful even when the offending code is in a generated partial.
/// </para>
/// <para>
/// Two layers of rule:
/// <list type="number">
/// <item><description>
/// <b>Lane-path construction.</b> <c>Path.Combine(.., TaskStates.X, ..)</c>
/// or a hardcoded lane folder literal (e.g. <c>"3-progress"</c>) inside
/// a <c>Path.Combine</c> call means the caller is building a path into
/// the lane-folder structure. That is a job-folder-tree operation by
/// construction and must go through <c>ITaskAccess</c> (or the
/// grandfathered storage services).
/// </description></item>
/// <item><description>
/// <b>Structural directory mutations.</b> <c>Directory.Move</c> and
/// <c>Directory.Delete</c> are the calls that produce zombie folders
/// and 409 conflicts when used against the job-folder tree from
/// anywhere except <c>TaskStateMachine</c>. Forbid them outright outside
/// the storage services; non-job-folder uses (writing to
/// <c>workspace/logs/</c>, dropping temp scratch) do not need
/// <c>Directory.Move</c> / <c>Directory.Delete</c> in practice.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public class TaskFolderAccessIsolationTest
{
    /// <summary>
    /// Files that may construct lane folder paths or perform structural
    /// directory mutations against the job-folder tree. Anything not on
    /// this list goes through <c>ITaskAccess</c> (today: through
    /// <c>TaskMutationService</c> / <c>TaskStateMachine</c>; after phase 4
    /// of <c>task-access-api-layer-extraction</c>, through the typed
    /// API in <c>Services/TaskAccess/</c>).
    ///
    /// <para>
    /// Each entry carries the reason it is on the list. Entries marked
    /// "MIGRATION TARGET" are tolerated only until the corresponding
    /// consumer is rewritten against <c>ITaskAccess</c>; removing the
    /// entry is the green-light gate for the migration.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Whitelist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Tier 1: the single owner of every read / write against the
            // job-folder tree. The phase 2-4 implementation lives in
            // TaskAccessService.cs; the contract surface stays in the
            // four sibling files below.
            ["backend/Features/TaskAccess/ITaskAccess.cs"] =
                "TaskAccess contract surface.",
            ["backend/Features/TaskAccess/ITaskAccessHost.cs"] =
                "TaskAccess lifecycle surface.",
            ["backend/Features/TaskAccess/TaskAccessRecords.cs"] =
                "TaskAccess typed request/result records.",
            ["backend/Features/TaskAccess/TaskAccessService.cs"] =
                "TaskAccess implementation: the one place that constructs lane folder paths.",

            // Tier 2: current storage authority. TaskStateMachine is the
            // single owner of folder moves and deletes; TaskMutationService
            // owns folder creates and field edits; TaskScannerService owns
            // reads; TaskTransitionService combines a move with its side
            // effects (auto-commit, runner-active-state clear).
            // (The Services/Jobs -> Services/Tasks rename moved these four
            // files; the whitelist tracks their current home.)
            ["backend/Features/Tasks/TaskStateMachine.cs"] =
                "Single-state-machine authority for folder moves and deletes.",
            ["backend/Features/Tasks/TaskMutationService.cs"] =
                "Owns folder creates and per-job field edits.",
            ["backend/Features/Tasks/TaskScannerService.cs"] =
                "Owns reads against the job-folder tree.",
            ["backend/Features/Tasks/TaskTransitionService.cs"] =
                "Combines folder moves with auto-commit and runner-active-state side effects.",
            ["backend/Features/Tasks/Merge/MergeService.cs"] =
                "Merge archives the secondary task folder and prunes its mirror; structural moves owned here.",
            ["backend/Features/Tasks/TaskWatcherService.cs"] =
                "FileSystemWatcher feeding the scanner; needs raw lane paths.",

            // Project-level (not lane-level) deletion: removes a whole
            // <TaskRepository>/projects/<key> folder when a registry project
            // is deleted via DELETE /api/projects/{PROJ-NNN}. Operates on the
            // project root, not individual lane folders.
            ["backend/Features/Configuration/WorkspaceManagementService.cs"] =
                "Owns project-storage folder lifecycle (create on workspace add, recursive delete on project delete).",

            // Restore verification extracts into the server-owned backup
            // directory, which is required to live outside TaskRepository,
            // then removes only that isolated staging directory.
            ["backend/Features/Management/ManagementService.cs"] =
                "Deletes isolated restore-verification staging outside the task store, never task folders.",

            // Tier 2 (boot): the crash-recovery sweep walks 3-progress
            // before the in-memory store boots. Explicitly carved out in
            // the prompt for this task.
            ["backend/Features/Runner/CrashRecoveryService.cs"] =
                "Boot-time recovery; runs before ITaskAccess could be available.",
            ["backend/Features/Runner/WorktreeTaskLifecycle.cs"] =
                "Git worktree cleanup under configured worktree roots, not task storage.",
            ["backend/Features/Pipeline/GateDependencyCache.cs"] =
                "Moves and replaces dependency caches under the OS review-workspace temp root, never task storage.",

            // Re-added after the structure migration folded the src/ executor
            // projects back into backend/ (the scan covers them again):
            ["backend/Features/Cli/Execution/NpmShimHealer.cs"] =
                "Temporary legacy/OneShot npm staging cleanup under the user npm directory, not the job-folder tree; removed in AGT-2373.",
            ["backend/Features/Cli/Execution/RunLogStore.cs"] =
                "Owns the per-run log directory lifecycle (run-context dirs), not lane folders.",
            ["backend/Features/Cli/Execution/BackendCarExecution.cs"] =
                "Deletes CAR's temporary mirror under TaskRepository/.runtime/car-cli-output, never a lane or task folder.",
            ["backend/Features/Cli/Execution/CleanContextPreparation.cs"] =
                "Adapts the external task clean-home store; failed-start cleanup never targets the job-folder tree.",
            ["backend/Features/Cli/Execution/CliWorkingMemoryService.cs"] =
                "Deletes a CLI's own working-memory state under its config home (guarded by a known-state whitelist), not lane folders.",
            ["backend/Features/Cli/Execution/DurableLocalCliProcess.cs"] =
                "AGT-2780 worker spool directory is outside the task folder tree and owned by the durable worker lifecycle.",
            ["backend/Features/Cli/Routing/OneShot/CodexOneShot.cs"] =
                "Deletes only the per-call OS temp directory created for bounded multimodal image arguments, never task storage.",

            // Wiki git-ops (2026-06-10): physical folder move/delete inside the
            // project's CODE repo docs/ tree (git-backed wiki reorganisation),
            // never the task-storage tree.
            ["backend/Features/Git/GitService.cs"] =
                "Wiki folder move/delete operate on the project repo docs/ tree, not task storage.",

            // Tier 3 migration complete. All former MIGRATION TARGET
            // entries (ProjectRunner, StaleProgressArchiver,
            // ReviewDecisionOrchestrator, MetaCycleHostedService,
            // ProjectSnapshotEndpoints, ReviewDecisionsEndpoints) now
            // route through ITaskAccess. Phase 4 of ADR-0024 is closed.
        };

    /// <summary>
    /// Detects lane-folder path construction. A match means the source
    /// line is building a path into the lane-folder structure (the
    /// signal that motivates this whole rule).
    /// </summary>
    internal static readonly Regex LanePathConstruction = new(
        @"Path\.Combine\([^;]*?(?:" +
        @"TaskStates\.(?:Backlog|Preparation|OrchestratorPrep|Ready|Progress|FailedPickup|AutoReview|HumanReview|Escalated|Completed|Archive)\b" +
        @"|""(?:0-backlog|1-preparation|1a-orchestrator-prep|2-ready|3-progress|3a-failed-pickup|4-auto-review|5-human-review|5e-escalated|6-completed|7-archive)""" +
        @")",
        RegexOptions.Compiled);

    /// <summary>
    /// Detects structural directory mutations: <c>Directory.Move</c> and
    /// <c>Directory.Delete</c>. These are the two calls that produce
    /// zombie folders and 409s when used against the job-folder tree
    /// from outside <c>TaskStateMachine</c>; everywhere else can manage
    /// with <c>Directory.CreateDirectory</c>, file APIs, or
    /// <c>ITaskAccess</c>.
    /// </summary>
    internal static readonly Regex StructuralDirectoryMutation = new(
        @"\bDirectory\.(?:Move|Delete)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoLaneFolderPathConstruction_OutsideWhitelist()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot, LanePathConstruction);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(
                "lane-folder path construction",
                "Use ITaskAccess (or, until phase 4 ships, TaskMutationService / TaskStateMachine) to address jobs by id and lane name without building raw filesystem paths.",
                violations));
    }

    [Fact]
    public void NoStructuralDirectoryMutation_OutsideWhitelist()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot, StructuralDirectoryMutation);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(
                "Directory.Move / Directory.Delete",
                "Folder structural changes against the job-folder tree must flow through TaskStateMachine (and, after phase 4, ITaskAccess.TransitionLaneAsync). Non-job-folder uses of Directory.Move/Delete are rare; whitelist the file with a one-line reason if there is a legitimate case.",
                violations));
    }

    [Fact]
    public void WhitelistEntries_StillExist()
    {
        var repoRoot = ResolveRepoRoot();
        var missing = new List<string>();
        foreach (var entry in Whitelist.Keys)
        {
            var absolute = Path.Combine(repoRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                missing.Add(entry);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Whitelist references files that no longer exist - prune the list:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void LanePathConstructionRegex_MatchesKnownViolations_AndIgnoresStateComparisons()
    {
        Assert.Matches(LanePathConstruction, "var x = Path.Combine(watchPath, TaskStates.Progress);");
        Assert.Matches(LanePathConstruction, "var y = Path.Combine(root, TaskStates.HumanReview, slug);");
        Assert.Matches(LanePathConstruction, "var z = Path.Combine(workspace, \"projects\", project, \"3-progress\", slug);");

        Assert.DoesNotMatch(LanePathConstruction, "if (info.State == TaskStates.Progress) {}");
        Assert.DoesNotMatch(LanePathConstruction, "return TaskStates.Ready;");
        Assert.DoesNotMatch(LanePathConstruction, "var lane = TaskStates.Progress;");
    }

    [Fact]
    public void StructuralDirectoryMutationRegex_MatchesMoveAndDelete_AndIgnoresOtherDirectoryCalls()
    {
        Assert.Matches(StructuralDirectoryMutation, "Directory.Move(sourceFolder, targetDir);");
        Assert.Matches(StructuralDirectoryMutation, "Directory.Delete(info.FolderPath, true);");

        Assert.DoesNotMatch(StructuralDirectoryMutation, "Directory.CreateDirectory(dir);");
        Assert.DoesNotMatch(StructuralDirectoryMutation, "Directory.EnumerateDirectories(progressDir)");
        Assert.DoesNotMatch(StructuralDirectoryMutation, "if (Directory.Exists(targetDir))");
    }

    private static List<Violation> ScanForViolations(string repoRoot, Regex pattern)
    {
        var backendDir = Path.Combine(repoRoot, "backend");
        var violations = new List<Violation>();

        foreach (var path in Directory.EnumerateFiles(backendDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(path)) continue;

            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (Whitelist.ContainsKey(relative)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsLineCommentedOut(line)) continue;
                if (!pattern.IsMatch(line)) continue;

                violations.Add(new Violation(relative, i + 1, line.Trim()));
            }
        }

        return violations;
    }

    private static bool IsExcludedPath(string path)
    {
        // Skip generated, build, and IDE folders.
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/Properties/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLineCommentedOut(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static string BuildFailureMessage(string ruleName, string remediation, List<Violation> violations)
    {
        var rendered = string.Join("\n  ", violations.Select(v => $"{v.RelativePath}:{v.Line}: {v.Source}"));
        return
            $"Found {violations.Count} forbidden {ruleName} call(s) outside the TaskFolderAccessIsolation whitelist.\n" +
            $"Remediation: {remediation}\n" +
            $"Offending lines:\n  {rendered}";
    }

    private static string ResolveRepoRoot()
    {
        // Walk up from the test binary location to the repo root.
        // The test runner's working directory is the test project's
        // bin/Debug/net10.0/, so the repo root is a few levels above.
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var marker = Path.Combine(current, "backend", "OrchestratorApi.csproj");
            if (File.Exists(marker)) return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root by walking up from {AppContext.BaseDirectory}.");
    }

    private readonly record struct Violation(string RelativePath, int Line, string Source);
}
