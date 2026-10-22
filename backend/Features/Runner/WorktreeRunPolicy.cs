namespace AgentStudio.Runner;

/// <summary>
/// ADR-0052 "always-worktree" invariant, as pure logic: a CODING run mutates the
/// source tree and therefore must execute in its own isolated task worktree -
/// EVERY time, regardless of slot count (sequential <c>max==1</c> as well as
/// parallel <c>max&gt;1</c>) and regardless of whether it is a fresh pickup, a
/// reissue, a resume, or a crash-recovery continuation. The shared main checkout
/// is read-only reference + the integration target; an agent coding run must
/// never run with its working directory pointed at it (that is the
/// cross-contamination bug ASS-1732). Report-only modes (planning / research)
/// write nothing, so they may run in-place. Concept runs
/// need isolation because they author a bounded repository dossier.
///
/// <para>
/// This is intentionally separate from <see cref="ParallelSlotPolicy"/>: that
/// policy answers "may a second slot be admitted?"; this one answers "must this
/// run be isolated, and is the chosen working directory legal?". Keeping it pure
/// lets the decision be unit-tested without spinning a runner.
/// </para>
/// </summary>
public static class WorktreeRunPolicy
{
    /// <summary>
    /// True when this run must execute inside an isolated task worktree. A coding
    /// run always does; concept runs do so their docs-only diff is isolated.
    /// Report-only modes (planning / research) do not.
    /// A required worktree with no
    /// authoritative Git repository is a rejected admission, never an in-place
    /// fallback.
    /// </summary>
    public static bool RequiresWorktree(string? mode)
        => !TaskModes.IsReadOnly(mode) || TaskModes.IsConcept(mode);

    /// <summary>
    /// Maps the project's configured CLI cwd into a newly materialized worktree.
    /// A cwd inside the authoritative checkout keeps its relative monorepo path.
    /// A storage/local folder outside that checkout cannot be mirrored and falls
    /// back to the worktree root, so source-mutating work never runs in task
    /// storage.
    /// </summary>
    public static string ResolveWorkingDirectory(
        string repositoryRoot,
        string? configuredWorkingDirectory,
        string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredWorkingDirectory))
            return Normalize(worktreeRoot);

        var repository = Normalize(repositoryRoot);
        var configured = Normalize(configuredWorkingDirectory!);
        var worktree = Normalize(worktreeRoot);
        var relative = Path.GetRelativePath(repository, configured);
        if (relative == ".") return worktree;
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return worktree;

        return Path.GetFullPath(Path.Combine(worktree, relative));
    }

    /// <summary>
    /// True when a run that <paramref name="requiresWorktree"/> would illegally
    /// execute in the shared main checkout (at its root or in a configured
    /// subfolder). This is the guard condition: a coding run resolving anywhere
    /// inside the main checkout means worktree preparation silently fell through,
    /// and the run must be refused + escalated rather than allowed to dirty the
    /// shared tree. Read-only / planning runs (requiresWorktree == false)
    /// legitimately run in the main checkout, so they never trip the guard.
    /// </summary>
    public static bool IsMainCheckoutViolation(bool requiresWorktree, string? runWorkingDir, string? mainCheckoutRoot)
    {
        if (!requiresWorktree) return false;
        if (string.IsNullOrWhiteSpace(runWorkingDir) || string.IsNullOrWhiteSpace(mainCheckoutRoot)) return false;
        var run = Normalize(runWorkingDir!);
        var main = Normalize(mainCheckoutRoot!);
        if (string.Equals(
                run,
                main,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return true;

        var relative = Path.GetRelativePath(main, run);
        return relative != ".."
               && !Path.IsPathRooted(relative)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recorded CLI session may be resumed only when it was born in the SAME
    /// working directory this run will use (the CLI keys <c>--resume</c> by cwd; a
    /// session born elsewhere hangs). Non-worktree runs always run in the same
    /// place, so their sessions resume. Worktree runs resume only when the
    /// worktree was RE-USED (<paramref name="worktreeReused"/>) - a freshly cut
    /// worktree means any prior session lived in a different directory (the old
    /// main checkout) and must start fresh.
    /// </summary>
    public static bool CanResumeSession(bool isWorktreeRun, bool worktreeReused)
        => CanResumeSession(isWorktreeRun, worktreeReused, null, null);

    /// <summary>
    /// S2 (AGT-1784): the guard that actually enforces the cwd-binding the summary
    /// above describes. Beyond the worktree-reuse check, when the session's BIRTH
    /// working directory (<paramref name="sessionBirthCwd"/>) is known, the resume
    /// is allowed only when this run's <paramref name="runWorkingDir"/> equals it.
    /// <para>
    /// Why: <c>worktreeReused</c> only proves the <c>task/&lt;id&gt;</c> branch still
    /// exists — it is keyed on repo+branch, NOT on the absolute worktree path. The
    /// path embeds the project DISPLAY name, so a display-name change relocates the
    /// dir while keeping the branch → <c>worktreeReused==true</c> at a NEW path.
    /// Passing <c>--resume &lt;id&gt;</c> in that different cwd makes the CLI find no
    /// conversation, mint a fresh session, arm a liveness watcher on a dead id, and
    /// exit abnormally (observed: claude exited -1 after 164s → infra-crash). The
    /// cwd-check turns that crash into a clean fresh start.
    /// </para>
    /// <para>
    /// Back-compat: when the birth cwd is unknown (legacy rows recorded before Cwd
    /// was tracked, or any null), fall back to the reuse behavior so normal
    /// same-path reissues never regress. This overload only ever turns a resume
    /// INTO a fresh start, never the reverse.
    /// </para>
    /// </summary>
    public static bool CanResumeSession(bool isWorktreeRun, bool worktreeReused, string? runWorkingDir, string? sessionBirthCwd)
    {
        // Non-worktree runs always execute in the same place → resume as before.
        if (!isWorktreeRun) return true;
        // A freshly-cut worktree means any prior session lived elsewhere → fresh.
        if (!worktreeReused) return false;
        // Reused worktree: the branch matched, but the absolute path can still have
        // moved. Enforce the cwd-binding when we know where the session was born.
        if (string.IsNullOrWhiteSpace(sessionBirthCwd) || string.IsNullOrWhiteSpace(runWorkingDir))
            return true; // unknown birth cwd → keep reuse behavior, don't regress
        return PathsEqual(runWorkingDir!, sessionBirthCwd!);
    }

    /// <summary>Path equality after full-path normalization, case-insensitive on Windows.</summary>
    public static bool PathsEqual(string a, string b)
        => string.Equals(
            Normalize(a),
            Normalize(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
