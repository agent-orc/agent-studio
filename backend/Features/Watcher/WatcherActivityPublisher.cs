using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// Where a Watcher case becomes visible. One durable case projects to exactly
/// one row per material phase change in Activity across projects, plus the
/// matching typed row on the event bus.
/// </summary>
public interface IWatcherActivityPublisher
{
    /// <summary>A new case: an acute Problem row.</summary>
    void FindingRaised(WatcherCase watcherCase);

    /// <summary>A bounded analysis finished, with its route and token attribution.</summary>
    void AnalysisComplete(WatcherCase watcherCase, WatcherAnalysisReceipt receipt);

    /// <summary>A ticket proposal exists and waits for an operator: a Decision row.</summary>
    void ProposalCreated(WatcherCase watcherCase, WatcherProposal proposal);

    /// <summary>The operator answered. Approve and edit read as resolved; reject reads as quiet history.</summary>
    void ProposalDecided(WatcherCase watcherCase, WatcherProposal proposal);

    /// <summary>The contingent refused a spend. Counted once per case, not once per sweep.</summary>
    void ContingentExhausted(WatcherCase watcherCase, string reason);
}

/// <summary>
/// Projects Watcher phase changes into the two surfaces the dossier names: the
/// chronological Activity feed (`orchestrator.jsonl`, which "Activity across
/// projects" reads) and the Agent Message Bus.
/// </summary>
/// <remarks>
/// Both writes are best effort and neither one gates the sweep: a case is
/// durable in the Watcher store before anything is published, so a failed
/// projection loses a feed row, never an incident.
/// <para>
/// Identical checks are rollups, not feed rows. Only the four material phase
/// changes below publish; a sweep that finds nothing new updates counters on
/// the case and stays silent.
/// </para>
/// </remarks>
public sealed class WatcherActivityPublisher : IWatcherActivityPublisher
{
    private readonly OrchestratorLog _log;
    private readonly TaskScannerService _scanner;
    private readonly AgentMessageBusBridge? _bus;
    private readonly ILogger<WatcherActivityPublisher> _logger;

    public WatcherActivityPublisher(
        OrchestratorLog log,
        TaskScannerService scanner,
        ILogger<WatcherActivityPublisher> logger,
        AgentMessageBusBridge? bus = null)
    {
        _log = log;
        _scanner = scanner;
        _logger = logger;
        _bus = bus;
    }

    public void FindingRaised(WatcherCase watcherCase) => Publish(
        watcherCase,
        OrchestratorLogKinds.Alert,
        WatcherBusTopics.FindingRaised,
        "error",
        "High",
        watcherCase.Title,
        $"{watcherCase.CaseId} · {watcherCase.DetectorClass} · {watcherCase.Summary}",
        new
        {
            caseId = watcherCase.CaseId,
            watcherCase.Fingerprint,
            watcherCase.DetectorClass,
            watcherCase.DetectorRule,
            watcherCase.Occurrences,
            evidencePackDigest = watcherCase.EvidencePackDigest,
            affectedCards = watcherCase.AffectedCards,
        });

    public void AnalysisComplete(WatcherCase watcherCase, WatcherAnalysisReceipt receipt) => Publish(
        watcherCase,
        OrchestratorLogKinds.Observation,
        WatcherBusTopics.AnalysisComplete,
        "observation",
        "Info",
        $"Analysis complete for {watcherCase.CaseId}",
        $"Route {receipt.Route}; {receipt.Calls.Count} call(s), "
        + $"{receipt.TotalInputTokens} in / {receipt.TotalOutputTokens} out, "
        + $"cost {(receipt.TotalDollars is null ? "unknown" : receipt.TotalDollars.Value.ToString("C4", System.Globalization.CultureInfo.InvariantCulture))}.",
        new
        {
            caseId = watcherCase.CaseId,
            receipt.Route,
            calls = receipt.Calls,
            totalDollars = receipt.TotalDollars,
        });

    public void ProposalCreated(WatcherCase watcherCase, WatcherProposal proposal) => Publish(
        watcherCase,
        OrchestratorLogKinds.Decision,
        WatcherBusTopics.ProposalCreated,
        "decision",
        "Warn",
        proposal.Kind == WatcherProposalKinds.Comment
            ? $"Approve the Watcher note on {proposal.TargetTaskKey}?"
            : $"Approve the Watcher proposal {proposal.CreatedTaskKey ?? proposal.ProposalId}?",
        $"{watcherCase.Title} · recommended {proposal.Recommendation.Model}"
        + $"{(proposal.Recommendation.ThinkingLevel is null ? string.Empty : " at " + proposal.Recommendation.ThinkingLevel)}"
        + " · the card stays in the proposal state until an operator answers.",
        new
        {
            caseId = watcherCase.CaseId,
            proposal.ProposalId,
            proposal.Kind,
            proposal.CreatedTaskKey,
            proposal.TargetTaskKey,
            recommendation = proposal.Recommendation,
            decision = proposal.Decision,
        },
        proposal.CreatedTaskId);

    public void ProposalDecided(WatcherCase watcherCase, WatcherProposal proposal) => Publish(
        watcherCase,
        // An answered decision is settled history, so it renders quietly rather
        // than as a second acute row.
        OrchestratorLogKinds.Observation,
        WatcherBusTopics.ProposalDecided,
        "decision",
        "Info",
        $"Watcher proposal {proposal.ProposalId} {proposal.Decision}",
        $"{watcherCase.Title} · {proposal.Decision} by {proposal.DecidedBy ?? "an operator"}"
        + (proposal.DecisionReason is { Length: > 0 } ? $" · {proposal.DecisionReason}" : string.Empty),
        new
        {
            caseId = watcherCase.CaseId,
            proposal.ProposalId,
            proposal.Decision,
            proposal.DecisionReason,
            proposal.DecidedBy,
            proposal.Edited,
            proposal.CreatedTaskKey,
        },
        proposal.CreatedTaskId);

    public void ContingentExhausted(WatcherCase watcherCase, string reason) => Publish(
        watcherCase,
        OrchestratorLogKinds.Alert,
        WatcherBusTopics.ContingentExhausted,
        "advisory",
        "Warn",
        $"Watcher contingent exhausted; {watcherCase.CaseId} stays unanalysed",
        $"{reason}. Detection and counting continue; the backlog is visible in CLI Management.",
        new { caseId = watcherCase.CaseId, reason });

    private void Publish(
        WatcherCase watcherCase,
        string feedKind,
        string topic,
        string busKind,
        string severity,
        string summary,
        string detail,
        object payload,
        string? jobId = null)
    {
        AppendToActivity(watcherCase, feedKind, topic, summary, detail, jobId);
        EmitToBus(watcherCase, topic, busKind, severity, summary, detail, payload, jobId);
    }

    private void AppendToActivity(
        WatcherCase watcherCase,
        string feedKind,
        string topic,
        string summary,
        string detail,
        string? jobId)
    {
        foreach (var watchPath in WatchPaths(watcherCase.Project))
        {
            _log.Append(watchPath, new OrchestratorLogEntry
            {
                Ts = DateTime.UtcNow,
                Kind = feedKind,
                Topic = topic,
                Summary = summary,
                Reasoning = detail,
                JobId = jobId,
                ParticipantId = WatcherParticipant.Id,
            });
        }
    }

    /// <summary>
    /// A workspace-scoped case has no owning project. The feed is per-project
    /// on disk, so it is written once to the first watch path rather than fanned
    /// out, which would show one finding many times.
    /// </summary>
    private IEnumerable<string> WatchPaths(string? project)
    {
        var all = _scanner.GetWatchPaths();
        if (!string.IsNullOrWhiteSpace(project))
        {
            var match = all.FirstOrDefault(row =>
                string.Equals(row.Name, project, StringComparison.OrdinalIgnoreCase));
            if (match?.Path is { Length: > 0 }) return [match.Path];
        }

        var first = all.Select(row => row.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        return first is null ? [] : [first];
    }

    private void EmitToBus(
        WatcherCase watcherCase,
        string topic,
        string busKind,
        string severity,
        string summary,
        string detail,
        object payload,
        string? jobId)
    {
        if (_bus is null) return;
        try
        {
            var message = new AgentMessage
            {
                Id = Guid.CreateVersion7().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                ParticipantId = WatcherParticipant.Id,
                Role = WatcherParticipant.Role,
                Kind = busKind,
                Severity = severity,
                Project = watcherCase.Project,
                JobId = jobId,
                Topic = topic,
                // The bus caps a summary at 280 characters; a longer headline is
                // a report bug, but it must not cost the event.
                Summary = summary.Length > 280 ? summary[..277] + "..." : summary,
                Body = detail,
                CorrelationId = watcherCase.CaseId,
                Payload = JsonSerializer.SerializeToElement(payload, AgentMessageBusStore.SerializerOptions),
                Tags = [WatcherTags.Proposal, WatcherTags.ForClass(watcherCase.DetectorClass)],
            };
            _ = _bus.EmitAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "watcher-bus-emit-failed case={CaseId} topic={Topic}", watcherCase.CaseId, topic);
        }
    }
}
