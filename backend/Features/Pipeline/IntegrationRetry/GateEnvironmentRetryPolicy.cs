namespace AgentStudio.Pipeline;

/// <summary>
/// What the gate-environment retry rail should do with one card on this tick.
/// </summary>
public enum GateEnvironmentRetryAction
{
    /// <summary>The card is not a candidate; leave it alone.</summary>
    Ignore,
    /// <summary>A candidate whose next bounded attempt is not due yet.</summary>
    Wait,
    /// <summary>Re-run the integration for the already reviewed delivery.</summary>
    Retry,
    /// <summary>The bounded budget is exhausted; record the parked reason once.</summary>
    Park,
}

/// <summary>
/// Everything the retry decision reads, lifted off disk by the caller so the
/// decision itself stays a pure function (AGT-2824).
/// </summary>
/// <param name="TaskState">Lane the card sits in right now.</param>
/// <param name="FailureCode">
/// Failure code of the card's latest integration attempt, as projected by
/// <see cref="AcceptedIntegrationFailurePolicy"/>.
/// </param>
/// <param name="LatestReviewPassed">
/// True when the card's newest terminal review attempt ended in Pass. The rail
/// exists to reuse that verdict; without it a retry would integrate unreviewed
/// work.
/// </param>
/// <param name="DeliverySha">
/// Result SHA of the reviewed delivery. The retry budget is scoped to it: a new
/// delivery is a new subject and starts over with a full budget.
/// </param>
/// <param name="ReviewedSha">
/// Result SHA the passed review actually graded. It must equal
/// <paramref name="DeliverySha"/>, otherwise the passed verdict does not cover
/// what a retry would merge.
/// </param>
/// <param name="FailedAtUtc">
/// When the integration attempt that produced <paramref name="FailureCode"/>
/// finished. Anchors the first backoff window.
/// </param>
/// <param name="Ledger">Durable retry bookkeeping for this card, or null when none exists yet.</param>
public sealed record GateEnvironmentRetryState(
    string? TaskState,
    string? FailureCode,
    bool LatestReviewPassed,
    string? DeliverySha,
    string? ReviewedSha,
    DateTimeOffset? FailedAtUtc,
    IntegrationRetryLedgerRecord? Ledger);

/// <param name="AttemptNumber">1-based number of the attempt this decision authorizes (0 when none).</param>
/// <param name="DueAtUtc">When the next attempt becomes due; set for <see cref="GateEnvironmentRetryAction.Wait"/>.</param>
public sealed record GateEnvironmentRetryDecision(
    GateEnvironmentRetryAction Action,
    string Reason,
    int AttemptNumber = 0,
    DateTimeOffset? DueAtUtc = null);

/// <summary>
/// Pure policy for the bounded automatic retry of an integration that failed
/// with <see cref="AcceptedIntegrationFailureCodes.GateEnvironmentFailure"/>
/// after the card's review already passed (AGT-2824).
///
/// A gate environment failure is a host fault: the toolchain crashed before
/// verification reached test discovery, so neither the delivery nor a fresh
/// review can change the outcome. Before this rail the only operator path was
/// a complete new remote review, which costs a review slot for 30+ minutes and
/// re-grades work that already passed. The rail instead replays the integration
/// for the same delivery SHA, on a bounded backoff, and parks the card with a
/// named reason once the budget is spent.
/// </summary>
public static class GateEnvironmentRetryPolicy
{
    /// <summary>
    /// Delay before attempt 1, 2 and 3. Measured from the failure that opened
    /// the window for the first attempt, and from the previous attempt after
    /// that. Bounded on purpose: a host that is still broken after ~65 minutes
    /// needs an operator, not another sweep.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(45),
    ];

    /// <summary>Number of automatic attempts the rail may spend per delivery SHA.</summary>
    public static int MaxAutomaticAttempts => Backoff.Count;

    /// <summary>
    /// Operator copy for a card that exhausted <see cref="MaxAutomaticAttempts"/>.
    /// Names the environment failure so the park is never read as a product
    /// failure of the delivery.
    /// </summary>
    public static string ParkedReason(string? failureDetail) =>
        $"The build/test gate failed before verification reached test discovery on all "
        + $"{MaxAutomaticAttempts} automatic integration retries "
        + $"({string.Join(", ", Backoff.Select(FormatDelay))} after the first failure). "
        + "This is a gate environment failure, not a delivery failure: the passed review still "
        + "stands. Repair the gate host and use \"Retry integration\" - a new review is not needed."
        + (string.IsNullOrWhiteSpace(failureDetail) ? string.Empty : $" Last gate reason: {failureDetail.Trim()}");

    /// <summary>
    /// Admission for the operator's "Retry integration": identical candidate
    /// rules, but the backoff window and an already spent budget are the
    /// operator's to override. A human who repaired the gate host holds the one
    /// piece of information the timer does not have.
    /// </summary>
    public static GateEnvironmentRetryDecision DecideOperatorRetry(GateEnvironmentRetryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Decide(
            state with { Ledger = null, FailedAtUtc = DateTimeOffset.MinValue },
            DateTimeOffset.MaxValue);
    }

    public static GateEnvironmentRetryDecision Decide(
        GateEnvironmentRetryState state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.Equals(state.TaskState, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase))
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                $"The rail only drives cards parked in {TaskStates.HumanReview}.");
        }

        if (!string.Equals(
                state.FailureCode,
                AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
                StringComparison.OrdinalIgnoreCase))
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The latest integration attempt is not a gate environment failure.");
        }

        if (!state.LatestReviewPassed)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The card has no passed review to reuse, so integration must not be replayed.");
        }

        if (string.IsNullOrWhiteSpace(state.DeliverySha))
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The card has no reviewed delivery SHA to replay.");
        }

        if (!string.Equals(state.ReviewedSha, state.DeliverySha, StringComparison.OrdinalIgnoreCase))
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The passed review graded a different delivery SHA than the card now carries.");
        }

        // A ledger from an earlier delivery describes a subject that no longer
        // exists. The new delivery is a new subject and gets the full budget.
        var ledger = state.Ledger is { } existing
                     && string.Equals(existing.DeliverySha, state.DeliverySha, StringComparison.OrdinalIgnoreCase)
            ? existing
            : null;

        if (ledger?.Parked == true)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The bounded automatic retries are already exhausted and the card is parked.");
        }

        var attemptsMade = Math.Max(0, ledger?.Attempts ?? 0);
        if (attemptsMade >= MaxAutomaticAttempts)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Park,
                $"All {MaxAutomaticAttempts} automatic integration retries hit the same gate environment failure.",
                attemptsMade);
        }

        var anchor = attemptsMade == 0
            ? state.FailedAtUtc
            : ledger?.LastAttemptAtUtc ?? state.FailedAtUtc;
        if (anchor is null)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Ignore,
                "The gate environment failure has no recorded time to schedule a retry from.");
        }

        var dueAt = anchor.Value.ToUniversalTime() + Backoff[attemptsMade];
        var attemptNumber = attemptsMade + 1;
        if (nowUtc.ToUniversalTime() < dueAt)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Wait,
                $"Retry {attemptNumber}/{MaxAutomaticAttempts} is due at {dueAt:O}.",
                attemptNumber,
                dueAt);
        }

        return new GateEnvironmentRetryDecision(
            GateEnvironmentRetryAction.Retry,
            $"Retry {attemptNumber}/{MaxAutomaticAttempts} of the integration for the already reviewed delivery.",
            attemptNumber,
            dueAt);
    }

    private static string FormatDelay(TimeSpan delay)
        => delay.TotalMinutes >= 1
            ? $"{delay.TotalMinutes:0} min"
            : $"{delay.TotalSeconds:0} s";
}
