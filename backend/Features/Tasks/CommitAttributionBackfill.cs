namespace AgentStudio.Tasks;

/// <summary>
/// Lazy attribution backfill for legacy folders. A job that left
/// <c>3-progress</c> before the commit-attribution step existed carries an
/// empty <c>commits[]</c> even though its runs moved HEAD; the kanban card's
/// commit total then reads 0 while the change set clearly shows work. The same
/// deterministic rule engine the transition uses runs once and persists the
/// result, so the SSOT catches up. See ADR "Commit-Attribution-Regel".
///
/// <para>
/// Git facts S3 (AGT-3323) moved this out of <c>GET /commits</c> into the Git
/// fact producer: a read no longer runs the attribution engine or writes
/// <c>task.json</c>. Idempotent: once the chain is populated the guard skips,
/// and an analysis-only job (no code activity) never touches Git.
/// </para>
/// </summary>
internal static class CommitAttributionBackfill
{
    /// <summary>
    /// Lanes where attribution is final: the job has left <c>3-progress</c>,
    /// so the transition post-step should already have stamped a chain.
    /// Includes the pre-ADR-0025 <c>4-review</c> alias for unmigrated folders.
    /// </summary>
    internal static readonly HashSet<string> FinalLanes = new(StringComparer.OrdinalIgnoreCase)
    {
        TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Escalated, "4-review",
        TaskStates.Completed, TaskStates.Archive,
    };

    internal static bool Needs(TaskInfo info)
        => FinalLanes.Contains(info.State) && info.Commits.Count == 0 && info.CodeActivityDetected;

    /// <summary>
    /// Runs the attribution engine for one card and persists a non-empty
    /// result. Best effort: returns false on any failure or an empty result.
    /// </summary>
    internal static bool Run(TaskInfo info, TaskSessionLog sessions, GitService git, TaskMutationService mutations)
    {
        try
        {
            var result = CommitAttributionRunner.Run(info, info.WatchPath, sessions, git);
            if (result is null || result.Attributed.Count == 0 && result.Excluded.Count == 0) return false;
            mutations.SetCommitAttributionOnFolder(info.FolderPath, result.Attributed);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SilentCatch.Note(ex, "CommitAttributionBackfill: backfill is best-effort");
            return false;
        }
    }
}
