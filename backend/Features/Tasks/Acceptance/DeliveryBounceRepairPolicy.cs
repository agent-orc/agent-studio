namespace AgentStudio.Tasks;

/// <summary>Exact incident selector for the 2026-10-11 false stale-delivery bounce.</summary>
public static class DeliveryBounceRepairPolicy
{
    private static readonly DateTime IncidentDate = new(2026, 10, 11);

    public static bool IsEligible(TaskInfo card, TaskIntegrationStatus? status,
        IReadOnlyList<TimelineEvent> timeline, bool hasOperatorOverride = false)
    {
        if (card.State != TaskStates.HumanReview || card.CompletionClaim is not null
            || status?.Status != IntegrationStatuses.Integrated && !hasOperatorOverride) return false;

        var laneChanges = timeline.Where(row => row.Kind == TimelineEventKinds.LaneChanged)
            .TakeLast(2).ToArray();
        if (laneChanges.Length != 2) return false;
        return IsMove(laneChanges[0], TaskStates.Completed, TaskStates.AutoReview)
            && IsMove(laneChanges[1], TaskStates.AutoReview, TaskStates.HumanReview);
    }

    private static bool IsMove(TimelineEvent row, string from, string to)
        => row.Ts.Date == IncidentDate
           && row.Details?.GetValueOrDefault("from") == from
           && row.Details?.GetValueOrDefault("to") == to
           && row.Details?.GetValueOrDefault(LaneChangeCauses.DetailKey)
               == LaneChangeCauses.AcceptanceIntegrationFailed
           && row.Details?.GetValueOrDefault(LaneChangeCauses.DetailQualifierKey)
               == "delivery-verdict-stale";
}
