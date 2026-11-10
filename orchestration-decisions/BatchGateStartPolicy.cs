namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Facts of one formed batch that waits for its project's flight slot
/// (AGT-3562, AGT-3710). <paramref name="QueuedSinceUtc"/> is the durable
/// formation time of the batch: when the first batch of its re-form or repack
/// chain closed, so a re-formed batch keeps its place.
/// <paramref name="FirstEnqueueSequence"/> is the lowest durable enqueue
/// sequence of its members. <paramref name="Child"/> marks an isolation,
/// bisection or successor child that continues its parent's flight.
/// <paramref name="DeferredAfterLeaseDeadline"/> marks the batch
/// whose last flight was cancelled before its coordinator lease, so the next
/// formed batch takes the slot first.
/// </summary>
public sealed record BatchGateStartCandidate(
    string BatchId, DateTimeOffset QueuedSinceUtc, long FirstEnqueueSequence, bool Stacked,
    bool Child, bool DeferredAfterLeaseDeadline);

/// <summary>
/// Start order and pre-lease deadline of batch flights (AGT-3562). Only one
/// flight per project runs, so the order decides which formed batch waits.
/// </summary>
public static class BatchGateStartPolicy
{
    /// <summary>
    /// Default maximum wait of a formed batch: the 45-minute planning deadline.
    /// Since AGT-3710 the start order is first in, first out, so this value no
    /// longer reorders batches; it remains a validated project setting.
    /// </summary>
    public const int DefaultMaximumWaitMinutes = 45;

    /// <summary>
    /// Default bound for a flight before its coordinator lease. It exceeds the
    /// 27-minute integration gate lease cap, so a flight that waits behind a
    /// direct integration is not cancelled.
    /// </summary>
    public const int DefaultFlightLeaseDeadlineMinutes = 40;

    /// <summary>Timeline event kind of a flight cancelled before its coordinator lease.</summary>
    public const string LeaseDeadlineEventKind = "batch-flight-lease-deadline";

    /// <summary>
    /// Orders formed batches for start, first in, first out (AGT-3710). A
    /// stacked successor has already claimed the next place in the train, and
    /// a child continues its parent's flight, so both start first. A batch
    /// deferred after a lease deadline follows the others. All other batches
    /// start in formation order: the oldest <see cref="BatchGateStartCandidate.QueuedSinceUtc"/>
    /// first, then the lowest member enqueue sequence, then the batch id.
    /// Size never reorders the queue, and the order never depends on how the
    /// store enumerates its folders.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<BatchGateStartCandidate> formed)
        => formed
            .OrderByDescending(batch => batch.Stacked)
            .ThenByDescending(batch => batch.Child)
            .ThenBy(batch => batch.DeferredAfterLeaseDeadline)
            .ThenBy(batch => batch.QueuedSinceUtc)
            .ThenBy(batch => batch.FirstEnqueueSequence)
            .ThenBy(batch => batch.BatchId, StringComparer.Ordinal)
            .Select(batch => batch.BatchId).ToArray();

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

    /// <summary>Visible phase reason of a formed batch whose flight was cancelled before its lease.</summary>
    public static string LeaseDeadlineReason(int deadlineMinutes)
        => $"flight cancelled: no coordinator lease within {Math.Max(1, deadlineMinutes)} minutes;"
            + " the batch stays formed and is retried after the next formed batch";
}
