using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Bus topics the Watcher produces. They ride the stable bus kinds of the
/// Agent Message Bus contract; no new kind and no competing Watcher-only feed
/// is introduced (dossier section 4a).
/// </summary>
public static class WatcherBusTopics
{
    public const string FindingRaised = "watcher-finding-raised";
    public const string AnalysisComplete = "watcher-analysis-complete";
    public const string DecisionRequired = "watcher-decision-required";
    public const string DecisionRecorded = "watcher-decision-recorded";
    public const string CaseResolved = "watcher-case-resolved";
    public const string ContingentExhausted = "watcher-contingent-exhausted";
    public const string Heartbeat = "watcher-heartbeat";

    public static readonly string[] All =
    [
        FindingRaised, AnalysisComplete, DecisionRequired, DecisionRecorded,
        CaseResolved, ContingentExhausted, Heartbeat,
    ];

    public static bool IsWatcherTopic(string? topic)
        => topic is not null && All.Contains(topic, StringComparer.Ordinal);
}

/// <summary>
/// Writes Watcher monitoring events onto the central event bus. Every event in
/// one case shares its correlation id with the durable case id and references
/// evidence rather than copying it.
/// </summary>
public sealed class WatcherBusPublisher
{
    /// <summary>The Watcher's registered bus identity (dossier section 4a).</summary>
    public const string ParticipantId = "orchestrator:global-watcher";
    public const string DisplayName = "Global Watcher";

    private readonly AgentMessageBusBridge? _bus;
    private readonly AgentMessageBusStore _store;
    private readonly WatcherStore _watcherStore;
    private readonly ILogger<WatcherBusPublisher> _logger;
    private int _participantRegistered;

    public WatcherBusPublisher(
        AgentMessageBusStore store,
        WatcherStore watcherStore,
        ILogger<WatcherBusPublisher> logger,
        AgentMessageBusBridge? bus = null)
    {
        _store = store;
        _watcherStore = watcherStore;
        _logger = logger;
        _bus = bus;
    }

    /// <summary>A new or reopened case. Acute until it resolves or becomes a decision.</summary>
    public Task FindingRaisedAsync(WatcherCase item, CancellationToken ct = default) => EmitAsync(
        item.Project,
        kind: "observation",
        severity: "Warn",
        topic: WatcherBusTopics.FindingRaised,
        correlationId: item.Id,
        summary: item.Title,
        payload: new
        {
            caseId = item.Id,
            detectorClass = item.DetectorClass,
            detectorRule = item.DetectorRule,
            fingerprint = item.Fingerprint,
            occurrences = item.Occurrences,
            sweepCount = item.SweepCount,
            firstSeenAtUtc = item.FirstSeenAtUtc,
            affectedCards = item.AffectedCards,
            evidenceDigest = item.EvidenceDigest,
        },
        tags: ["watcher", item.DetectorClass],
        ct: ct);

    /// <summary>An analysis receipt with its route and token attribution.</summary>
    public Task AnalysisCompleteAsync(
        WatcherCase item,
        WatcherProposal proposal,
        CancellationToken ct = default) => EmitAsync(
        item.Project,
        kind: "observation",
        severity: "Info",
        topic: WatcherBusTopics.AnalysisComplete,
        correlationId: item.Id,
        summary: $"Analysis complete for {item.Id}",
        payload: new
        {
            caseId = item.Id,
            proposalId = proposal.Id,
            evidenceDigest = proposal.EvidenceDigest,
            recommendation = proposal.Recommendation,
            modelCalls = proposal.ModelCalls,
        },
        tags: ["watcher", "analysis"],
        ct: ct);

    /// <summary>
    /// The operator entry point: a pending proposal. This is the row Activity
    /// renders as a Decision.
    /// </summary>
    public Task DecisionRequiredAsync(
        WatcherCase item,
        WatcherProposal proposal,
        CancellationToken ct = default) => EmitAsync(
        item.Project,
        kind: "decision",
        severity: "Warn",
        topic: WatcherBusTopics.DecisionRequired,
        correlationId: item.Id,
        summary: proposal.Kind == WatcherProposalKinds.Comment
            ? $"Review the Watcher comment on {proposal.CommentedOnTaskKey}: {proposal.Draft.Title}"
            : $"Approve the Watcher proposal {proposal.CreatedTaskKey}: {proposal.Draft.Title}",
        payload: new
        {
            caseId = item.Id,
            proposalId = proposal.Id,
            proposalKind = proposal.Kind,
            detectorClass = item.DetectorClass,
            createdTaskKey = proposal.CreatedTaskKey,
            commentedOnTaskKey = proposal.CommentedOnTaskKey,
            recommendedModel = proposal.Recommendation.Model,
            recommendedThinkingLevel = proposal.Recommendation.ThinkingLevel,
            recommendedTier = proposal.Recommendation.Tier,
            evidenceDigest = proposal.EvidenceDigest,
        },
        tags: ["watcher", "decision"],
        ct: ct);

    /// <summary>An attributable operator answer closes the decision.</summary>
    public Task DecisionRecordedAsync(WatcherProposal proposal, CancellationToken ct = default) => EmitAsync(
        proposal.Project,
        kind: "decision",
        severity: "Info",
        topic: WatcherBusTopics.DecisionRecorded,
        correlationId: proposal.CaseId,
        summary: $"Watcher proposal {proposal.Id} was {proposal.Decision.State}",
        payload: new
        {
            caseId = proposal.CaseId,
            proposalId = proposal.Id,
            decision = proposal.Decision,
            detectorClass = proposal.DetectorClass,
            createdTaskKey = proposal.CreatedTaskKey,
        },
        tags: ["watcher", "decision"],
        ct: ct);

    /// <summary>A case reached a terminal without a proposal.</summary>
    public Task CaseResolvedAsync(WatcherCase item, CancellationToken ct = default) => EmitAsync(
        item.Project,
        kind: "observation",
        severity: "Info",
        topic: WatcherBusTopics.CaseResolved,
        correlationId: item.Id,
        summary: $"{item.Title} ({item.State})",
        payload: new
        {
            caseId = item.Id,
            state = item.State,
            terminalReason = item.TerminalReason,
            detectorClass = item.DetectorClass,
        },
        tags: ["watcher", "resolved"],
        ct: ct);

    /// <summary>
    /// The contingent ran out. Counting continues, so this is an advisory with
    /// the size of the backlog it produced, not a failure.
    /// </summary>
    public Task ContingentExhaustedAsync(
        string dimension,
        int backlogCases,
        CancellationToken ct = default) => EmitAsync(
        project: null,
        kind: "advisory",
        severity: "Warn",
        topic: WatcherBusTopics.ContingentExhausted,
        correlationId: null,
        summary: $"The Watcher contingent is exhausted ({dimension}); {backlogCases} cases are waiting.",
        payload: new { dimension, backlogCases },
        tags: ["watcher", "contingent"],
        ct: ct);

    /// <summary>One-minute liveness signal an independent monitor can read.</summary>
    public Task HeartbeatAsync(WatcherSnapshot snapshot, CancellationToken ct = default) => EmitAsync(
        project: null,
        kind: "heartbeat",
        severity: "Info",
        topic: WatcherBusTopics.Heartbeat,
        correlationId: null,
        summary: $"Watcher sweep {snapshot.Sweeps}: {snapshot.OpenCases} open, {snapshot.PendingProposals} pending",
        payload: snapshot,
        tags: ["watcher"],
        ct: ct);

    private async Task EmitAsync(
        string? project,
        string kind,
        string severity,
        string topic,
        string? correlationId,
        string summary,
        object payload,
        string[] tags,
        CancellationToken ct)
    {
        if (_bus is null) return;
        try
        {
            await EnsureParticipantAsync(ct);
            var message = new AgentMessage
            {
                Id = Guid.CreateVersion7().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                ParticipantId = ParticipantId,
                Role = "system",
                Kind = kind,
                Severity = severity,
                Project = project,
                Topic = topic,
                CorrelationId = correlationId,
                Summary = Truncate(summary),
                Payload = JsonSerializer.SerializeToElement(payload, AgentMessageBusStore.SerializerOptions),
                Tags = tags,
            };
            await _bus.EmitAsync(message, ct);
        }
        catch (Exception ex)
        {
            // The durable case store is the record of truth. A bus append that
            // fails costs visibility, never a case.
            _logger.LogWarning(ex, "watcher-bus-emit-failed topic={Topic}", topic);
        }
    }

    private async Task EnsureParticipantAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _participantRegistered, 1) == 1) return;
        var root = _watcherStore.WorkspaceRoot;
        if (root is null) return;
        await _store.RegisterParticipantAsync(
            root,
            new AgentParticipant
            {
                Id = ParticipantId,
                Kind = "Orchestrator",
                DisplayName = DisplayName,
                CreatedAt = DateTime.UtcNow,
            },
            ct);
    }

    // The bus contract caps a summary at 280 characters.
    private static string Truncate(string value) =>
        value.Length <= 280 ? value : value[..279] + "…";
}
