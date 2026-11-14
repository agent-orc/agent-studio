using System.Text.Json.Serialization;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// The step a batch flight is in. Each step has its own progress deadline;
/// the numeric values are persisted in the flight claim record, so append new
/// steps at the end.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchFlightStage>))]
public enum BatchFlightStage
{
    /// <summary>The flight claimed the batch and waits for the project reservation.</summary>
    Reserving,

    /// <summary>The flight holds the reservation and takes its lease, replays and assembles the candidate.</summary>
    Assembling,

    /// <summary>A gate runs on the candidate (the batch gate, a retry, a base check or an isolation child).</summary>
    Gating,

    /// <summary>The flight publishes a green candidate and releases its members.</summary>
    Publishing,
}

/// <summary>Why a flight claim is no longer live.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchFlightClaimExpiry>))]
public enum BatchFlightClaimExpiry
{
    /// <summary>The owner stopped renewing the claim: its process died or froze.</summary>
    HeartbeatLost,

    /// <summary>The owner renews the claim, but its current step made no progress in time.</summary>
    StageDeadlinePassed,
}

/// <summary>What an expired claim does with its batch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchFlightExpiryOutcome>))]
public enum BatchFlightExpiryOutcome
{
    /// <summary>
    /// A formed batch stays formed and is retried after the next formed batch;
    /// a batch past assembly is retired and its members return to the queue.
    /// </summary>
    Retry,

    /// <summary>The batch pauses with a visible reason; the pause hands its members to the per-task gate.</summary>
    VisibleGate,

    /// <summary>
    /// The claim layer does not touch the batch: restart recovery owns it
    /// (a formed batch starts again, a later phase is abandoned, a publication
    /// is reconstructed from its exact remote SHA).
    /// </summary>
    Recover,
}

/// <summary>
/// Facts of the durable flight claim of one scope at <paramref name="NowUtc"/>.
/// <paramref name="Ended"/> is true once the claim was released or expired;
/// <paramref name="Settled"/> is true once an expired claim's outcome was
/// applied to its batch (a released claim is always settled).
/// <paramref name="OwnerGone"/> is true when the host knows the owning
/// process no longer runs (a restart), so its heartbeat need not lapse first.
/// </summary>
public sealed record BatchFlightClaimFacts(
    BatchFlightStage Stage, DateTimeOffset StageDeadlineUtc, DateTimeOffset HeartbeatExpiresAtUtc,
    bool Ended, bool Settled, DateTimeOffset NowUtc, bool OwnerGone = false);

/// <summary>A claim request: granted, or refused with a reason the caller logs.</summary>
public sealed record BatchFlightClaimDecision(bool Granted, string? Refusal);

/// <summary>
/// AGT-3569: the durable claim a batch flight holds on its scope. A flight
/// claims its formed batch before it waits for the project reservation, so
/// the right to run a batch is a durable fact with a heartbeat and a progress
/// deadline per step instead of a process task. At most one live claim exists
/// per scope across processes; a claim that lost its heartbeat or missed its
/// step deadline expires to a retry, a visible gate or restart recovery, and
/// the scope is claimable again once that outcome is applied.
/// </summary>
public static class BatchFlightClaimPolicy
{
    public const string Held = "claim-held";
    public const string AwaitingSettlement = "expired-claim-awaiting-settlement";

    /// <summary>Timeline event kind of a claim that expired after its reservation step.</summary>
    public const string ExpiredEventKind = "batch-flight-claim-expired";

    /// <summary>Lifetime of one heartbeat, as for the coordinator lease.</summary>
    public static readonly TimeSpan HeartbeatTtl = TimeSpan.FromMinutes(2);

    /// <summary>Deadline of replay and assembly after the reservation.</summary>
    public const int AssemblingDeadlineMinutes = 30;

    /// <summary>Time a gate gets beyond its own timeout before its flight expires.</summary>
    public const int GatingGraceMinutes = 15;

    /// <summary>Deadline of the publication step.</summary>
    public const int PublishingDeadlineMinutes = 15;

    /// <summary>
    /// Expiries a member's batches may reach before the next one pauses to a
    /// visible gate. One retry, then the operator sees the batch.
    /// </summary>
    public const int RetryBudget = 1;

    /// <summary>
    /// Null while the claim is live (or already ended); otherwise why it is not.
    /// A passed step deadline names the expiry even when the heartbeat lapsed
    /// too, so a step that hung longer than its deadline always counts against
    /// the retry budget.
    /// </summary>
    public static BatchFlightClaimExpiry? Expiry(BatchFlightClaimFacts claim)
    {
        if (claim.Ended) return null;
        if (claim.NowUtc >= claim.StageDeadlineUtc) return BatchFlightClaimExpiry.StageDeadlinePassed;
        if (claim.OwnerGone || claim.NowUtc >= claim.HeartbeatExpiresAtUtc) return BatchFlightClaimExpiry.HeartbeatLost;
        return null;
    }

    /// <summary>True while the claim gives its owner the right to run and mutate its batch.</summary>
    public static bool IsLive(BatchFlightClaimFacts claim) => !claim.Ended && Expiry(claim) is null;

    /// <summary>
    /// A new claim on a scope. Null <paramref name="current"/> is a scope that
    /// was never claimed. An ended claim frees the scope once settled; a claim
    /// that is no longer live but not yet ended must be expired first, so its
    /// outcome reaches its batch before another batch flies.
    /// </summary>
    public static BatchFlightClaimDecision Decide(BatchFlightClaimFacts? current)
    {
        if (current is null) return new(true, null);
        if (current.Ended) return current.Settled ? new(true, null) : new(false, AwaitingSettlement);
        return Expiry(current) is null ? new(false, Held) : new(false, AwaitingSettlement);
    }

    /// <summary>
    /// The heartbeat renews a live claim only: a step past its deadline is not
    /// extended by a heartbeat, so a hung step cannot keep its claim.
    /// </summary>
    public static bool Renews(BatchFlightClaimFacts claim) => IsLive(claim);

    /// <summary>
    /// What an expired claim does with its batch. A lost heartbeat leaves the
    /// batch to restart recovery, as a crash does. A missed deadline before
    /// publication retries while the members' earlier expiries stay within
    /// <see cref="RetryBudget"/>, then pauses to a visible gate. A publication
    /// is never retried or paused by the claim layer: only its exact-SHA
    /// recovery may decide whether the candidate reached the remote.
    /// </summary>
    public static BatchFlightExpiryOutcome Outcome(BatchFlightStage stage, BatchFlightClaimExpiry expiry,
        int priorExpiries)
    {
        if (expiry == BatchFlightClaimExpiry.HeartbeatLost || stage == BatchFlightStage.Publishing)
            return BatchFlightExpiryOutcome.Recover;
        return priorExpiries < RetryBudget ? BatchFlightExpiryOutcome.Retry : BatchFlightExpiryOutcome.VisibleGate;
    }

    /// <summary>
    /// Progress deadline of <paramref name="stage"/> entered at <paramref name="nowUtc"/>.
    /// The reservation step keeps the AGT-3562 flight lease deadline; a gate
    /// step gets the gate timeout plus <see cref="GatingGraceMinutes"/>.
    /// </summary>
    public static DateTimeOffset StageDeadline(BatchFlightStage stage, DateTimeOffset nowUtc,
        int reservationDeadlineMinutes, int gateTimeoutMinutes)
        => nowUtc + TimeSpan.FromMinutes(DeadlineMinutes(stage, reservationDeadlineMinutes, gateTimeoutMinutes));

    public static int DeadlineMinutes(BatchFlightStage stage, int reservationDeadlineMinutes, int gateTimeoutMinutes)
        => stage switch
        {
            BatchFlightStage.Reserving => Math.Max(1, reservationDeadlineMinutes),
            BatchFlightStage.Assembling => AssemblingDeadlineMinutes,
            BatchFlightStage.Gating => Math.Max(1, gateTimeoutMinutes) + GatingGraceMinutes,
            _ => PublishingDeadlineMinutes,
        };

    /// <summary>
    /// The visible phase reason of an expired claim. A formed batch retried
    /// after its reservation deadline keeps the AGT-3562 wording.
    /// </summary>
    public static string ExpiryReason(BatchFlightStage stage, BatchFlightClaimExpiry expiry,
        BatchFlightExpiryOutcome outcome, int deadlineMinutes)
    {
        if (stage == BatchFlightStage.Reserving && expiry == BatchFlightClaimExpiry.StageDeadlinePassed
            && outcome == BatchFlightExpiryOutcome.Retry)
            return BatchGateStartPolicy.LeaseDeadlineReason(deadlineMinutes);
        var cause = expiry == BatchFlightClaimExpiry.HeartbeatLost
            ? "the flight owner stopped renewing its claim"
            : $"no progress in the {StageName(stage)} step within {deadlineMinutes} minutes";
        return outcome switch
        {
            BatchFlightExpiryOutcome.Retry => $"flight claim expired: {cause}; the members return to the queue",
            BatchFlightExpiryOutcome.VisibleGate =>
                $"flight claim expired: {cause}; retry budget spent, the members go to the per-task gate",
            _ => $"flight claim expired: {cause}; restart recovery takes over the batch",
        };
    }

    private static string StageName(BatchFlightStage stage) => stage switch
    {
        BatchFlightStage.Reserving => "reservation",
        BatchFlightStage.Assembling => "assembly",
        BatchFlightStage.Gating => "gate",
        _ => "publication",
    };
}
