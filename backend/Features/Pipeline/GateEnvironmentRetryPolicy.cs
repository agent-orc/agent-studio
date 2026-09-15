namespace AgentStudio.Pipeline;

/// <summary>What the gate-environment retry rail does with one card right now.</summary>
public enum GateEnvironmentRetryAction
{
    /// <summary>Not a gate-environment retry candidate at all.</summary>
    Ignore,

    /// <summary>A candidate whose next backoff step has not elapsed yet.</summary>
    Wait,

    /// <summary>Replay the integration for the already reviewed delivery.</summary>
    Retry,

    /// <summary>The bounded retry budget is spent; record the parked reason once.</summary>
    Park,
}

/// <summary>Who asked for the decision. The operator path skips the backoff wait.</summary>
public enum GateEnvironmentRetryTrigger
{
    Sweep,
    Operator,
}

/// <summary>
/// Durable facts one card contributes to <see cref="GateEnvironmentRetryPolicy"/>.
/// Every field is read from disk evidence (pipeline step, timeline receipts,
/// review subject, attempt authority) so the decision itself stays pure.
/// </summary>
/// <param name="IntegrationRequired">Whether this acceptance expects an integration at all.</param>
/// <param name="AlreadyIntegrated">Git already proves the delivery is on the integration branch.</param>
/// <param name="FailureCode">Failure code of the latest integration attempt.</param>
/// <param name="FailedAtUtc">When that attempt failed; the anchor of the first backoff step.</param>
/// <param name="ReviewPassed">Whether the card's current review attempt settled with Pass.</param>
/// <param name="ReviewedResultSha">The SHA that passed review.</param>
/// <param name="DeliveryResultSha">The SHA the fenced delivery sidecar still names.</param>
/// <param name="CompletedRetries">Retry receipts already written for this delivery SHA.</param>
/// <param name="LastRetryAtUtc">Instant of the newest such receipt, or null when there is none.</param>
/// <param name="Parked">A parked receipt for this delivery SHA already exists.</param>
public sealed record GateEnvironmentRetryState(
    bool IntegrationRequired,
    bool AlreadyIntegrated,
    string? FailureCode,
    DateTimeOffset? FailedAtUtc,
    bool ReviewPassed,
    string? ReviewedResultSha,
    string? DeliveryResultSha,
    int CompletedRetries,
    DateTimeOffset? LastRetryAtUtc,
    bool Parked,
    GateEnvironmentRetryTrigger Trigger = GateEnvironmentRetryTrigger.Sweep);

/// <param name="AttemptNumber">1-based number of the retry this decision describes; 0 when none is due.</param>
/// <param name="DueAtUtc">When the next retry becomes due. Null outside <see cref="GateEnvironmentRetryAction.Wait"/>.</param>
public sealed record GateEnvironmentRetryDecision(
    GateEnvironmentRetryAction Action,
    int AttemptNumber,
    DateTimeOffset? DueAtUtc,
    string Reason);

/// <summary>
/// AGT-2824 - pure policy behind the healed-gate integration retry.
///
/// <para>A card whose review passed and whose integration then failed with
/// <see cref="AcceptedIntegrationFailureCodes.GateEnvironmentFailure"/> is not a
/// product failure: the build/test gate never reached verification, so the
/// delivery has nothing to fix and a fresh Remote Review would only re-prove a
/// verdict that already exists. The card therefore replays the merge for the
/// SAME delivery SHA on a bounded backoff, reusing the passed review, and parks
/// with a named reason once the budget is spent.</para>
///
/// <para>Deliberately narrow. Every other failure code, an unproven review, and a
/// delivery SHA that no longer matches the reviewed one fall through to
/// <see cref="GateEnvironmentRetryAction.Ignore"/> - those need the acceptance
/// rail, a steer round, or an operator, not a replay.</para>
/// </summary>
public static class GateEnvironmentRetryPolicy
{
    /// <summary>
    /// Bounded backoff before retry 1, 2, and 3. Long enough that a Windows gate
    /// host healing between sweeps is picked up without hammering it, short
    /// enough that the whole ladder ends inside an hour (AGT-2811..AGT-2813 sat
    /// unretried for longer than that).
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(45),
    ];

    /// <summary>Retries spent before the card parks with a named reason.</summary>
    public static int MaxRetries => Backoff.Count;

    public static GateEnvironmentRetryDecision Decide(
        GateEnvironmentRetryState state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.IntegrationRequired)
            return Ignore("This acceptance explicitly expects no integration.");
        // The failure code is checked first so the caller only has to pay for
        // the Git ancestry and authority reads behind the remaining fields when
        // the card is a candidate at all.
        if (!string.Equals(
                state.FailureCode,
                AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
                StringComparison.Ordinal))
        {
            return Ignore("The latest integration attempt is not a gate environment failure.");
        }
        if (state.AlreadyIntegrated)
            return Ignore("The delivery is already on the integration branch.");
        if (!state.ReviewPassed)
            return Ignore("The card has no passed review to reuse; a retry would need a new review.");
        if (!SameDelivery(state.ReviewedResultSha, state.DeliveryResultSha))
            return Ignore("The passed review does not describe the current delivery SHA.");

        var spent = Math.Max(0, state.CompletedRetries);
        if (state.Trigger == GateEnvironmentRetryTrigger.Operator)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Retry,
                spent + 1,
                null,
                "An operator asked to retry the integration for the reviewed delivery.");
        }

        if (state.Parked)
            return Ignore("The bounded retry budget is spent and the parked reason is recorded.");
        if (spent >= MaxRetries)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Park,
                spent,
                null,
                $"The build/test gate failed before verification in all {MaxRetries} automatic retries.");
        }

        var anchor = state.LastRetryAtUtc ?? state.FailedAtUtc;
        if (anchor is not { } since)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Retry,
                spent + 1,
                null,
                "The gate environment failure carries no timestamp, so its first retry is due now.");
        }

        var due = since + Backoff[spent];
        if (due > nowUtc)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Wait,
                spent + 1,
                due,
                $"Retry {spent + 1}/{MaxRetries} is due at {due:O}.");
        }

        return new GateEnvironmentRetryDecision(
            GateEnvironmentRetryAction.Retry,
            spent + 1,
            due,
            $"Retry {spent + 1}/{MaxRetries} of the healed gate is due.");
    }

    /// <summary>
    /// The reason the card shows once the budget is spent. Names the environment
    /// failure, the exhausted ladder, and the one action that still helps.
    /// </summary>
    public static string ParkedReason(string? gateReason)
    {
        var ladder = string.Join(
            ", ",
            Backoff.Select(step => $"{(int)step.TotalMinutes} min"));
        var evidence = string.IsNullOrWhiteSpace(gateReason)
            ? "The build/test gate failed before verification reached test discovery."
            : gateReason.Trim();
        return $"Gate environment failure: {evidence} "
               + $"All {MaxRetries} automatic integration retries ({ladder}) hit the same environment failure, "
               + "so the delivery is parked with its passed review intact. "
               + "Repair the gate host and use \"Retry integration\"; no new review is needed.";
    }

    private static bool SameDelivery(string? reviewed, string? delivered)
        => !string.IsNullOrWhiteSpace(reviewed)
           && !string.IsNullOrWhiteSpace(delivered)
           && string.Equals(reviewed.Trim(), delivered.Trim(), StringComparison.OrdinalIgnoreCase);

    private static GateEnvironmentRetryDecision Ignore(string reason)
        => new(GateEnvironmentRetryAction.Ignore, 0, null, reason);
}
