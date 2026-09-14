namespace AgentStudio.Tasks;

/// <summary>
/// Everything the completion contract needs to decide, collected by the
/// caller before the decision runs. Containment is a Git answer
/// (<see cref="IntegrationStatuses"/>), never a stored record: a card with no
/// integration record whose delivery is an ancestor of the integration branch
/// is integrated, and a card with a green record whose delivery is absent is
/// not.
/// </summary>
/// <param name="IntegrationRequired">
/// The card is expected to deliver code (<see cref="AcceptanceIntegrationPolicy.IsIntegrationRequired"/>).
/// </param>
/// <param name="HasAttributedCommits">The card carries at least one entry in <c>commits[]</c>.</param>
/// <param name="HasEffectiveCommits">
/// At least one attributed commit has no named successor
/// (<see cref="TaskCommitSupersession.IsEffectiveDelivery"/>). The
/// <c>next-attempt</c> placeholder keeps a commit effective: until a
/// replacement publishes it is still the only delivery the card has. False
/// with <paramref name="HasAttributedCommits"/> true is the contradiction
/// AGT-2795 and AGT-2743 exhibit: every attributed delivery was replaced and
/// the successor was never recorded.
/// </param>
/// <param name="ContainmentStatus">
/// Git-derived verdict for the effective delivery, one of
/// <see cref="IntegrationStatuses"/>. <see cref="ContainmentUnknown"/> means the
/// containment question could not be answered at all - it is never read as
/// "not integrated".
/// </param>
/// <param name="ContainmentCommitSha">Attributed SHA that proved containment, when integrated.</param>
/// <param name="IntegrationBranch">Branch the containment answer was computed against.</param>
/// <param name="OperatorOverride">The operator asked to complete against the contract.</param>
/// <param name="OverrideReason">Written reason supplied with the override.</param>
/// <param name="DeliverablePath">Repository-relative path of a named code-free deliverable.</param>
/// <param name="DeliverableKey">Dossier or workbench key of a named code-free deliverable.</param>
/// <param name="Actor">Who requested the move.</param>
public sealed record CompletionContractFacts(
    bool IntegrationRequired,
    bool HasAttributedCommits,
    bool HasEffectiveCommits,
    string? ContainmentStatus,
    string? ContainmentCommitSha = null,
    string? IntegrationBranch = null,
    bool OperatorOverride = false,
    string? OverrideReason = null,
    string? DeliverablePath = null,
    string? DeliverableKey = null,
    string? Actor = null);

/// <summary>Outcome of the completion contract for one move into <c>6-completed</c>.</summary>
public sealed record CompletionContractDecision(
    bool Accepted,
    TaskCompletionClaim? Claim = null,
    string? RefusalCode = null,
    string? Message = null);

/// <summary>
/// AGT-2817 - pure completion contract. A move into <c>6-completed</c> is
/// accepted when one of three grounds holds, and the card records which one:
/// a contained delivery, a named deliverable without code, or an operator
/// override carrying a written reason. Nothing else completes a card.
///
/// <para>
/// The contract is deliberately blind to lane history, pipeline verdicts, and
/// stored integration records. Those are caches; the inputs here are the
/// containment answer and what the card declares. AGT-2795 sat in the
/// delivered lane with a superseded-only delivery and no integration record,
/// and AGT-2706 was reported as "pending" purely because its record was
/// missing - both are decided correctly from these facts alone.
/// </para>
/// </summary>
public static class CompletionContractPolicy
{
    /// <summary>
    /// The containment question has not been answered. Distinct from
    /// <see cref="IntegrationStatuses.Pending"/>, which is a verdict.
    /// </summary>
    public const string ContainmentUnknown = "unknown";

    /// <summary>Shortest reason text the contract accepts for an override.</summary>
    public const int MinimumOverrideReasonLength = 8;

    public static CompletionContractDecision Decide(CompletionContractFacts facts)
    {
        var reason = Trimmed(facts.OverrideReason);
        if (facts.OperatorOverride && !IsUsableReason(reason))
        {
            return Refuse(
                CompletionRefusalCodes.OverrideWithoutReason,
                "Completing this task against the contract needs a written reason of at least "
                + $"{MinimumOverrideReasonLength} characters. It is stored on the card and shown "
                + "wherever the card claims completion.");
        }

        // Containment outranks every other ground, including an override the
        // operator did not need: the truthful claim is the integrated one.
        if (IsContained(facts.ContainmentStatus))
        {
            return Accept(new TaskCompletionClaim
            {
                Basis = CompletionClaimBases.IntegratedDelivery,
                Evidence = ContainmentEvidence(facts),
                CommitSha = Trimmed(facts.ContainmentCommitSha),
                IntegrationBranch = Trimmed(facts.IntegrationBranch),
                Actor = Trimmed(facts.Actor),
            });
        }

        // A card whose every attributed delivery is superseded and whose
        // successor was never recorded claims a delivery that does not exist.
        if (facts.HasAttributedCommits && !facts.HasEffectiveCommits)
        {
            return facts.OperatorOverride
                ? Accept(Override(facts, reason!))
                : Refuse(
                    CompletionRefusalCodes.SupersededOnlyDelivery,
                    "Every delivery attributed to this task is marked superseded and no successor is "
                    + "recorded, so the card would claim a delivery that does not exist. Record the "
                    + "successor attempt, or complete it with a written reason.");
        }

        if (!facts.IntegrationRequired)
        {
            var path = Trimmed(facts.DeliverablePath);
            var key = Trimmed(facts.DeliverableKey);
            if (path is not null || key is not null)
            {
                return Accept(new TaskCompletionClaim
                {
                    Basis = CompletionClaimBases.DeliverableWithoutCode,
                    Evidence = DeliverableEvidence(path, key),
                    DeliverablePath = path,
                    DeliverableKey = key,
                    Actor = Trimmed(facts.Actor),
                });
            }

            return facts.OperatorOverride
                ? Accept(Override(facts, reason!))
                : Refuse(
                    CompletionRefusalCodes.UndeclaredDeliverable,
                    "This task delivers no code, so completion needs the deliverable it produced to be "
                    + "named - for a concept card the Dossier path and key. Name it, or complete it "
                    + "with a written reason.");
        }

        return facts.OperatorOverride
            ? Accept(Override(facts, reason!))
            : Refuse(
                CompletionRefusalCodes.UnintegratedDelivery,
                UnintegratedMessage(facts));
    }

    /// <summary>
    /// True when the Git answer proves the effective delivery is in the
    /// integration branch. Everything else - including
    /// <see cref="ContainmentUnknown"/> and a null - is "not proven", which is
    /// not the same as "not integrated" and never carries that wording.
    /// </summary>
    public static bool IsContained(string? containmentStatus)
        => string.Equals(containmentStatus, IntegrationStatuses.Integrated, StringComparison.Ordinal);

    /// <summary>
    /// True when the card has nothing to integrate, so the containment
    /// question has no subject. Used by callers that must not present a
    /// no-branch card as a pending integration.
    /// </summary>
    public static bool HasNothingToIntegrate(string? containmentStatus)
        => string.Equals(containmentStatus, IntegrationStatuses.NoBranch, StringComparison.Ordinal);

    /// <summary>True when the containment question has not been answered yet.</summary>
    public static bool IsUnknown(string? containmentStatus)
        => string.IsNullOrWhiteSpace(containmentStatus)
           || string.Equals(containmentStatus, ContainmentUnknown, StringComparison.Ordinal);

    internal static bool IsUsableReason(string? reason)
        => reason is not null && reason.Length >= MinimumOverrideReasonLength;

    private static TaskCompletionClaim Override(CompletionContractFacts facts, string reason)
        => new()
        {
            Basis = CompletionClaimBases.OperatorOverride,
            Evidence = "Completed by operator override; the delivery was not proven to be contained in "
                + (Trimmed(facts.IntegrationBranch) ?? "the integration branch") + ".",
            Reason = reason,
            IntegrationBranch = Trimmed(facts.IntegrationBranch),
            Actor = Trimmed(facts.Actor),
        };

    private static string ContainmentEvidence(CompletionContractFacts facts)
    {
        var branch = Trimmed(facts.IntegrationBranch) ?? "the integration branch";
        var sha = Trimmed(facts.ContainmentCommitSha);
        return sha is null
            ? $"The delivery is contained in {branch}."
            : $"Delivery {sha} is contained in {branch}.";
    }

    private static string DeliverableEvidence(string? path, string? key)
    {
        if (path is not null && key is not null) return $"Deliverable without code: {path} ({key}).";
        return path is not null
            ? $"Deliverable without code: {path}."
            : $"Deliverable without code: {key}.";
    }

    private static string UnintegratedMessage(CompletionContractFacts facts)
    {
        var branch = Trimmed(facts.IntegrationBranch) ?? "the integration branch";
        if (IsUnknown(facts.ContainmentStatus))
        {
            return $"Whether this delivery is contained in {branch} could not be determined, so the card "
                + "cannot claim completion yet. Retry once the repository is reachable, or complete it "
                + "with a written reason.";
        }

        if (HasNothingToIntegrate(facts.ContainmentStatus))
        {
            return "This task expects code but has no delivery to integrate. Attribute its delivery, "
                + "declare the deliverable it produced instead, or complete it with a written reason.";
        }

        return $"This delivery is not contained in {branch} ({facts.ContainmentStatus}). Integrate it, "
            + "or complete it with a written reason that is stored on the card.";
    }

    private static CompletionContractDecision Accept(TaskCompletionClaim claim)
        => new(true, claim);

    private static CompletionContractDecision Refuse(string code, string message)
        => new(false, null, code, message);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
