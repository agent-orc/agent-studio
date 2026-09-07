namespace AgentStudio.Tasks;

/// <summary>
/// Reads the acceptance rail's own timeline receipts (AGT-2749).
/// <para>
/// An infrastructure replay writes no recovery intent and no steer, so the rail
/// has nothing to count but the receipts it wrote itself. Two callers need the
/// same number: the rail, to spend its retry budget, and the card projection, to
/// render "retry 2/3" in the lane. Keeping the definition here stops the lane
/// from disagreeing with the policy that moved the card.
/// </para>
/// </summary>
public static class AcceptanceRailReceipts
{
    /// <summary>Timeline detail value written by an infrastructure replay.</summary>
    public const string InfrastructureRequeueAction = "requeued-infrastructure";

    /// <summary>
    /// Infrastructure replays already spent on this card, plus the instant of
    /// the last one. Returns <c>(0, null)</c> for a card that was never replayed.
    /// </summary>
    public static (int Count, DateTimeOffset? LastAt) CountInfrastructureRequeues(
        TimelineLog timeline,
        string folderPath)
    {
        var receipts = timeline.ReadAll(folderPath)
            .Where(entry => entry.Kind == TimelineEventKinds.AcceptanceRailActed
                            && entry.Details?.GetValueOrDefault("action") == InfrastructureRequeueAction)
            .ToList();
        if (receipts.Count == 0) return (0, null);

        var last = receipts.Max(entry => entry.Ts);
        if (last.Kind != DateTimeKind.Utc) last = last.ToUniversalTime();
        return (receipts.Count, new DateTimeOffset(last));
    }
}
