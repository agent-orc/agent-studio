

namespace AgentStudio.Tasks;

/// <summary>
/// Shared builder + in-progress projection for a job's commit aggregate.
///
/// <para>
/// <see cref="Build"/> is the single binding of <see cref="TaskCommitsAggregator"/>
/// against the production <see cref="GitService"/> + session timeline. It was
/// extracted from <c>TaskGitEndpoints</c> so that the <c>/commits</c> family
/// and the background task-detail Git snapshot agree on which commits belong to a job - no
/// second, drifting implementation.
/// </para>
///
/// <para>
/// <b>ASS-1712.</b> An in-progress per-task-worktree task's persisted
/// <see cref="TaskInfo.Commits"/> chain collapses to empty/singular: its per-run
/// SHA ranges track the shared develop HEAD (before==after), and the attribution
/// post-step only stamps the chain once the task LEAVES <c>3-progress</c>. The
/// backend reconstruction (<see cref="GitService.GetTaskRunCommits"/>, folded in
/// by <see cref="TaskCommitsAggregator.Aggregate"/>) recovers the real task-branch
/// history, but it previously reached only the bare <c>/commits</c> endpoint - a
/// surface the frontend never calls. <see cref="WithReconstructedInProgressCommits(TaskDetail, TaskSessionLog, string?, GitService)"/>
/// folds that reconstruction into the background task-detail snapshot's
/// <see cref="TaskInfo.Commits"/>, which the git-pane chain strip and the
/// header commit-count badge DO read, so Task-Detail shows the full history
/// instead of one commit. Scope is deliberately narrow (detail snapshot only);
/// the board card still reads the list projection (see the gap analysis,
/// <c>results/ASS-1712-ui-wiring-gap.md</c>, Option A).
/// </para>
/// </summary>
public static class JobCommitsAggregation
{
    /// <summary>
    /// Builds the job-level commit aggregate from all sources: per-run SHA
    /// ranges, the reconstructed task-branch run commits (durable trailer), and
    /// the persisted attribution chain + auto-commit. Ordered newest-first.
    /// </summary>
    public static TaskCommitsAggregate Build(
        TaskInfo info, TaskSessionLog sessions, string jobId, string? watchPath, GitService git)
    {
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
        var taskRunCommits = git.GetTaskRunCommits(jobId, watchPath);
        return TaskCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after),
            taskRunCommits);
    }

    /// <summary>
    /// Pure projection: fold a freshly built <paramref name="aggregate"/> onto an
    /// in-progress task's <see cref="TaskInfo.Commits"/> chain. Returns
    /// <paramref name="info"/> UNCHANGED (same reference) unless ALL hold:
    /// the task is in <c>3-progress</c>, its persisted chain is collapsed
    /// (≤1 entry), and the aggregate surfaced strictly more commits. The mapped
    /// chain is re-ordered oldest→newest to match the
    /// <see cref="TaskInfo.Commits"/> convention (the aggregate is newest-first).
    /// No I/O - unit-testable without a git repo.
    /// </summary>
    public static TaskInfo WithReconstructedInProgressCommits(TaskInfo info, TaskCommitsAggregate aggregate)
    {
        if (!string.Equals(info.State, TaskStates.Progress, StringComparison.OrdinalIgnoreCase)) return info;
        if (info.Commits.Count > 1) return info;
        if (aggregate.Commits.Count <= info.Commits.Count) return info;

        var mapped = aggregate.Commits
            .Select(c => new TaskCommitInfo
            {
                Sha = c.Sha,
                ShortSha = c.ShortSha,
                Message = c.Subject,
                FilesChanged = c.FilesChanged,
                Files = [],
                At = c.AuthorDateUtc,
                Attribution = c.Attribution,
                Confidence = c.Confidence,
            })
            .ToList();
        mapped.Reverse(); // aggregate is newest-first; TaskInfo.Commits convention is oldest→newest

        return info with { Commits = mapped };
    }

    /// <summary>
    /// I/O wrapper for the background task-detail index: when the task is an
    /// in-progress task with a collapsed chain, builds the aggregate and folds
    /// the reconstructed history into <see cref="TaskDetail.Info"/>. Best-effort -
    /// any failure returns <paramref name="detail"/> unchanged so a detail read
    /// never fails on a reconstruction hiccup. The cheap state/chain guard runs
    /// before any git call so non-applicable detail opens pay nothing.
    /// </summary>
    public static TaskDetail WithReconstructedInProgressCommits(
        TaskDetail detail, TaskSessionLog sessions, string? watchPath, GitService git)
    {
        var info = detail.Info;
        if (!string.Equals(info.State, TaskStates.Progress, StringComparison.OrdinalIgnoreCase)) return detail;
        if (info.Commits.Count > 1) return detail;
        // A first-run task cannot have an integrated worktree commit to
        // reconstruct. Avoid a synchronous `git log --all` on the detail-read
        // path until provenance proves that an earlier run reached post
        // processing. Large repositories otherwise make a fresh task appear
        // to fail loading for several seconds (AGT-2157).
        if (!HasPriorPostProcessingTransition(info)) return detail;

        try
        {
            var aggregate = Build(info, sessions, info.Id, watchPath, git);
            var enriched = WithReconstructedInProgressCommits(info, aggregate);
            return ReferenceEquals(enriched, info) ? detail : detail with { Info = enriched };
        }
        catch
        {
            return detail;
        }
    }

    internal static bool HasPriorPostProcessingTransition(TaskInfo info)
        => info.Provenance?.Transitions.Any(transition =>
            string.Equals(transition.Lane, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase)) == true;
}
