using AgentStudio.Git;

namespace AgentStudio.Admin;

/// <summary>
/// The board-performance SLO panel (AGT-2726 requirement 4): p50/p95 per
/// git-touching endpoint over the last hour, index age per repository, and the
/// spawn rate - with an explicit warning when the board's p95 or the process
/// budget is breached.
///
/// <para>
/// Every number is read from <see cref="GitProcessTelemetry"/> and
/// <see cref="GitStateIndex"/>. There is deliberately no second counter: the
/// same samples that produce the <c>git-info request=</c> log lines produce
/// this panel, so a log-based investigation and the panel can never disagree.
/// </para>
/// </summary>
public static class GitPerformanceEndpoints
{
    /// <summary>The endpoint whose p95 the board SLO is written against.</summary>
    internal const string BoardEndpoint = "tasks/grouped";

    public static void MapGitPerformanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/git-performance", (
            GitStateIndex index,
            GitBackgroundExecutor executor) =>
        {
            var options = index.Options;
            var performance = GitProcessTelemetry.Window.Snapshot(DateTimeOffset.UtcNow);
            var repositories = index.Status();
            return Results.Ok(new GitPerformanceReport(
                performance.Endpoints,
                repositories,
                performance.SpawnsInWindow,
                performance.SpawnsPerMinute,
                performance.SpawnsLastMinute,
                executor.QueueDepth,
                executor.DegreeOfParallelism,
                (long)options.GroupedP95Budget.TotalMilliseconds,
                options.SpawnsPerMinuteBudget,
                BuildWarnings(performance, repositories, options)));
        });
    }

    /// <summary>
    /// The two SLO breaches worth an operator's attention, plus the one that
    /// makes the other two meaningless: a request path that spawned git at all.
    /// Pure over its inputs so the thresholds are testable without a host.
    /// </summary>
    internal static IReadOnlyList<string> BuildWarnings(
        GitPerformanceSnapshot performance,
        IReadOnlyList<GitIndexRepositoryStatus> repositories,
        GitStateIndexOptions options)
    {
        var warnings = new List<string>();
        var budgetMs = (long)options.GroupedP95Budget.TotalMilliseconds;

        var board = performance.Endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Endpoint, BoardEndpoint, StringComparison.Ordinal));
        if (board is not null && board.P95Ms > budgetMs)
        {
            warnings.Add(
                $"{BoardEndpoint} p95 is {board.P95Ms} ms over the last hour; the budget is {budgetMs} ms.");
        }

        if (performance.SpawnsLastMinute > options.SpawnsPerMinuteBudget)
        {
            warnings.Add(
                $"Git spawns reached {performance.SpawnsLastMinute} in the last minute; the budget is {options.SpawnsPerMinuteBudget}.");
        }

        // A request path with a non-zero spawn count is the invariant this whole
        // change exists to hold. Report it by name rather than as a latency
        // symptom - the latency is the consequence, not the defect.
        foreach (var endpoint in performance.Endpoints.Where(endpoint =>
                     endpoint.Spawns > 0 && RequestPathEndpoints.Contains(endpoint.Endpoint)))
        {
            warnings.Add(
                $"{endpoint.Endpoint} spawned {endpoint.Spawns} git process(es) on the request path; it must read the index instead.");
        }

        foreach (var repository in repositories.Where(repository =>
                     repository.IndexAgeSeconds is > 60 && !repository.Running))
        {
            warnings.Add(
                $"Git index for {repository.ProjectName} is {repository.IndexAgeSeconds:0} s old.");
        }

        return warnings;
    }

    /// <summary>
    /// Telemetry labels that are opened by an HTTP handler. Anything else in
    /// the window is background work, where spawns are expected.
    /// </summary>
    internal static readonly HashSet<string> RequestPathEndpoints = new(StringComparer.Ordinal)
    {
        "tasks/grouped",
        "tasks/list",
    };
}

/// <summary>The Admin panel payload. Additive; no existing route changes shape.</summary>
public sealed record GitPerformanceReport(
    IReadOnlyList<GitEndpointPerformance> Endpoints,
    IReadOnlyList<GitIndexRepositoryStatus> Repositories,
    int SpawnsInWindow,
    double SpawnsPerMinute,
    int SpawnsLastMinute,
    int QueueDepth,
    int IndexConcurrency,
    long BoardP95BudgetMs,
    int SpawnsPerMinuteBudget,
    IReadOnlyList<string> Warnings);
