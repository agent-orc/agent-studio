namespace AgentStudio.Watcher;

/// <summary>One Watcher bus event projected into the Activity entry grid.</summary>
/// <param name="Project">Project chip label. Workspace-wide events use the Watcher's own label.</param>
/// <param name="Entry">The row, in the existing orchestrator-feed shape.</param>
/// <param name="SourceProject">Bus scope, or null for workspace-wide. Used for access filtering.</param>
public sealed record WatcherActivityEntry(string Project, OrchestratorLogEntry Entry, string? SourceProject);

/// <summary>
/// Projects Watcher bus events into the Activity across projects read model.
/// </summary>
/// <remarks>
/// <para>
/// Section 4a is explicit that Activity is the chronological view of the event
/// bus, not a second monitoring system, and that no new bus kind or competing
/// Watcher-only feed is required. So the Watcher writes stable bus kinds with
/// its own topics, and this projection maps those topics onto the entry grid
/// the feed already renders.
/// </para>
/// <para>
/// The heartbeat is deliberately not projected. A liveness beat every minute is
/// for an independent monitor, not for the operator's chronological feed.
/// </para>
/// </remarks>
public sealed class WatcherActivityProjection
{
    /// <summary>Project chip shown for workspace-wide Watcher events.</summary>
    public const string GlobalProjectLabel = "Global Watcher";

    /// <summary>Upper bound on Watcher rows merged into one feed response.</summary>
    public const int DefaultLimit = 200;

    private readonly AgentMessageBusStore _store;
    private readonly WatcherStore _watcherStore;
    private readonly ILogger<WatcherActivityProjection> _logger;

    public WatcherActivityProjection(
        AgentMessageBusStore store,
        WatcherStore watcherStore,
        ILogger<WatcherActivityProjection> logger)
    {
        _store = store;
        _watcherStore = watcherStore;
        _logger = logger;
    }

    /// <summary>
    /// Watcher rows for the given bus scopes, newest last. Pass the project
    /// names the caller may read; workspace-wide rows are included only when at
    /// least one project is readable.
    /// </summary>
    public IReadOnlyList<WatcherActivityEntry> Read(
        IReadOnlyList<string> readableProjects,
        int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        var root = _watcherStore.WorkspaceRoot;
        if (root is null) return [];

        var entries = new List<WatcherActivityEntry>();
        var scopes = new List<string?>();
        if (readableProjects.Count > 0) scopes.Add(null);
        scopes.AddRange(readableProjects.Select(name => (string?)name));

        foreach (var scope in scopes)
        {
            try
            {
                var messages = _store.Query(
                    root,
                    scope,
                    new AgentMessageQuery(
                        ParticipantId: WatcherBusPublisher.ParticipantId,
                        Limit: Math.Clamp(limit, 1, 1_000)),
                    ct);
                foreach (var message in messages)
                {
                    var entry = ToEntry(message);
                    if (entry is null) continue;
                    entries.Add(new WatcherActivityEntry(
                        scope ?? GlobalProjectLabel,
                        entry,
                        scope));
                }
            }
            catch (Exception ex)
            {
                // The feed must still render its ordinary rows when the Watcher
                // projection cannot be read.
                _logger.LogWarning(ex, "watcher-activity-projection-failed scope={Scope}", scope ?? "_workspace");
            }
        }

        return entries
            .OrderBy(item => item.Entry.Ts)
            .TakeLast(Math.Clamp(limit, 1, 1_000))
            .ToList();
    }

    /// <summary>
    /// Map one Watcher bus message onto the entry grid, or null when the topic
    /// is not an operator-facing row.
    /// </summary>
    public static OrchestratorLogEntry? ToEntry(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var kind = message.Topic switch
        {
            // A new or reopened case is acute until it resolves.
            WatcherBusTopics.FindingRaised => OrchestratorLogKinds.Alert,
            // The operator entry point of review mode.
            WatcherBusTopics.DecisionRequired => OrchestratorLogKinds.Decision,
            // An attributable answer is work performed, not a pending question.
            WatcherBusTopics.DecisionRecorded => OrchestratorLogKinds.Action,
            // Settled history stays visually quiet.
            WatcherBusTopics.AnalysisComplete => OrchestratorLogKinds.Observation,
            WatcherBusTopics.CaseResolved => OrchestratorLogKinds.Observation,
            // A closed budget is acute: the Watcher keeps counting but stops working.
            WatcherBusTopics.ContingentExhausted => OrchestratorLogKinds.Alert,
            // Liveness belongs to the health monitor, not to the feed.
            _ => null,
        };
        if (kind is null) return null;

        return new OrchestratorLogEntry
        {
            Ts = message.CreatedAt,
            Kind = kind,
            Topic = message.Topic ?? "watcher",
            Summary = message.Summary ?? "",
            Reasoning = message.Body,
            JobId = message.JobId,
            ParticipantId = message.ParticipantId,
            // The Watcher's correlation id is the durable case id, so a row in
            // Activity leads back to its case without parsing the summary.
            CorrelationId = message.CorrelationId,
        };
    }
}
