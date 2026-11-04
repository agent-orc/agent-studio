namespace AgentStudio.Shared;

// QuotaWindow and QuotaSnapshot live in the contracts package
// (ModelSelection/QuotaSnapshotContracts.cs) so the shared planner reads them.

public record QuotaReport
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    /// <summary>Cache TTL (seconds) the backend is using; the UI computes a "stale" badge as <c>now - snapshot.fetchedAt &gt; ttlSeconds</c>.</summary>
    public int TtlSeconds { get; init; }
    public List<QuotaSnapshot> Snapshots { get; init; } = [];
}
