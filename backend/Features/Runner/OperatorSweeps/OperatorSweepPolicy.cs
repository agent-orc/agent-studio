namespace AgentStudio.Runner;

/// <summary>
/// The three supervision sweeps that used to run from an operator shell loop
/// outside the product (AGT-3011). Their ids are part of the API route and of
/// the persisted pause state, so they never change.
/// </summary>
public static class OperatorSweepKinds
{
    /// <summary>A fresh <c>ProductFailure</c> review in Human Review becomes a fix round.</summary>
    public const string FixRounds = "fix-rounds";

    /// <summary>A failed merge gate is classified; a product failure becomes a fix round.</summary>
    public const string GateTriage = "gate-triage";

    /// <summary>A timed-out run parked with a salvage commit continues from that commit.</summary>
    public const string Salvage = "salvage";

    public static readonly IReadOnlyList<string> All = [FixRounds, GateTriage, Salvage];

    public static string? Normalize(string? value)
        => All.FirstOrDefault(kind => string.Equals(kind, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a sweep does with one card on one tick.</summary>
public enum OperatorSweepAction
{
    /// <summary>The card is not this sweep's business; nothing is recorded for it.</summary>
    Ignore,

    /// <summary>The sweep is entitled to act but holds off for a stated reason; it re-evaluates next tick.</summary>
    Hold,

    /// <summary>Open the round (fix round or salvage continuation).</summary>
    Act,

    /// <summary>The card needs a person; the sweep will not act on it again by itself.</summary>
    WaitForPerson,
}

/// <summary>Stable reason tokens. The UI and the logs show these verbatim.</summary>
public static class OperatorSweepReasons
{
    public const string NotApplicable = "not-applicable";
    public const string Paused = "sweep-paused";
    public const string ProjectContinuationsDisabled = "automatic-continuations-disabled";
    public const string ActiveReviewAttempt = "active-review-attempt";
    public const string ActiveRunAttempt = "active-run-attempt";
    public const string AlreadyHandled = "already-handled";
    public const string BudgetExhausted = "round-budget-exhausted";
    public const string EnvironmentFailure = "environment-failure-owned-by-gate-retry";
    public const string UnclassifiedFailure = "gate-failure-unclassified";
    public const string FreshProductFailure = "fresh-product-failure";
    public const string ProductGateFailure = "product-gate-failure";
    public const string SalvageAvailable = "timed-out-with-salvage";
    public const string AnotherSweepActed = "another-sweep-acted";
    public const string JournalUnavailable = "decision-journal-unavailable";
    public const string ActionFailed = "action-failed";
    public const string EvaluationFailed = "evaluation-failed";

    public static string Explain(string reason) => reason switch
    {
        NotApplicable => "The card is not in a state this sweep handles.",
        Paused => "The sweep is paused for this project.",
        ProjectContinuationsDisabled => "Automatic failure continuations are disabled for this project.",
        ActiveReviewAttempt => "A review attempt is pending or holds a lease; the sweep never races it.",
        ActiveRunAttempt => "A run attempt is pending or holds a lease.",
        AlreadyHandled => "The sweep already opened a round for this exact failure.",
        BudgetExhausted => "The card has used its whole round budget; a person decides the next step.",
        EnvironmentFailure => "The gate failed for environmental reasons; the gate-environment retry ladder owns it.",
        UnclassifiedFailure => "The gate failure could not be classified as product or environment; a person decides.",
        FreshProductFailure => "A fresh ProductFailure review; a fix round was opened with its findings.",
        ProductGateFailure => "The merge gate failed on the delivery itself; a fix round was opened.",
        SalvageAvailable => "The run timed out with a salvage commit; it continues from that commit.",
        AnotherSweepActed => "Another sweep already opened a round on this card in this tick.",
        JournalUnavailable => "The decision journal is not configured, so the round budget cannot be charged.",
        ActionFailed => "The round could not be opened; the sweep retries on the next tick.",
        EvaluationFailed => "The card's facts could not be read; the sweep retries on the next tick.",
        _ => reason,
    };
}

/// <summary>
/// The facts every sweep checks before its own trigger. Read once per card by
/// the service so the three sweeps cannot disagree about the same card.
/// </summary>
/// <param name="Paused">The sweep is paused for the card's project.</param>
/// <param name="AutomaticContinuationsEnabled">The project's existing automatic-continuation switch.</param>
/// <param name="HasActiveReviewAttempt">Attempt authority shows a <c>Pending</c> or <c>Leased</c> review attempt.</param>
/// <param name="HasActiveRunAttempt">Attempt authority shows a <c>Pending</c> or <c>Leased</c> run attempt.</param>
/// <param name="Budget">The card's shared round budget.</param>
public sealed record OperatorSweepGuardFacts(
    bool Paused,
    bool AutomaticContinuationsEnabled,
    bool HasActiveReviewAttempt,
    bool HasActiveRunAttempt,
    CardRoundBudgetState Budget);

/// <summary>The trigger a sweep found on the card, or null when there is none.</summary>
/// <param name="SubjectKey">
/// Identity of the exact failure (review attempt id, delivery SHA plus stage,
/// salvage commit). One round per subject: the receipt makes the sweep
/// idempotent across ticks and restarts.
/// </param>
/// <param name="AlreadyHandled">A receipt for <paramref name="SubjectKey"/> already exists.</param>
/// <param name="FailureDomain">
/// Gate triage only: <c>product</c>, <c>infrastructure</c>, or null when the
/// product's classifier had no deterministic answer.
/// </param>
public sealed record OperatorSweepTrigger(
    string SubjectKey,
    bool AlreadyHandled,
    string? FailureDomain = null);

public sealed record OperatorSweepDecision(OperatorSweepAction Action, string Reason)
{
    public static OperatorSweepDecision Ignore { get; } = new(OperatorSweepAction.Ignore, OperatorSweepReasons.NotApplicable);
}

/// <summary>
/// Pure decision for one card and one sweep. Guard order is load-bearing:
/// <list type="number">
/// <item>no trigger: ignore (silent, no receipt);</item>
/// <item>an active review or run attempt always holds, even when paused, so
/// the projection says the real blocker;</item>
/// <item>pause and the project switch hold;</item>
/// <item>a subject already handled holds;</item>
/// <item>a gate environment failure belongs to the existing retry ladder, an
/// unclassified one to a person;</item>
/// <item>an exhausted shared budget waits for a person;</item>
/// <item>otherwise act.</item>
/// </list>
/// </summary>
public static class OperatorSweepPolicy
{
    public static OperatorSweepDecision Decide(
        string sweep,
        OperatorSweepTrigger? trigger,
        OperatorSweepGuardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (trigger is null) return OperatorSweepDecision.Ignore;

        if (facts.HasActiveReviewAttempt)
            return Hold(OperatorSweepReasons.ActiveReviewAttempt);
        if (facts.HasActiveRunAttempt)
            return Hold(OperatorSweepReasons.ActiveRunAttempt);
        if (facts.Paused)
            return Hold(OperatorSweepReasons.Paused);
        if (!facts.AutomaticContinuationsEnabled)
            return Hold(OperatorSweepReasons.ProjectContinuationsDisabled);
        if (trigger.AlreadyHandled)
            return Hold(OperatorSweepReasons.AlreadyHandled);

        if (sweep == OperatorSweepKinds.GateTriage)
        {
            if (string.Equals(trigger.FailureDomain, AgentStudio.Pipeline.FailureDomains.Infrastructure,
                    StringComparison.Ordinal))
                return Hold(OperatorSweepReasons.EnvironmentFailure);
            if (!string.Equals(trigger.FailureDomain, AgentStudio.Pipeline.FailureDomains.Product,
                    StringComparison.Ordinal))
                return new(OperatorSweepAction.WaitForPerson, OperatorSweepReasons.UnclassifiedFailure);
        }

        if (facts.Budget.Exhausted)
            return new(OperatorSweepAction.WaitForPerson, OperatorSweepReasons.BudgetExhausted);

        return new(OperatorSweepAction.Act, sweep switch
        {
            OperatorSweepKinds.FixRounds => OperatorSweepReasons.FreshProductFailure,
            OperatorSweepKinds.GateTriage => OperatorSweepReasons.ProductGateFailure,
            OperatorSweepKinds.Salvage => OperatorSweepReasons.SalvageAvailable,
            _ => throw new ArgumentOutOfRangeException(nameof(sweep), sweep, "Unknown operator sweep."),
        });
    }

    private static OperatorSweepDecision Hold(string reason) => new(OperatorSweepAction.Hold, reason);
}
