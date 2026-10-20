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
    /// The recovery-queued row of the reviewed delivery when the decision's
    /// settlement journal names the review: the journal fences an older review
    /// generation, and the result SHA fences an earlier round of the card.
    /// </summary>
    public static TimelineEvent? Find(TimelineLog timeline, string folderPath, string reviewAttemptId,
        string? reviewedSha)
    {
        if (string.IsNullOrWhiteSpace(reviewedSha)
            || RemoteDeliverySettlementStore.Read(folderPath)?.ReviewAttemptId != reviewAttemptId)
            return null;
        return timeline.ReadAll(folderPath).LastOrDefault(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && string.Equals(entry.Details?.GetValueOrDefault("resultSha"), reviewedSha,
                StringComparison.OrdinalIgnoreCase));
    }
}
