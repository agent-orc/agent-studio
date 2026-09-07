

namespace AgentStudio.Tasks;

/// <summary>
/// Builds the <see cref="AttributionInput"/> for a task from git + session
/// telemetry and runs the pure <see cref="CommitAttributionService"/> rule
/// engine. This is the single place that turns a task's run SHA-windows into
/// an attributed chain, shared by both attribution entry points so they can
/// never diverge (ADR "Commit-Attribution-Regel"):
/// <list type="bullet">
/// <item>the <c>3-progress -&gt; 4-auto-review</c> transition post-step
///   (<see cref="TaskTransitionService"/>), and</item>
/// <item>the lazy backfill on the <c>/api/tasks/{id}/commits</c> read path,
///   which repopulates legacy folders whose <c>commits[]</c> predates the
///   attribution step.</item>
/// </list>
/// Pure orchestration: it reads (git, session log) but never writes. Callers
/// persist the returned <see cref="AttributionResult"/> through
/// <see cref="TaskMutationService"/>, keeping the API-only job-folder rule.
/// </summary>
public static class CommitAttributionRunner
{
    /// <summary>
    /// Computes the attribution for <paramref name="info"/>. Returns null when
    /// the task has no candidate commits at all (an analysis-only task, or one
    /// whose runs never moved HEAD) - in that case there is nothing to persist
    /// and the caller should leave the folder untouched.
    /// </summary>
    public static AttributionResult? Run(
        TaskInfo info, string? watchPath,
        TaskSessionLog sessions, GitService git,
        string? repository = null)
    {
        var events = sessions.ReadSessionEvents(info.Id, watchPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);

        var aggregate = TaskCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(info.Id, watchPath, before, after));

        if (aggregate.Commits.Count == 0) return null;

        // Enrich with the full commit body (for Co-Authored-By detection) and
        // the merge flag (parent count) in one git call, so the rule engine
        // works off the real message body rather than the subject line.
        var meta = git.GetCommitMeta(info.Id, watchPath, aggregate.Commits.Select(c => c.Sha));

        var candidates = aggregate.Commits.Select(c =>
        {
            meta.TryGetValue(c.Sha, out var m);
            return new AttributionCandidate
            {
                Sha = c.Sha,
                ShortSha = c.ShortSha,
                Author = c.Author,
                Subject = c.Subject,
                Message = string.IsNullOrEmpty(m?.Body) ? c.Subject : m!.Body,
                AuthorDateUtc = c.AuthorDateUtc,
                FilesChanged = c.FilesChanged,
                IsMerge = m?.IsMerge ?? false,
            };
        }).ToList();

        var input = new AttributionInput
        {
            TaskId = info.Id,
            Repository = repository ?? ResolveRepository(info, watchPath, git),
            TaskBranch = ResolveAttributionBranch(info, watchPath, git),
            Candidates = candidates,
            // The platform-stamped auto-commit(s) are the task's accepted work
            // by construction - pin them to full confidence.
            PlatformStampShas = info.Commits
                .Where(c => !string.IsNullOrWhiteSpace(c.Sha))
                .Select(c => c.Sha)
                .ToList(),
        };

        return CommitAttributionService.Attribute(input);
    }

    private static string? ResolveAttributionBranch(TaskInfo info, string? watchPath, GitService git)
    {
        var repository = git.ResolveRepoRootForWatchPath(watchPath ?? info.WatchPath);
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var current = git.ReadCurrentBranchAt(repository);
            if (!string.IsNullOrWhiteSpace(current)) return current;
        }

        return info.Commits.LastOrDefault(commit => !string.IsNullOrWhiteSpace(commit.Branch))?.Branch
            ?? info.Commit?.Branch;
    }

    private static string? ResolveRepository(TaskInfo info, string? watchPath, GitService git)
    {
        var root = git.ResolveRepoRootForWatchPath(watchPath ?? info.WatchPath);
        return string.IsNullOrWhiteSpace(root) ? null : git.ReadOriginUrlAt(root);
    }
}
