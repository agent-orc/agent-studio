namespace AgentStudio.Git;

/// <summary>
/// Why an index run was scheduled. The value is logged verbatim as the
/// <c>trigger=</c> field of <c>git-index-run</c>, so it stays a short,
/// low-cardinality slug.
/// </summary>
public static class GitIndexTriggers
{
    public const string Startup = "startup";
    public const string HeadChange = "head-change";
    public const string RefChange = "ref-change";
    public const string PackedRefs = "packed-refs";
    public const string Worktree = "worktree";
    public const string TaskEvent = "task-event";
    public const string Sweep = "sweep";
}

/// <summary>
/// One repository the index tracks, together with the projects that resolve to
/// it. A repository can back more than one project handle, and the inventory is
/// project-scoped, so the capture step needs both.
/// </summary>
public sealed record GitIndexedRepository(
    string RepositoryRoot,
    IReadOnlyList<string> ProjectNames);

/// <summary>
/// The git-derived state of one repository as of <see cref="CapturedAtUtc"/>.
/// Everything a board or Project Hub request needs is already resolved here, so
/// the request path reads memory and never forks git.
/// </summary>
public sealed record GitRepositoryState(
    string RepositoryRoot,
    DateTimeOffset CapturedAtUtc,
    string? Head,
    IReadOnlyDictionary<string, string> BranchTips,
    IReadOnlyList<GitWorktreeEntry> Worktrees,
    IReadOnlyDictionary<string, GitProjectInventory> InventoryByProject);

/// <summary>
/// Freshness stamp carried by every request that reads git-derived state.
/// <see cref="GitStateAt"/> is the oldest capture across the repositories the
/// response covers; <see cref="Stale"/> says a change is already known but not
/// yet folded into that capture.
/// </summary>
public sealed record GitStateStamp(DateTimeOffset? GitStateAt, bool Stale)
{
    public static GitStateStamp Unknown { get; } = new(null, true);
}

/// <summary>
/// One completed index run, kept for the Admin page so an operator can see what
/// the last refresh of a repository cost without reading the log.
/// </summary>
public sealed record GitIndexRunRecord(
    string RepositoryRoot,
    string Trigger,
    DateTimeOffset CompletedAtUtc,
    long ElapsedMs,
    int Spawns,
    bool Succeeded);

/// <summary>
/// Maps a path that changed under a repository's git metadata directory onto an
/// index trigger, or to null when the change cannot move any state the index
/// holds. Pure, so the interesting cases are a direct matrix test rather than a
/// filesystem race.
/// </summary>
internal static class GitIndexPathClassifier
{
    /// <param name="relativePath">
    /// Path of the changed entry relative to the watched git metadata directory,
    /// in either separator style.
    /// </param>
    internal static string? Classify(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var path = relativePath.Replace('\\', '/').Trim('/');
        if (path.Length == 0) return null;

        // Git writes a lock file beside the ref it is about to replace and
        // removes it again. Reacting to both edges would double every run for
        // no new state.
        if (path.EndsWith(".lock", StringComparison.Ordinal)) return null;

        if (string.Equals(path, "HEAD", StringComparison.Ordinal)) return GitIndexTriggers.HeadChange;
        if (string.Equals(path, "packed-refs", StringComparison.Ordinal)) return GitIndexTriggers.PackedRefs;
        if (path.StartsWith("refs/", StringComparison.Ordinal)) return GitIndexTriggers.RefChange;

        // Repositories on the reftable backend keep every ref in this directory
        // instead of refs/, and rewrite tables.list on each update.
        if (path.StartsWith("reftable/", StringComparison.Ordinal)) return GitIndexTriggers.RefChange;

        if (path.StartsWith("worktrees/", StringComparison.Ordinal) || path == "worktrees")
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // worktrees/ and worktrees/<name> are the add and remove edges.
            if (segments.Length <= 2) return GitIndexTriggers.Worktree;
            var leaf = segments[^1];
            return leaf is "HEAD" or "gitdir" ? GitIndexTriggers.Worktree : null;
        }

        // index, COMMIT_EDITMSG, logs/, objects/, FETCH_HEAD and friends churn
        // constantly and move nothing the index projects.
        return null;
    }
}

/// <summary>
/// The scheduling decision for one repository, kept pure so the debounce and
/// single-flight rules are pinned by a truth table instead of by timing.
/// </summary>
internal static class GitIndexRunPolicy
{
    /// <summary>
    /// A run starts only when work is pending, no run is already in flight for
    /// that repository (single flight: concurrent triggers coalesce into the
    /// next run), the debounce window since the newest trigger has elapsed, and
    /// the cross-repository spawn budget has a free slot.
    /// </summary>
    internal static bool ShouldStartRun(
        bool hasPendingTrigger,
        bool running,
        bool debounceElapsed,
        bool slotAvailable)
        => hasPendingTrigger && !running && debounceElapsed && slotAvailable;

    /// <summary>
    /// A repository is due for the safety sweep when nothing else has refreshed
    /// it inside the sweep interval. The sweep is a backstop for git layouts the
    /// watcher cannot observe, never the normal invalidation path.
    /// </summary>
    internal static bool IsSweepDue(
        DateTimeOffset? lastRunAt,
        DateTimeOffset now,
        TimeSpan sweepInterval)
        => lastRunAt is null || now - lastRunAt.Value >= sweepInterval;
}

/// <summary>One breached service level, rendered as a warning on the Admin page.</summary>
public sealed record GitStateSloWarning(string Code, string Message);

/// <summary>
/// The Admin page's warning rules. Pure so the thresholds are a matrix test and
/// the numbers live in exactly one place.
/// </summary>
internal static class GitStateSloPolicy
{
    /// <summary>Board budget from AGT-2726: grouped p95 stays under one second.</summary>
    internal const double GroupedP95BudgetMs = 1000;

    /// <summary>Spawn budget from AGT-2726: at most twenty git processes a minute.</summary>
    internal const double SpawnsPerMinuteBudget = 20;

    internal static IReadOnlyList<GitStateSloWarning> Evaluate(
        double? groupedP95Ms,
        double spawnsPerMinute)
    {
        var warnings = new List<GitStateSloWarning>();
        if (groupedP95Ms is { } p95 && p95 > GroupedP95BudgetMs)
        {
            warnings.Add(new GitStateSloWarning(
                "grouped-p95",
                $"tasks/grouped p95 is {p95:0} ms over the last hour; the budget is {GroupedP95BudgetMs:0} ms."));
        }
        if (spawnsPerMinute > SpawnsPerMinuteBudget)
        {
            warnings.Add(new GitStateSloWarning(
                "spawn-budget",
                $"Git spawns are running at {spawnsPerMinute:0.#} per minute; the budget is {SpawnsPerMinuteBudget:0} per minute."));
        }
        return warnings;
    }
}
