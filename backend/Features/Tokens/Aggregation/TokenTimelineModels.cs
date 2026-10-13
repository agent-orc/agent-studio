namespace AgentStudio.Tokens;

/// <summary>
/// Workspace-wide token timeline response. One entry per
/// (project, time bucket) cell that had at least one orchestrator LLM
/// call inside the window.
/// </summary>
public sealed record TokenTimeline(
    string WindowStart,
    string WindowEnd,
    int WindowHours,
    int BucketMinutes,
    int BucketCount,
    IReadOnlyList<TokenTimelineCell> Cells,
    IReadOnlyList<TokenTimelineProject> Projects,
    string FetchedAt,
    string Disclaimer)
{
    /// <summary>
    /// Separate project/week lines for calls that ran after an admission
    /// decision with a better benchmark candidate for the selected route.
    /// </summary>
    public IReadOnlyList<BetterCandidateUsageLine> BetterCandidateUsage { get; init; } = [];

    /// <summary>
    /// Per (project, model id, executing host) usage over the window
    /// (AGT-2986). Sums reconcile to the per-project totals.
    /// </summary>
    public IReadOnlyList<TokenTimelineModelUsage> Models { get; init; } = [];

    /// <summary>Read health of the merged usage ledger behind this response.</summary>
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
}

/// <summary>
/// One (project, model, host) row. <see cref="Model"/> is the stored id;
/// <see cref="ModelLabel"/> is resolved from the registry for this response
/// and equals the id when the registry does not know it. <see cref="Host"/>
/// is a remote runner id, <c>local</c>, or <c>remote-unrecorded</c> for
/// remote receipts written before host attribution.
/// </summary>
public sealed record TokenTimelineModelUsage(
    string Project,
    string Model,
    string ModelLabel,
    string Host,
    IReadOnlyList<string> CliTypes,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced);

/// <summary>Share of one project's window total executed on one host.</summary>
public sealed record TokenTimelineHostShare(string Host, int Calls, long Total);

/// <summary>
/// One (project, bucket) cell. <see cref="AllModelsPriced"/> is false
/// when at least one call in the bucket used a model that is not in
/// <see cref="TokenPricing.Catalog"/>; <see cref="Dollars"/> in that
/// case covers only the priced subset. <see cref="AgentTokens"/> +
/// <see cref="SupportingTokens"/> + <see cref="OrchestratorTokens"/> add
/// up to <see cref="Total"/>; the split lets the UI show the orchestrator
/// share separately (AGT-2038).
/// </summary>
public sealed record TokenTimelineCell(
    string Project,
    string BucketStart,
    string BucketEnd,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced,
    long AgentTokens,
    long SupportingTokens,
    long OrchestratorTokens);

/// <summary>
/// Per-project rollup over the full window. Drives the legend and the
/// summary table under the chart. <see cref="AgentTokens"/> +
/// <see cref="SupportingTokens"/> + <see cref="OrchestratorTokens"/> add
/// up to <see cref="Total"/> so the table can carry a Total / davon Agent
/// / davon Orchestrator split (AGT-2038). <see cref="LastActivity"/> now
/// reflects the newest real activity of any kind - an agent run counts,
/// not only the last orchestrator call.
/// </summary>
public sealed record TokenTimelineProject(
    string Project,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced,
    string? PeakBucketStart,
    long PeakBucketTotal,
    string? LastActivity,
    long AgentTokens,
    long SupportingTokens,
    long OrchestratorTokens)
{
    /// <summary>Executing hosts of this project's window total, largest first (AGT-2986).</summary>
    public IReadOnlyList<TokenTimelineHostShare> Hosts { get; init; } = [];
}
