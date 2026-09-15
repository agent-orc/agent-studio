namespace AgentStudio.Docs;

/// <summary>
/// One immutable published wiki revision. Readers hold a reference to this
/// record; promotion swaps the reference in one assignment after the snapshot
/// is completely materialized, so a concurrent reader always sees either the
/// whole previous tree or the whole new one and never a mixed state.
/// </summary>
public sealed record WikiPublishedRevision(
    string ProjectKey,
    string SourceRef,
    string Sha,
    string ShortSha,
    string SnapshotRoot,
    DateTime PromotedAtUtc,
    string? PreviousSha);

/// <summary>How a publication attempt ended.</summary>
public enum WikiPublicationStatus
{
    Disabled,
    NoOp,
    Promoted,
    RolledBack,
    /// <summary>
    /// A rollback pinned the project, so the scheduled trigger deliberately did
    /// not advance it. Without this state the next tick would re-promote the
    /// revision the operator just rolled back from, and the rollback would last
    /// less than one interval.
    /// </summary>
    Held,
    Failed,
}

/// <summary>
/// Deployment evidence for one attempt: what happened, between which SHAs, and
/// the typed failure when it did not advance. Recorded per project and returned
/// by the publication diagnostics endpoint.
/// </summary>
public sealed record WikiPublicationOutcome(
    string ProjectKey,
    string? SourceRef,
    WikiPublicationStatus Status,
    WikiPublicationFailure Failure,
    string Reason,
    string? FromSha,
    string? ToSha,
    DateTime AtUtc,
    long DurationMs,
    string Trigger);

/// <summary>
/// The publication state the diagnostics surface reports: which commit is
/// online right now, which one it replaced, and how the last attempt ended.
/// </summary>
public sealed record WikiPublicationReport(
    string ProjectName,
    string? SourceRef,
    bool Enabled,
    string? PublishedSha,
    string? PublishedShortSha,
    DateTime? PublishedAtUtc,
    string? PreviousSha,
    bool RollbackAvailable,
    bool Held,
    WikiPublicationOutcome? LastOutcome,
    WikiPublicationOutcome? LastFailure,
    int IntervalSeconds);

/// <summary>
/// Resolved publication settings. Kept as a record with a pure
/// <see cref="FromConfiguration"/> reader so the clamping matrix is testable
/// without a host, and so every consumer sees the same effective values.
/// </summary>
public sealed record WikiPublicationOptions(
    bool Enabled,
    string Remote,
    int IntervalSeconds,
    int SnapshotRetention,
    IReadOnlyList<string> AcceptedRefs)
{
    public const int MinIntervalSeconds = 15;

    /// <summary>
    /// Ceiling for the sync interval. The published freshness SLO is five
    /// minutes, so a configured interval above four minutes cannot be honoured
    /// together with one fetch plus one promotion inside the window.
    /// </summary>
    public const int MaxIntervalSeconds = 240;

    public static WikiPublicationOptions Defaults { get; } =
        new(false, "origin", 120, 3, WikiPublicationPolicy.DefaultAcceptedRefs);

    public static WikiPublicationOptions FromConfiguration(IConfiguration? configuration)
    {
        if (configuration == null) return Defaults;

        var accepted = configuration
            .GetSection("WikiPublication:AcceptedRefs")
            .Get<string[]>();

        return new WikiPublicationOptions(
            Enabled: configuration.GetValue<bool?>("WikiPublication:Enabled") ?? Defaults.Enabled,
            Remote: Blank(configuration.GetValue<string?>("WikiPublication:Remote"))
                ? Defaults.Remote
                : configuration.GetValue<string>("WikiPublication:Remote")!.Trim(),
            IntervalSeconds: Math.Clamp(
                configuration.GetValue<int?>("WikiPublication:IntervalSeconds") ?? Defaults.IntervalSeconds,
                MinIntervalSeconds,
                MaxIntervalSeconds),
            SnapshotRetention: Math.Clamp(
                configuration.GetValue<int?>("WikiPublication:SnapshotRetention") ?? Defaults.SnapshotRetention,
                2,
                50),
            AcceptedRefs: accepted is { Length: > 0 }
                ? accepted.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList()
                : Defaults.AcceptedRefs);
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
