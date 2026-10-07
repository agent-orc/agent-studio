

namespace AgentStudio.Tokens;

/// <summary>
/// Canonical token-aggregation surface. The union of every roll-up the
/// observability surfaces consume today (project-detail token-usage panel,
/// workspace timeline, status-bar usage modal, ad-hoc usage chart, job-card
/// footers). New consumers depend on this interface rather than the legacy
/// per-surface services so the implementation can move under them without
/// breaking call sites.
///
/// <para>
/// <b>Consolidation status (2026-08-09).</b> Project, lifetime, workspace
/// aggregate, and task-card reads combine historical bus events with durable
/// task token receipts. The legacy services
/// (<see cref="ProjectTokenUsageService"/>, <see cref="TokenSummaryService"/>,
/// <see cref="WorkspaceTokensTimelineService"/>, <see cref="AdHocUsageService"/>)
/// still hold pure fold helpers and parity fixtures, but new consumers use
/// this interface. See <c>docs/system/domains/tokens.md</c> for the full plan.
/// </para>
/// </summary>
public interface ITokenAggregator
{
    /// <summary>
    /// Bus-native diagnostic rollup for one project. Pre-computed buckets when no
    /// since/until is supplied; bounded scan otherwise. The canonical
    /// dimensions are byModel / byParticipant / byDay; the response also
    /// carries lifetime totals.
    /// </summary>
    TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default);

    /// <summary>Lifetime + last-24h totals with Job/Supporting/Orchestrator split.</summary>
    ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null);

    /// <summary>Per-job × per-day heatmap over the requested window.</summary>
    ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null);

    /// <summary>Top-N jobs by total token spend.</summary>
    IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit);

    /// <summary>Per-call drill-down for one job with delta-vs-prior.</summary>
    ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId);

    /// <summary>Per-project lifetime totals + per-model split + estimated USD.</summary>
    TokenSummary LifetimeSummary(string projectName, string watchPath);

    /// <summary>Workspace-wide aggregate across every watched project.</summary>
    TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects);

    /// <summary>Last persisted workspace aggregate, used for instant status-bar rendering.</summary>
    TokenSummaryAggregate? CachedWorkspaceAggregate();

    /// <summary>Per-job rollup for one project's job-card token footers.</summary>
    Dictionary<string, TaskTokenSummary> WorkspacePerJob(string projectName, string watchPath);

    /// <summary>(project, time-bucket) cells for the workspace tokens timeline.</summary>
    TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null);

    /// <summary>Workspace timeline for an exact UTC calendar interval.</summary>
    TokenTimeline WorkspaceTimelineRange(IEnumerable<(string Name, string WatchPath)> projects, DateTime fromUtc, DateTime toUtc, int bucketMinutes);

    /// <summary>Workspace-wide ad-hoc one-shot call rollup (TitleGen, SummaryGen, ...).</summary>
    AdHocUsageAggregate AdHocAggregate(DateTime? since = null);
}
