using AgentStudio.Bus;
using AgentStudio.Tasks;

namespace AgentStudio.Watcher;

/// <summary>
/// AGT-3207: shows one intervention receipt on the event (the Activity feed
/// entry next to the stall finding), on every affected card (a timeline
/// entry) and on the host (the intervention list that the host view reads).
/// An escalation carries the whole receipt chain.
/// </summary>
public sealed class StallInterventionReceipts(
    OrchestratorLog log,
    WatcherActivityProjector projector,
    TaskScannerService scanner,
    TimelineLog timeline,
    ILogger<StallInterventionReceipts> logger)
{
    private const int MaxCardTimelines = 25;

    public void Publish(StallInterventionRecord record, IReadOnlyList<StallInterventionRecord> all)
    {
        var chain = record.Chain.Select(id => all.FirstOrDefault(item => item.Id == id))
            .OfType<StallInterventionRecord>().ToArray();
        var reasoning = record.Status == StallInterventionStatuses.Escalated && chain.Length > 0
            ? record.Receipt + " Receipt chain: " + string.Join(" | ",
                chain.Select(item => $"{item.Id} {item.Action} ({item.Status}): {item.Done}"))
            : record.Receipt;
        if (record.Outcome is not null) reasoning += " Outcome: " + record.Outcome;
        var summary = Headline(record);
        var watchPath = projector.WatchPathFor(record.Project);
        if (!string.IsNullOrWhiteSpace(watchPath))
            log.Append(watchPath, new OrchestratorLogEntry
            {
                Kind = record.Status switch
                {
                    StallInterventionStatuses.Escalated => OrchestratorLogKinds.Alert,
                    StallInterventionStatuses.Proposed => OrchestratorLogKinds.Decision,
                    _ => OrchestratorLogKinds.Intervention,
                },
                Topic = "stall-intervention",
                Summary = summary,
                Reasoning = reasoning,
                JobId = record.AffectedCards.FirstOrDefault(),
                ParticipantId = AgentMessageBusBridge.ParticipantWatcher,
                Intervention = View(record),
            });
        projector.EmitIntervention(record.Project, record.AffectedCards.FirstOrDefault(), summary,
            record.Status == StallInterventionStatuses.Escalated ? "Warn" : "Info", View(record), record.Id);

        if (record.AffectedCards.Count == 0) return;
        var jobs = scanner.ScanAllJobs();
        foreach (var card in record.AffectedCards.Take(MaxCardTimelines))
        {
            var job = jobs.FirstOrDefault(item => item.Id == card || item.Key == card || item.TaskKey == card);
            if (job is null) continue;
            if (!timeline.Append(job.FolderPath, TimelineEventKinds.StallInterventionRecorded, TimelineActors.Orchestrator,
                    summary + " " + record.Receipt, details: new Dictionary<string, string>
                    {
                        ["interventionId"] = record.Id,
                        ["subject"] = record.Subject,
                        ["action"] = record.Action,
                        ["autonomy"] = record.Autonomy,
                        ["status"] = record.Status,
                        ["undo"] = record.Undo,
                    }))
                logger.LogDebug("stall-intervention-timeline-skipped card={Card} intervention={Id}", card, record.Id);
        }
    }

    public static StallInterventionReceiptEvent View(StallInterventionRecord record)
        => new(record.Id, record.Subject, record.Action, record.Autonomy, record.Status, record.Observed,
            record.Done, record.Why, record.Before, record.After, record.Undo, record.Proposal, record.Chain,
            record.Undoable && record.UndoneAtUtc is null, record.EffectEvidence, record.Outcome);

    /// <summary>One line for the feed: who, which class, and the action in plain words.</summary>
    public static string Headline(StallInterventionRecord record) => record.Status switch
    {
        StallInterventionStatuses.Undone => $"Undone by {record.UndoneBy}: {record.Done}",
        StallInterventionStatuses.Proposed => $"AI Operator proposal ({record.Autonomy}) for {record.Subject}: {record.Proposal}",
        StallInterventionStatuses.Escalated => $"AI Operator escalated {record.Subject}: {record.Observed}",
        _ => $"AI Operator ({record.Autonomy}) on {record.Subject}: {record.Done}",
    };
}
