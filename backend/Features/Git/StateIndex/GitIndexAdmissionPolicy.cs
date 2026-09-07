namespace AgentStudio.Git;

/// <summary>
/// What the indexer does with one repository when a scheduling tick observes it.
/// </summary>
internal enum GitIndexAdmission
{
    /// <summary>Nothing is due; leave the repository alone.</summary>
    Idle,

    /// <summary>Start exactly one run for this repository now.</summary>
    Start,

    /// <summary>
    /// A run already owns this repository. The trigger folds into that run
    /// (single-flight); the run marks itself for a follow-up instead of
    /// starting a duplicate process fan-out.
    /// </summary>
    Coalesce,

    /// <summary>
    /// A change is pending but its debounce window has not elapsed. Git writes
    /// refs in bursts (a fetch touches dozens of remote refs); starting on the
    /// first event would spawn one run per ref.
    /// </summary>
    Wait,
}

/// <summary>
/// The whole scheduling decision for the background git index, as a pure
/// function of four observations. Keeping it separate from the timers, the
/// watcher, and the process runner is what makes "single-flight coalescing"
/// and "a ref change wins over the sweep" testable as a direct matrix rather
/// than as a timing-dependent integration test.
/// </summary>
internal static class GitIndexAdmissionPolicy
{
    /// <param name="running">A run for this repository has not finished yet.</param>
    /// <param name="dirty">A change trigger arrived since the last run started.</param>
    /// <param name="debounceElapsed">
    /// The quiet period after the most recent trigger has passed.
    /// </param>
    /// <param name="sweepDue">
    /// The slow periodic safety net is due. It exists for the changes no
    /// watcher reports (a network share, a repository added while the backend
    /// was down), so it must never pre-empt or duplicate a change-driven run.
    /// </param>
    internal static GitIndexAdmission Admit(
        bool running,
        bool dirty,
        bool debounceElapsed,
        bool sweepDue)
    {
        // Single-flight: a run in progress is never duplicated. Concurrent
        // triggers only record that the current run started too early.
        if (running) return dirty || sweepDue ? GitIndexAdmission.Coalesce : GitIndexAdmission.Idle;

        // A real change beats the sweep, but only after the burst has settled.
        if (dirty) return debounceElapsed ? GitIndexAdmission.Start : GitIndexAdmission.Wait;

        return sweepDue ? GitIndexAdmission.Start : GitIndexAdmission.Idle;
    }
}
