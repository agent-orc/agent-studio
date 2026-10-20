using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3218: an accepted review whose integration ended in Conflict or
/// AgentRoundRequired and started the automatic agent round. The round service
/// moves the card to Ready inside the integration, so the engine decision owes
/// neither the Human Review move nor the park verdict; the next delivery is
/// reviewed again. The applier and the applier canary read it the same way.
/// </summary>
public static class IntegrationAgentRoundHandoff
{
    /// <summary>
    /// The round row that this review's own integration wrote, or null. It
    /// counts only when every fence holds:
    /// <list type="bullet">
    /// <item>the settlement journal names the review (an older review generation does not count);</item>
    /// <item>on the applier's first pass, <paramref name="firstPass"/> is Conflict or
    /// AgentRoundRequired and spent no exhausted budget (an exhausted budget
    /// leaves the card in Auto Review for Human Review);</item>
    /// <item>the row is the <see cref="IntegrationAgentRoundService"/> row of the
    /// reviewed SHA (reason <see cref="IntegrationAgentRoundService.AttributionAmbiguousReason"/>;
    /// acceptance-rail and recovery-service rows carry the SHA too), written
    /// at or after the card's newest enqueued or agent-round re-entry lane row,
    /// so a round of an earlier review of the same delivery does not count;</item>
    /// <item>the card is no longer in <paramref name="sourceLane"/>; a card
    /// back in Auto Review is reviewed and integrated again, whatever rows an
    /// earlier review left.</item>
    /// </list>
    /// </summary>
    public static TimelineEvent? Find(TimelineLog timeline, TaskInfo task, string sourceLane,
        string reviewAttemptId, string? reviewedSha, MergeIntoIntegrationResult? firstPass = null)
    {
        if (string.IsNullOrWhiteSpace(reviewedSha)
            || RemoteDeliverySettlementStore.Read(task.FolderPath)?.ReviewAttemptId != reviewAttemptId)
            return null;
        if (task.State == sourceLane) return null;
        if (firstPass is not null && (firstPass.Outcome is not (MergeIntoIntegrationOutcome.Conflict
                or MergeIntoIntegrationOutcome.AgentRoundRequired)
            || firstPass.AutomaticRecoveryBudgetUsed is not null))
            return null;
        if (IntegrationLaneEvents.Read(task.FolderPath).LastOrDefault(entry =>
                entry.Kind is "enqueued" or "agent-round-re-entry") is not { } enqueued)
            return null;
        var since = enqueued.AtUtc.UtcDateTime;
        return timeline.ReadAll(task.FolderPath).LastOrDefault(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && entry.Ts >= since
            && entry.Details is { } details
            && details.GetValueOrDefault("reason") == IntegrationAgentRoundService.AttributionAmbiguousReason
            && details.GetValueOrDefault("automatic") == "true"
            && !details.ContainsKey("source")
            && string.Equals(details.GetValueOrDefault("resultSha"), reviewedSha, StringComparison.OrdinalIgnoreCase));
    }
}
