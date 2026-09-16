namespace AgentStudio.Tasks;

/// <summary>Containment and supersession state of one attributed commit.</summary>
/// <param name="Sha">Full attributed SHA.</param>
/// <param name="Contained">The commit is an ancestor of the integration branch.</param>
/// <param name="SupersessionState">One of <see cref="CommitSupersessionStates"/>.</param>
/// <param name="CarriesFiles">
/// The commit changed at least one file. Zero-file runner lifecycle markers are
/// not delivery expectations and never make a card look unintegrated.
/// </param>
public sealed record DeliveryClaimCommitFact(
    string Sha,
    bool Contained,
    string SupersessionState,
    bool CarriesFiles);

/// <summary>Everything the sweep needs about one delivered card.</summary>
public sealed record DeliveryClaimCardFacts(
    bool IntegrationRequired,
    IReadOnlyList<DeliveryClaimCommitFact> Commits,
    string? ContainmentStatus,
    bool HasIntegrationRecord,
    bool HasNamedDeliverable);

/// <summary>What the reconciliation pass may repair for one card.</summary>
/// <param name="AppendIntegrationRecord">
/// Containment proves the delivery landed, but no integration record exists.
/// The repair writes the record; it never invents a merge commit it cannot
/// name.
/// </param>
/// <param name="ClearPendingSupersession">
/// Contained commits still carry the <c>next-attempt</c> placeholder. The
/// repair clears it; a named successor is never touched.
/// </param>
public sealed record DeliveryClaimRepairPlan(
    bool AppendIntegrationRecord,
    bool ClearPendingSupersession)
{
    public bool HasWork => AppendIntegrationRecord || ClearPendingSupersession;
}

/// <summary>Verdict for one card: its class, what contradicts it, and what can be repaired.</summary>
public sealed record DeliveryClaimAssessment(
    string Class,
    IReadOnlyList<string> Findings,
    DeliveryClaimRepairPlan Repairs);

/// <summary>Healthy and unhealthy classes a swept card falls into.</summary>
public static class DeliveryClaimClasses
{
    /// <summary>Every effective delivery is contained in the integration branch.</summary>
    public const string IntegratedDelivery = "integrated-delivery";

    /// <summary>No code expected and a deliverable is named.</summary>
    public const string DeliverableWithoutCode = "deliverable-without-code";

    /// <summary>Code expected, an effective delivery exists, and it is not contained.</summary>
    public const string UnintegratedDelivery = "unintegrated-delivery";

    /// <summary>Every attributed delivery is marked replaced and no successor is recorded.</summary>
    public const string SupersededOnlyDelivery = "superseded-only-delivery";

    /// <summary>Code expected, no delivery attributed, and no deliverable named.</summary>
    public const string NothingClaimed = "nothing-claimed";

    /// <summary>The containment question could not be answered for this card.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Contradictions the sweep reports per card.</summary>
public static class DeliveryClaimFindings
{
    /// <summary>The card's effective delivery is not in the integration branch.</summary>
    public const string UnintegratedDelivery = "unintegrated-delivery";

    /// <summary>The only attributed delivery is superseded; the successor was never recorded.</summary>
    public const string SupersededOnlyDelivery = "superseded-only-delivery";

    /// <summary>
    /// An earlier, uncontained delivery sits next to a contained one without
    /// being marked superseded, so the card attributes deliveries that were
    /// replaced (AGT-2744).
    /// </summary>
    public const string UnmarkedReplacedDelivery = "unmarked-replaced-delivery";

    /// <summary>The delivery is contained but the card carries no integration record (AGT-2706).</summary>
    public const string MissingIntegrationRecord = "missing-integration-record";

    /// <summary>
    /// A contained commit still carries the <c>next-attempt</c> placeholder, so
    /// every consumer that reads supersession as a verdict sees the shipped
    /// delivery as replaced (AGT-2706).
    /// </summary>
    public const string StalePendingSupersession = "stale-pending-supersession";

    /// <summary>Code expected, nothing attributed, nothing declared.</summary>
    public const string NothingClaimed = "nothing-claimed";
}

/// <summary>
/// AGT-2817 - pure per-card reconciliation policy for the delivery-claim
/// sweep. Containment is the only proof of integration; the stored record and
/// the supersession marker are caches, and this policy reports where those
/// caches contradict the Git answer.
///
/// <para>
/// It never rewrites history it cannot prove: a repair is proposed only when
/// containment is positive, which is exactly the direction in which the cache
/// can be wrong without losing information.
/// </para>
/// </summary>
public static class DeliveryClaimSweepPolicy
{
    public static DeliveryClaimAssessment Assess(DeliveryClaimCardFacts facts)
    {
        var attributed = facts.Commits
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha))
            .ToList();
        var effective = attributed
            .Where(commit => !string.Equals(
                commit.SupersessionState,
                CommitSupersessionStates.Replaced,
                StringComparison.Ordinal))
            .ToList();
        var expectations = effective.Where(commit => commit.CarriesFiles).ToList();

        var findings = new List<string>();
        // Only a live attribution documents the card's current delivery. A
        // contained commit that already has a named successor is history, and
        // is not evidence that the card's delivery landed.
        var containedExists = effective.Any(commit => commit.Contained);

        if (attributed.Count > 0 && effective.Count == 0)
            findings.Add(DeliveryClaimFindings.SupersededOnlyDelivery);

        if (containedExists && !facts.HasIntegrationRecord)
            findings.Add(DeliveryClaimFindings.MissingIntegrationRecord);

        var stalePlaceholder = effective.Any(commit => commit.Contained
            && string.Equals(
                commit.SupersessionState,
                CommitSupersessionStates.ReplacementPending,
                StringComparison.Ordinal));
        if (stalePlaceholder)
            findings.Add(DeliveryClaimFindings.StalePendingSupersession);

        // AGT-2744's shape: a delivery that never landed, retained as an
        // active attribution beside one that did. The contained delivery is
        // what shipped; the uncontained sibling is a replaced round that was
        // never marked.
        if (containedExists && expectations.Any(commit => !commit.Contained))
            findings.Add(DeliveryClaimFindings.UnmarkedReplacedDelivery);

        var repairs = new DeliveryClaimRepairPlan(
            AppendIntegrationRecord: containedExists && !facts.HasIntegrationRecord,
            ClearPendingSupersession: stalePlaceholder);

        var cardClass = ClassifyCard(facts, expectations, attributed.Count, effective.Count, findings);
        return new DeliveryClaimAssessment(cardClass, findings, repairs);
    }

    private static string ClassifyCard(
        DeliveryClaimCardFacts facts,
        IReadOnlyList<DeliveryClaimCommitFact> expectations,
        int attributedCount,
        int effectiveCount,
        List<string> findings)
    {
        if (attributedCount > 0 && effectiveCount == 0)
            return DeliveryClaimClasses.SupersededOnlyDelivery;

        if (expectations.Count == 0)
        {
            if (!facts.IntegrationRequired)
            {
                return facts.HasNamedDeliverable
                    ? DeliveryClaimClasses.DeliverableWithoutCode
                    : Nothing(findings);
            }

            if (CompletionContractPolicy.IsContained(facts.ContainmentStatus))
                return DeliveryClaimClasses.IntegratedDelivery;
            if (facts.HasNamedDeliverable)
                return DeliveryClaimClasses.DeliverableWithoutCode;
            return Nothing(findings);
        }

        if (expectations.All(commit => commit.Contained))
            return DeliveryClaimClasses.IntegratedDelivery;

        if (CompletionContractPolicy.IsUnknown(facts.ContainmentStatus))
            return DeliveryClaimClasses.Unknown;

        findings.Add(DeliveryClaimFindings.UnintegratedDelivery);
        return DeliveryClaimClasses.UnintegratedDelivery;
    }

    private static string Nothing(List<string> findings)
    {
        findings.Add(DeliveryClaimFindings.NothingClaimed);
        return DeliveryClaimClasses.NothingClaimed;
    }
}
