namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Facts of one formed batch that waits for its project's flight slot
/// (AGT-3562). <paramref name="ClosedAtUtc"/> is when the first batch of its
/// re-form chain closed, so a re-formed batch keeps its wait.
/// <paramref name="DeferredAfterLeaseDeadline"/> marks the batch
/// whose last flight was cancelled before its coordinator lease, so the next
/// formed batch takes the slot first.
/// </summary>
public sealed record BatchGateStartCandidate(
    string BatchId, int MemberCount, DateTimeOffset ClosedAtUtc, bool Stacked,
    bool DeferredAfterLeaseDeadline, int MaximumWaitMinutes);

/// <summary>
/// Start order and pre-lease deadline of batch flights (AGT-3562). Only one
/// flight per project runs, so the order decides which formed batch waits.
/// </summary>
public static class BatchGateStartPolicy
{
    /// <summary>Default maximum wait of a formed batch: the 45-minute planning deadline.</summary>
    public const int DefaultMaximumWaitMinutes = 45;

    /// <summary>
    /// Default bound for a flight before its coordinator lease. It exceeds the
    /// 27-minute integration gate lease cap, so a flight that waits behind a
    /// direct integration is not cancelled.
    /// </summary>
    public const int DefaultFlightLeaseDeadlineMinutes = 40;

    /// <summary>Timeline event kind of a flight cancelled before its coordinator lease.</summary>
    public const string LeaseDeadlineEventKind = "batch-flight-lease-deadline";

    /// <summary>True once a formed batch has waited its maximum wait since it closed.</summary>
    public static bool Overdue(BatchGateStartCandidate batch, DateTimeOffset nowUtc)
        => nowUtc - batch.ClosedAtUtc >= TimeSpan.FromMinutes(Math.Max(0, batch.MaximumWaitMinutes));

    /// <summary>
    /// Orders formed batches for start. A stacked successor has already claimed
    /// the next place in the train. A batch deferred after a lease deadline
    /// follows the others. A batch past its maximum wait starts before younger
    /// ones, oldest first, regardless of size. Within the maximum wait the
    /// larger batch starts first, then the older one.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<BatchGateStartCandidate> formed,
        DateTimeOffset nowUtc)
        => formed.Select(batch => (Batch: batch, Overdue: Overdue(batch, nowUtc)))
            .OrderByDescending(item => item.Batch.Stacked)
            .ThenBy(item => item.Batch.DeferredAfterLeaseDeadline)
            .ThenByDescending(item => item.Overdue)
            .ThenBy(item => item.Overdue ? item.Batch.ClosedAtUtc : DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Batch.MemberCount)
            .ThenBy(item => item.Batch.ClosedAtUtc)
            .ThenBy(item => item.Batch.BatchId, StringComparer.Ordinal)
            .Select(item => item.Batch.BatchId).ToArray();

    /// <summary>
    /// True when a flight that has not reached its coordinator lease has run
    /// for the deadline. The host then cancels it and frees the project slot.
    /// </summary>
    public static bool LeaseDeadlinePassed(DateTimeOffset startedAtUtc, bool leaseClaimed,
        DateTimeOffset nowUtc, int deadlineMinutes)
        => !leaseClaimed && nowUtc - startedAtUtc >= TimeSpan.FromMinutes(Math.Max(1, deadlineMinutes));

    /// <summary>
    /// True when a finished flight starts the next formed batch of its project
    /// at once. A flight that completed or failed qualifies: its batch ran a
    /// gate, or left the formed phase other than by retirement before replay.
    /// A publication moves the base of the next formed batch, so a flight that
    /// a completed flight started and that re-formed its batch on the moved
    /// base also qualifies; each such step needs a real base move. A flight
    /// cancelled at its lease deadline, refused a lease, or otherwise retired
    /// before replay leaves the start to the next tick, so a refusal cannot loop.
    /// </summary>
    public static bool StartsNextAfterFlight(bool cancelledAtLeaseDeadline, bool batchStillFormed,
        bool batchRetired, bool gateRan, bool reformedIntoSuccessor = false,
        bool followsCompletedFlight = false)
        => !cancelledAtLeaseDeadline && !batchStillFormed
            && (gateRan || !batchRetired || reformedIntoSuccessor && followsCompletedFlight);

    /// <summary>
    /// A red parent without a live flight needs a terminal recovery state
    /// unless every member has an isolation finding and none remains pending.
    /// </summary>
    public static bool EndInterruptedIsolation(bool red, bool flightActive,
        bool hasPendingMember, bool allMembersHaveFindings)
        => red && !flightActive && (hasPendingMember || !allMembersHaveFindings);

    /// <summary>Visible phase reason of a formed batch whose flight was cancelled before its lease.</summary>
    public static string LeaseDeadlineReason(int deadlineMinutes)
        => $"flight cancelled: no coordinator lease within {Math.Max(1, deadlineMinutes)} minutes;"
            + " the batch stays formed and is retried after the next formed batch";
}
